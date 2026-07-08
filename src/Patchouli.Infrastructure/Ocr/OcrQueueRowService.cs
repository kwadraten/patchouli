using Dapper;
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

    public async Task<Result<IReadOnlyList<OcrQueueRow>>> ListRowsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _scheduler.ListTasksAsync(new OcrQueueTaskFilter(), cancellationToken);
        if (tasks.IsFailure)
        {
            return Result<IReadOnlyList<OcrQueueRow>>.Failure(tasks.ErrorCode!, tasks.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var titles = (await connection.QueryAsync<Row>(
                """
                select di.document_instance_id as DocumentInstanceId,
                       i.title as ItemTitle
                from document_instances di
                join items i on i.item_id = di.item_id
                where di.document_instance_id in @Ids;
                """,
                new { Ids = tasks.Value.Select(task => task.DocumentInstanceId.ToString()).Distinct().ToArray() }))
                .ToDictionary(row => row.DocumentInstanceId, row => row.ItemTitle, StringComparer.Ordinal);

            return Result<IReadOnlyList<OcrQueueRow>>.Success(tasks.Value.Select(task =>
                new OcrQueueRow(
                    task.TaskId,
                    titles.GetValueOrDefault(task.DocumentInstanceId.ToString(), "(unknown item)"),
                    task.TaskKind,
                    task.State,
                    task.Priority,
                    task.EngineId,
                    task.PageIds.Count,
                    task.LastErrorCode)).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<OcrQueueRow>>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public Task<Result> PauseRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
        => _scheduler.PauseAsync(OcrPauseScope.Task, taskId.ToString(), cancellationToken);

    public Task<Result> ResumeRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
        => _scheduler.ResumeAsync(OcrPauseScope.Task, taskId.ToString(), cancellationToken);

    public Task<Result> CancelRowAsync(OcrQueueTaskId taskId, CancellationToken cancellationToken = default)
        => _scheduler.CancelTaskAsync(taskId, cancellationToken);

    private sealed class Row
    {
        public string DocumentInstanceId { get; set; } = "";
        public string ItemTitle { get; set; } = "";
    }
}
