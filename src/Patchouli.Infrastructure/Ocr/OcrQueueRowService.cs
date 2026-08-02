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
                            progress.Total));
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
}
