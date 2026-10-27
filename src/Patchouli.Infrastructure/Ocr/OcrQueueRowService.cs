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
        // The scheduler snapshot is taken before any database access; no queue state is touched
        // while a read connection is open, and every row is projected from this one snapshot.
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
                    new CommandDefinition(
                        """
                        select di.document_instance_id as DocumentInstanceId,
                               i.title as ItemTitle
                        from document_instances di
                        join items i on i.item_id = di.item_id
                        where di.document_instance_id in
                              (select value from json_each(@DocumentIdsJson));
                        """,
                        new
                        {
                            DocumentIdsJson = JsonSerializer.Serialize(tasks.Value
                                .Select(task => task.DocumentInstanceId.ToString()).Distinct())
                        },
                        cancellationToken: cancellationToken)))
                .ToDictionary(row => row.DocumentInstanceId, row => row.ItemTitle, StringComparer.Ordinal);

            string[] linkedRunIds = tasks.Value
                .Select(task => task.RunId?.ToString())
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string linkedRunIdsJson = JsonSerializer.Serialize(linkedRunIds);

            // Progress belongs only to the run a task explicitly links to. A queued task without
            // a RunId shows no page counts — it must not borrow progress from another run of the
            // same document. A retained-Run adoption/index retry keeps its own run's counters.
            Dictionary<string, OcrRunProgressRow> progressByRunId = linkedRunIds.Length == 0
                ? new Dictionary<string, OcrRunProgressRow>(StringComparer.Ordinal)
                : (await connection.QueryAsync<OcrRunProgressRow>(
                    new CommandDefinition(
                        """
                        select r.ocr_run_id as RunId,
                               sum(case when pr.state = 'succeeded' then 1 else 0 end) as Succeeded,
                               sum(case when pr.state in ('failed', 'skipped', 'cancelled') then 1 else 0 end)
                                   as Failed,
                               sum(case when pr.state = 'processing' then 1 else 0 end) as Processing,
                               count(pr.result_id) as Total
                        from json_each(@RunIdsJson) linked
                        join ocr_runs r on r.ocr_run_id = linked.value
                        left join ocr_page_results pr on pr.ocr_run_id = r.ocr_run_id
                        group by r.ocr_run_id;
                        """,
                        new { RunIdsJson = linkedRunIdsJson },
                        cancellationToken: cancellationToken)))
                .ToDictionary(row => row.RunId, StringComparer.Ordinal);

            Dictionary<string, OcrRunOutcomeRow> outcomeRows = await LoadOutcomeRowsAsync(
                connection, linkedRunIds, linkedRunIdsJson, cancellationToken);

            return Result<IReadOnlyList<OcrQueueRow>>.Success(tasks.Value.Select(task =>
            {
                string? runId = task.RunId?.ToString();
                OcrRunProgressRow? progress = runId is not null &&
                                              progressByRunId.TryGetValue(runId, out OcrRunProgressRow? row)
                    ? row
                    : null;
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
                    runId is not null && outcomeRows.TryGetValue(runId, out OcrRunOutcomeRow? outcome)
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
    ///     rebuild failed after the commit transaction. Collections travel as a single JSON
    ///     parameter joined through <c>json_each</c>: expanding every adopted revision id into
    ///     <c>IN</c> variables made parameter binding grow super-linearly on large histories.
    /// </summary>
    private static async Task<Dictionary<string, OcrRunOutcomeRow>> LoadOutcomeRowsAsync(
        SqliteConnection connection,
        string[] runIds,
        string runIdsJson,
        CancellationToken cancellationToken)
    {
        if (runIds.Length == 0)
        {
            return new Dictionary<string, OcrRunOutcomeRow>(StringComparer.Ordinal);
        }

        Dictionary<string, string> runStates = (await connection.QueryAsync<RunStateRow>(
                new CommandDefinition(
                    """
                    select ocr_run_id as RunId, state as State
                    from ocr_runs
                    where ocr_run_id in (select value from json_each(@RunIdsJson));
                    """,
                    new { RunIdsJson = runIdsJson },
                    cancellationToken: cancellationToken)))
            .ToDictionary(row => row.RunId, row => row.State, StringComparer.Ordinal);

        // A historical task's adopted body must not borrow the document's current index: if any
        // adopted revision was superseded by a newer commit (or no longer exists), the task's
        // index_state is reported as "superseded" instead of "indexed". Both the page count and
        // the superseded check are computed in SQL against json_each so no revision id crosses
        // the C# boundary. Malformed, non-array, or non-string-element JSON keeps the previous
        // tolerant "parse as empty" outcome instead of failing the whole projection.
        Dictionary<string, AdoptionRow> adoptions = new(StringComparer.Ordinal);
        foreach (AdoptionRow adoption in await connection.QueryAsync<AdoptionRow>(
                     new CommandDefinition(
                         """
                         select a.ocr_run_id as RunId,
                                a.document_instance_id as DocumentInstanceId,
                                case
                                    when json_valid(a.adopted_pages_json) = 1
                                         and json_type(case when json_valid(a.adopted_pages_json)
                                                            then a.adopted_pages_json
                                                            else '[]' end) = 'array'
                                         and not exists (select 1
                                                         from json_each(
                                                             case when json_valid(a.adopted_pages_json)
                                                                  then a.adopted_pages_json
                                                                  else '[]' end) e
                                                         where e.type not in ('text', 'null'))
                                    then json_array_length(a.adopted_pages_json)
                                    else 0
                                end as CommittedPageCount,
                                case
                                    when json_valid(a.adopted_tree_revisions_json) = 1
                                         and json_type(case when json_valid(a.adopted_tree_revisions_json)
                                                            then a.adopted_tree_revisions_json
                                                            else '[]' end) = 'array'
                                         and not exists (select 1
                                                         from json_each(
                                                             case when json_valid(a.adopted_tree_revisions_json)
                                                                  then a.adopted_tree_revisions_json
                                                                  else '[]' end) e
                                                         where e.type not in ('text', 'null'))
                                         and exists (select 1
                                                     from json_each(
                                                         case when json_valid(a.adopted_tree_revisions_json)
                                                              then a.adopted_tree_revisions_json
                                                              else '[]' end) e
                                                     left join document_tree_revisions r
                                                         on r.tree_revision_id = e.value
                                                     where r.is_current is null or r.is_current = 0)
                                    then 1
                                    else 0
                                end as Superseded
                         from ocr_candidate_adoptions a
                         where a.ocr_run_id in (select value from json_each(@RunIdsJson))
                         order by a.rowid;
                         """,
                         new { RunIdsJson = runIdsJson },
                         cancellationToken: cancellationToken)))
        {
            // A run normally commits once; if several adoption rows exist the latest inserted row
            // wins, matching the previous dictionary-overwrite behaviour but deterministically.
            adoptions[adoption.RunId] = adoption;
        }

        string[] committedDocumentIds = adoptions.Values
            .Select(adoption => adoption.DocumentInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, string> indexStates = committedDocumentIds.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await connection.QueryAsync<IndexStateRow>(
                new CommandDefinition(
                    """
                    select scope_id as ScopeId, status as Status
                    from search_index_status
                    where scope_type = 'document_instance'
                      and scope_id in (select value from json_each(@ScopeIdsJson));
                    """,
                    new { ScopeIdsJson = JsonSerializer.Serialize(committedDocumentIds) },
                    cancellationToken: cancellationToken)))
            .ToDictionary(row => row.ScopeId, row => row.Status, StringComparer.Ordinal);

        Dictionary<string, OcrRunOutcomeRow> outcomes = new(StringComparer.Ordinal);
        foreach (string runId in runIds)
        {
            if (!runStates.TryGetValue(runId, out string? runState))
            {
                continue;
            }

            adoptions.TryGetValue(runId, out AdoptionRow? adoption);
            outcomes[runId] = new OcrRunOutcomeRow(
                runState,
                adoption?.CommittedPageCount ?? 0,
                adoption is null ? null : indexStates.GetValueOrDefault(adoption.DocumentInstanceId),
                adoption?.Superseded ?? false);
        }

        return outcomes;
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
        public string DocumentInstanceId { get; set; } = "";
        public int CommittedPageCount { get; set; }
        public bool Superseded { get; set; }
    }

    private sealed class IndexStateRow
    {
        public string ScopeId { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed record OcrRunOutcomeRow(
        string? RunState,
        int CommittedPageCount,
        string? IndexState,
        bool Superseded);
}
