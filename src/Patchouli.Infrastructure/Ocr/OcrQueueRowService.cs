using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Ocr;

public sealed class OcrQueueRowService : IOcrQueueRowService
{
    private readonly IOcrQueueScheduler _scheduler;
    private readonly SqliteConnectionFactory _connectionFactory;

    public OcrQueueRowService(IOcrQueueScheduler scheduler, SqliteConnectionFactory connectionFactory)
    {
        _scheduler = scheduler;
        _connectionFactory = connectionFactory;
    }

    public async Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsAsync(bool includeCompleted = false,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<OcrQueueTask>> tasks =
            await _scheduler.ListTasksAsync(new OcrQueueTaskFilter(IncludeCompleted: includeCompleted),
                cancellationToken);
        if (tasks.IsFailure)
        {
            return Result<IReadOnlyList<OcrQueueRow>>.Failure(tasks.ErrorCode!, tasks.ErrorMessage!);
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            Dictionary<string, string> titles = (await connection.QueryAsync<Row>(
                    """
                    select di.document_instance_id as DocumentInstanceId,
                           i.title as ItemTitle
                    from document_instances di
                    join items i on i.item_id = di.item_id
                    where di.document_instance_id in @Ids;
                    """,
                    new { Ids = tasks.Value.Select(task => task.DocumentInstanceId.ToString()).Distinct().ToArray() }))
                .ToDictionary(row => row.DocumentInstanceId, row => row.ItemTitle, StringComparer.Ordinal);

            string[] runningDocumentIds = tasks.Value
                .Where(task => task.State == OcrQueueTaskState.Running || task.RunId is not null)
                .Select(task => task.DocumentInstanceId.ToString())
                .Distinct()
                .ToArray();
            OcrRunProgressRow[] progressRows = runningDocumentIds.Length == 0
                ? []
                : (await connection.QueryAsync<OcrRunProgressRow>(
                    """
                    select r.ocr_run_id as RunId,
                           r.document_instance_id as DocumentInstanceId,
                           sum(case when pr.state = 'succeeded' then 1 else 0 end) as Succeeded,
                           sum(case when pr.state in ('failed', 'skipped', 'cancelled') then 1 else 0 end) as Failed,
                           sum(case when pr.state = 'processing' then 1 else 0 end) as Processing,
                           count(pr.result_id) as Total
                    from ocr_runs r
                    left join ocr_page_results pr on pr.ocr_run_id = r.ocr_run_id
                    where r.document_instance_id in @DocumentIds
                    group by r.ocr_run_id, r.document_instance_id, r.updated_at
                    order by r.updated_at desc;
                    """,
                    new { DocumentIds = runningDocumentIds })).ToArray();

            Dictionary<string, OcrRunOutcomeRow> outcomeRows = await LoadOutcomeRowsAsync(
                connection, tasks.Value, cancellationToken);

            return Result<IReadOnlyList<OcrQueueRow>>.Success(tasks.Value.Select(task =>
            {
                OcrRunProgressRow? progress = progressRows.FirstOrDefault(row =>
                    task.RunId?.ToString() == row.RunId ||
                    (task.RunId is null && task.DocumentInstanceId.ToString() == row.DocumentInstanceId));
                return new OcrQueueRow(
                    task.TaskId,
                    titles.GetValueOrDefault(task.DocumentInstanceId.ToString(), "(unknown item)"),
                    task.TaskKind,
                    task.State,
                    task.Priority,
                    task.EngineId,
                    task.PageIds.Count,
                    task.LastErrorCode,
                    task,
                    progress is null
                        ? null
                        : new OcrQueueProgress(progress.Succeeded, progress.Failed, progress.Processing,
                            progress.Total),
                    task.RunId is { } runId && outcomeRows.TryGetValue(runId.ToString(), out OcrRunOutcomeRow? outcome)
                        ? new OcrQueueOutcome(
                            outcome.RunState,
                            outcome.CommittedPageCount > 0,
                            outcome.CommittedPageCount,
                            outcome.CommittedPageCount > 0
                                ? outcome.Superseded
                                    ? "superseded"
                                    : outcome.IndexState is null
                                        ? "not_indexed"
                                        : outcome.IndexState == "current"
                                            ? "indexed"
                                            : outcome.IndexState
                                : null)
                        : null);
            }).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.ocr-queue-row"))
        {
            return Result<IReadOnlyList<OcrQueueRow>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    /// <summary>
    ///     Loads the durable per-run truth (run state, candidate adoption, document index status)
    ///     that the volatile queue state cannot express — notably a committed body whose index
    ///     rebuild failed after the commit transaction.
    /// </summary>
    private static async Task<Dictionary<string, OcrRunOutcomeRow>> LoadOutcomeRowsAsync(
        SqliteConnection connection,
        IReadOnlyList<OcrQueueTask> tasks,
        CancellationToken cancellationToken)
    {
        string[] runIds = tasks.Select(task => task.RunId?.ToString())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runIds.Length == 0)
        {
            return new Dictionary<string, OcrRunOutcomeRow>(StringComparer.Ordinal);
        }

        Dictionary<string, string> runStates = (await connection.QueryAsync<RunStateRow>(
                """
                select ocr_run_id as RunId, state as State
                from ocr_runs
                where ocr_run_id in @RunIds;
                """,
                new { RunIds = runIds }))
            .ToDictionary(row => row.RunId, row => row.State, StringComparer.Ordinal);

        Dictionary<string, AdoptionRow> adoptions = new(StringComparer.Ordinal);
        foreach (AdoptionRow adoption in await connection.QueryAsync<AdoptionRow>(
                     """
                     select ocr_run_id as RunId, adopted_pages_json as PagesJson,
                            adopted_tree_revisions_json as RevisionsJson
                     from ocr_candidate_adoptions
                     where ocr_run_id in @RunIds;
                     """,
                     new { RunIds = runIds }))
        {
            adoptions[adoption.RunId] = adoption;
        }

        Dictionary<string, int> committedPages = adoptions.ToDictionary(
            pair => pair.Key,
            pair => CountCommittedPages(pair.Value.PagesJson),
            StringComparer.Ordinal);

        // A historical task's adopted body must not borrow the document's current index: if any
        // adopted revision was superseded by a newer commit, the task's index_state is reported
        // as "superseded" instead of "indexed".
        HashSet<string> currentRevisionIds = new(StringComparer.Ordinal);
        string[] adoptedRevisionIds = adoptions.Values
            .SelectMany(adoption => ParseRevisionIds(adoption.RevisionsJson))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (adoptedRevisionIds.Length > 0)
        {
            foreach (RevisionCurrentRow row in await connection.QueryAsync<RevisionCurrentRow>(
                         """
                         select tree_revision_id as TreeRevisionId
                         from document_tree_revisions
                         where tree_revision_id in @Ids and is_current = 1;
                         """,
                         new { Ids = adoptedRevisionIds }))
            {
                currentRevisionIds.Add(row.TreeRevisionId);
            }
        }

        Dictionary<string, bool> supersededRuns = adoptions.ToDictionary(
            pair => pair.Key,
            pair => ParseRevisionIds(pair.Value.RevisionsJson)
                .Any(id => !currentRevisionIds.Contains(id)),
            StringComparer.Ordinal);

        string[] committedDocumentIds = tasks
            .Where(task => task.RunId is { } runId &&
                           committedPages.ContainsKey(runId.ToString()))
            .Select(task => task.DocumentInstanceId.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, string> indexStates = committedDocumentIds.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await connection.QueryAsync<IndexStateRow>(
                """
                select scope_id as ScopeId, status as Status
                from search_index_status
                where scope_type = 'document_instance' and scope_id in @ScopeIds;
                """,
                new { ScopeIds = committedDocumentIds }))
            .ToDictionary(row => row.ScopeId, row => row.Status, StringComparer.Ordinal);

        Dictionary<string, OcrRunOutcomeRow> outcomes = new(StringComparer.Ordinal);
        foreach (string runId in runIds)
        {
            string? documentId = tasks.FirstOrDefault(task => task.RunId?.ToString() == runId)
                ?.DocumentInstanceId.ToString();
            outcomes[runId] = new OcrRunOutcomeRow(
                runStates.GetValueOrDefault(runId),
                committedPages.GetValueOrDefault(runId),
                documentId is not null ? indexStates.GetValueOrDefault(documentId) : null,
                supersededRuns.GetValueOrDefault(runId));
        }

        return outcomes;
    }

    private static IReadOnlyList<string> ParseRevisionIds(string? revisionsJson)
    {
        if (string.IsNullOrWhiteSpace(revisionsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(revisionsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int CountCommittedPages(string? pagesJson)
    {
        if (string.IsNullOrWhiteSpace(pagesJson))
        {
            return 0;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(pagesJson)?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public Task<Result> PauseRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
    {
        return _scheduler.PauseAsync(OcrPauseScope.Task, taskId.ToString(), cancellationToken);
    }

    public Task<Result> ResumeRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
    {
        return _scheduler.ResumeAsync(OcrPauseScope.Task, taskId.ToString(), cancellationToken);
    }

    public Task<Result> CancelRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
    {
        return _scheduler.CancelTaskAsync(taskId, cancellationToken);
    }

    private sealed class Row
    {
        public string DocumentInstanceId { get; set; } = "";
        public string ItemTitle { get; set; } = "";
    }

    private sealed class OcrRunProgressRow
    {
        public string RunId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Processing { get; set; }
        public int Total { get; set; }
    }

    private sealed class RunStateRow
    {
        public string RunId { get; set; } = "";
        public string State { get; set; } = "";
    }

    private sealed class AdoptionRow
    {
        public string RunId { get; set; } = "";
        public string? PagesJson { get; set; }
        public string? RevisionsJson { get; set; }
    }

    private sealed class IndexStateRow
    {
        public string ScopeId { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed class RevisionCurrentRow
    {
        public string TreeRevisionId { get; set; } = "";
    }

    private sealed record OcrRunOutcomeRow(
        string? RunState,
        int CommittedPageCount,
        string? IndexState,
        bool Superseded);
}
