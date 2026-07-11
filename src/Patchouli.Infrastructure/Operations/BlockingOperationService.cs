using System.Text.Json;
using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Operations;

public sealed class BlockingOperationService : IBlockingOperationService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public BlockingOperationService(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result<BlockingOperation>> StartAsync(
        string operationType,
        string scopeType,
        string? scopeId = null,
        bool canCancel = false,
        string? progressLabel = null,
        int? progressCurrent = null,
        int? progressTotal = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateStart(operationType, scopeType, progressCurrent, progressTotal);
        if (validation.IsFailure)
        {
            return Result<BlockingOperation>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            var now = _clock.UtcNow.ToUniversalTime();
            var operation = new BlockingOperation(
                BlockingOperationId.New(),
                operationType.Trim(),
                scopeType.Trim(),
                NullIfWhiteSpace(scopeId),
                BlockingOperationStatus.Running,
                progressCurrent,
                progressTotal,
                NullIfWhiteSpace(progressLabel),
                canCancel,
                null,
                null,
                CleanActions(nextActions),
                now,
                now);

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await InsertOperationAsync(connection, transaction, operation);
            await InsertLogEntryAsync(
                connection,
                transaction,
                new BlockingOperationLogEntry(
                    BlockingOperationLogEntryId.New(),
                    operation.OperationId,
                    BlockingOperationLogLevel.Info,
                    "Blocking operation started.",
                    operation.ProgressLabel,
                    operation.ScopeType,
                    operation.ScopeId,
                    now));
            await transaction.CommitAsync(cancellationToken);
            return Result<BlockingOperation>.Success(operation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<BlockingOperation>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<BlockingOperation>> UpdateProgressAsync(
        BlockingOperationId operationId,
        int? progressCurrent = null,
        int? progressTotal = null,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateProgress(progressCurrent, progressTotal);
        if (validation.IsFailure)
        {
            return Result<BlockingOperation>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var current = await GetRowAsync(connection, transaction, operationId);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.NotFound, "Blocking operation was not found.");
            }

            if (BlockingOperationStatus.IsTerminal(current.Status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.InvalidState, "Terminal blocking operations cannot update progress.");
            }

            var updated = current.ToModel() with
            {
                ProgressCurrent = progressCurrent ?? current.ProgressCurrent,
                ProgressTotal = progressTotal ?? current.ProgressTotal,
                ProgressLabel = NullIfWhiteSpace(progressLabel) ?? current.ProgressLabel,
                NextActions = nextActions is null ? current.ToModel().NextActions : CleanActions(nextActions),
                UpdatedAt = _clock.UtcNow.ToUniversalTime()
            };

            await UpdateOperationAsync(connection, transaction, updated);
            await transaction.CommitAsync(cancellationToken);
            return Result<BlockingOperation>.Success(updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<BlockingOperation>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<BlockingOperation>> CompleteAsync(
        BlockingOperationId operationId,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default)
    {
        return await TransitionAsync(
            operationId,
            BlockingOperationStatus.Completed,
            null,
            null,
            progressLabel,
            nextActions,
            BlockingOperationLogLevel.Info,
            "Blocking operation completed.",
            cancellationToken);
    }

    public async Task<Result<BlockingOperation>> FailAsync(
        BlockingOperationId operationId,
        string failureCode,
        string failureMessage,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(failureCode) || string.IsNullOrWhiteSpace(failureMessage))
        {
            return Result<BlockingOperation>.Failure(AppErrorCodes.ValidationFailed, "Failure code and failure message are required.");
        }

        return await TransitionAsync(
            operationId,
            BlockingOperationStatus.Failed,
            failureCode.Trim(),
            failureMessage.Trim(),
            progressLabel,
            nextActions,
            BlockingOperationLogLevel.Error,
            failureMessage.Trim(),
            cancellationToken);
    }

    public async Task<Result<BlockingOperation>> CancelAsync(
        BlockingOperationId operationId,
        string? progressLabel = null,
        IReadOnlyList<string>? nextActions = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var current = await GetRowAsync(connection, transaction, operationId);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.NotFound, "Blocking operation was not found.");
            }

            if (current.CanCancel == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.InvalidState, "This blocking operation cannot be cancelled.");
            }

            if (BlockingOperationStatus.IsTerminal(current.Status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.InvalidState, "Terminal blocking operations cannot be cancelled.");
            }

            var updated = current.ToModel() with
            {
                Status = BlockingOperationStatus.Cancelled,
                ProgressLabel = NullIfWhiteSpace(progressLabel) ?? current.ProgressLabel,
                NextActions = nextActions is null ? current.ToModel().NextActions : CleanActions(nextActions),
                UpdatedAt = _clock.UtcNow.ToUniversalTime()
            };

            await UpdateOperationAsync(connection, transaction, updated);
            await InsertLogEntryAsync(
                connection,
                transaction,
                new BlockingOperationLogEntry(
                    BlockingOperationLogEntryId.New(),
                    updated.OperationId,
                    BlockingOperationLogLevel.Warning,
                    "Blocking operation cancelled.",
                    updated.ProgressLabel,
                    updated.ScopeType,
                    updated.ScopeId,
                    updated.UpdatedAt));
            await transaction.CommitAsync(cancellationToken);
            return Result<BlockingOperation>.Success(updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<BlockingOperation>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<BlockingOperation>> GetAsync(BlockingOperationId operationId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await GetRowAsync(connection, null, operationId);
            return row is null
                ? Result<BlockingOperation>.Failure(AppErrorCodes.NotFound, "Blocking operation was not found.")
                : Result<BlockingOperation>.Success(row.ToModel());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<BlockingOperation>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<BlockingOperation>>> ListAsync(
        string? status = null,
        string? operationType = null,
        string? scopeType = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<Row>(
                """
                select operation_id as OperationId,
                       operation_type as OperationType,
                       scope_type as ScopeType,
                       scope_id as ScopeId,
                       status as Status,
                       progress_current as ProgressCurrent,
                       progress_total as ProgressTotal,
                       progress_label as ProgressLabel,
                       can_cancel as CanCancel,
                       failure_code as FailureCode,
                       failure_message as FailureMessage,
                       next_actions_json as NextActionsJson,
                       created_at as CreatedAt,
                       updated_at as UpdatedAt
                from blocking_operations
                where (@Status is null or status = @Status)
                  and (@OperationType is null or operation_type = @OperationType)
                  and (@ScopeType is null or scope_type = @ScopeType)
                  and (@ScopeId is null or scope_id = @ScopeId)
                order by created_at desc;
                """,
                new
                {
                    Status = NullIfWhiteSpace(status),
                    OperationType = NullIfWhiteSpace(operationType),
                    ScopeType = NullIfWhiteSpace(scopeType),
                    ScopeId = NullIfWhiteSpace(scopeId)
                });
            return Result<IReadOnlyList<BlockingOperation>>.Success(rows.Select(row => row.ToModel()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<IReadOnlyList<BlockingOperation>>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<BlockingOperationLogEntry>> AddLogEntryAsync(
        BlockingOperationId operationId,
        string level,
        string message,
        string? detail = null,
        string? scopeType = null,
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(level) || string.IsNullOrWhiteSpace(message))
        {
            return Result<BlockingOperationLogEntry>.Failure(AppErrorCodes.ValidationFailed, "Log level and message are required.");
        }

        try
        {
            var operation = await GetAsync(operationId, cancellationToken);
            if (operation.IsFailure)
            {
                return Result<BlockingOperationLogEntry>.Failure(operation.ErrorCode!, operation.ErrorMessage!);
            }

            var entry = new BlockingOperationLogEntry(
                BlockingOperationLogEntryId.New(),
                operationId,
                level.Trim(),
                message.Trim(),
                NullIfWhiteSpace(detail),
                NullIfWhiteSpace(scopeType),
                NullIfWhiteSpace(scopeId),
                _clock.UtcNow.ToUniversalTime());

            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await InsertLogEntryAsync(connection, null, entry);
            return Result<BlockingOperationLogEntry>.Success(entry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<BlockingOperationLogEntry>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<BlockingOperationLogEntry>>> GetLogEntriesAsync(
        BlockingOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<LogRow>(
                """
                select entry_id as EntryId,
                       operation_id as OperationId,
                       level as Level,
                       message as Message,
                       detail as Detail,
                       scope_type as ScopeType,
                       scope_id as ScopeId,
                       created_at as CreatedAt
                from blocking_operation_log_entries
                where operation_id = @OperationId
                order by created_at, rowid;
                """,
                new { OperationId = operationId.ToString() });
            return Result<IReadOnlyList<BlockingOperationLogEntry>>.Success(rows.Select(row => row.ToModel()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<IReadOnlyList<BlockingOperationLogEntry>>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result<BlockingOperation>> TransitionAsync(
        BlockingOperationId operationId,
        string targetStatus,
        string? failureCode,
        string? failureMessage,
        string? progressLabel,
        IReadOnlyList<string>? nextActions,
        string logLevel,
        string logMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var current = await GetRowAsync(connection, transaction, operationId);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.NotFound, "Blocking operation was not found.");
            }

            if (BlockingOperationStatus.IsTerminal(current.Status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<BlockingOperation>.Failure(AppErrorCodes.InvalidState, "Terminal blocking operations cannot transition again.");
            }

            var updated = current.ToModel() with
            {
                Status = targetStatus,
                FailureCode = failureCode,
                FailureMessage = failureMessage,
                ProgressLabel = NullIfWhiteSpace(progressLabel) ?? current.ProgressLabel,
                NextActions = nextActions is null ? current.ToModel().NextActions : CleanActions(nextActions),
                UpdatedAt = _clock.UtcNow.ToUniversalTime()
            };

            await UpdateOperationAsync(connection, transaction, updated);
            await InsertLogEntryAsync(
                connection,
                transaction,
                new BlockingOperationLogEntry(
                    BlockingOperationLogEntryId.New(),
                    updated.OperationId,
                    logLevel,
                    logMessage,
                    failureMessage,
                    updated.ScopeType,
                    updated.ScopeId,
                    updated.UpdatedAt));
            await transaction.CommitAsync(cancellationToken);
            return Result<BlockingOperation>.Success(updated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.blocking-operation"))
        {
            return Result<BlockingOperation>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private static Result ValidateStart(string operationType, string scopeType, int? progressCurrent, int? progressTotal)
    {
        if (string.IsNullOrWhiteSpace(operationType) || string.IsNullOrWhiteSpace(scopeType))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Operation type and scope type are required.");
        }

        return ValidateProgress(progressCurrent, progressTotal);
    }

    private static Result ValidateProgress(int? progressCurrent, int? progressTotal)
    {
        if (progressCurrent is < 0 || progressTotal is < 0)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Progress values must be zero or greater.");
        }

        if (progressCurrent is not null && progressTotal is not null && progressCurrent > progressTotal)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Progress current cannot exceed progress total.");
        }

        return Result.Success();
    }

    private static async Task InsertOperationAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        BlockingOperation operation)
    {
        await connection.ExecuteAsync(
            """
            insert into blocking_operations (
                operation_id, operation_type, scope_type, scope_id, status,
                progress_current, progress_total, progress_label, can_cancel,
                failure_code, failure_message, next_actions_json, created_at, updated_at
            )
            values (
                @OperationId, @OperationType, @ScopeType, @ScopeId, @Status,
                @ProgressCurrent, @ProgressTotal, @ProgressLabel, @CanCancel,
                @FailureCode, @FailureMessage, @NextActionsJson, @CreatedAt, @UpdatedAt
            );
            """,
            new
            {
                OperationId = operation.OperationId.ToString(),
                operation.OperationType,
                operation.ScopeType,
                operation.ScopeId,
                operation.Status,
                operation.ProgressCurrent,
                operation.ProgressTotal,
                operation.ProgressLabel,
                CanCancel = operation.CanCancel ? 1 : 0,
                operation.FailureCode,
                operation.FailureMessage,
                NextActionsJson = JsonSerializer.Serialize(operation.NextActions),
                CreatedAt = operation.CreatedAt.ToString("O"),
                UpdatedAt = operation.UpdatedAt.ToString("O")
            },
            transaction);
    }

    private static async Task UpdateOperationAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        BlockingOperation operation)
    {
        await connection.ExecuteAsync(
            """
            update blocking_operations
            set status = @Status,
                progress_current = @ProgressCurrent,
                progress_total = @ProgressTotal,
                progress_label = @ProgressLabel,
                can_cancel = @CanCancel,
                failure_code = @FailureCode,
                failure_message = @FailureMessage,
                next_actions_json = @NextActionsJson,
                updated_at = @UpdatedAt
            where operation_id = @OperationId;
            """,
            new
            {
                OperationId = operation.OperationId.ToString(),
                operation.Status,
                operation.ProgressCurrent,
                operation.ProgressTotal,
                operation.ProgressLabel,
                CanCancel = operation.CanCancel ? 1 : 0,
                operation.FailureCode,
                operation.FailureMessage,
                NextActionsJson = JsonSerializer.Serialize(operation.NextActions),
                UpdatedAt = operation.UpdatedAt.ToString("O")
            },
            transaction);
    }

    private static Task<Row?> GetRowAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        BlockingOperationId operationId)
    {
        return connection.QuerySingleOrDefaultAsync<Row>(
            """
            select operation_id as OperationId,
                   operation_type as OperationType,
                   scope_type as ScopeType,
                   scope_id as ScopeId,
                   status as Status,
                   progress_current as ProgressCurrent,
                   progress_total as ProgressTotal,
                   progress_label as ProgressLabel,
                   can_cancel as CanCancel,
                   failure_code as FailureCode,
                   failure_message as FailureMessage,
                   next_actions_json as NextActionsJson,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from blocking_operations
            where operation_id = @OperationId;
            """,
            new { OperationId = operationId.ToString() },
            transaction);
    }

    private static Task InsertLogEntryAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        BlockingOperationLogEntry entry)
    {
        return connection.ExecuteAsync(
            """
            insert into blocking_operation_log_entries (
                entry_id, operation_id, level, message, detail, scope_type, scope_id, created_at
            )
            values (
                @EntryId, @OperationId, @Level, @Message, @Detail, @ScopeType, @ScopeId, @CreatedAt
            );
            """,
            new
            {
                EntryId = entry.EntryId.ToString(),
                OperationId = entry.OperationId.ToString(),
                entry.Level,
                entry.Message,
                entry.Detail,
                entry.ScopeType,
                entry.ScopeId,
                CreatedAt = entry.CreatedAt.ToString("O")
            },
            transaction);
    }

    private static IReadOnlyList<string> CleanActions(IReadOnlyList<string>? actions)
        => actions is null
            ? Array.Empty<string>()
            : actions.Where(action => !string.IsNullOrWhiteSpace(action)).Select(action => action.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Row
    {
        public string OperationId { get; set; } = "";
        public string OperationType { get; set; } = "";
        public string ScopeType { get; set; } = "";
        public string? ScopeId { get; set; }
        public string Status { get; set; } = "";
        public int? ProgressCurrent { get; set; }
        public int? ProgressTotal { get; set; }
        public string? ProgressLabel { get; set; }
        public int CanCancel { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
        public string NextActionsJson { get; set; } = "[]";
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public BlockingOperation ToModel() => new(
            BlockingOperationId.Parse(OperationId),
            OperationType,
            ScopeType,
            ScopeId,
            Status,
            ProgressCurrent,
            ProgressTotal,
            ProgressLabel,
            CanCancel != 0,
            FailureCode,
            FailureMessage,
            ParseActions(NextActionsJson),
            DateTimeOffset.Parse(CreatedAt),
            DateTimeOffset.Parse(UpdatedAt));
    }

    private sealed class LogRow
    {
        public string EntryId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string Level { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Detail { get; set; }
        public string? ScopeType { get; set; }
        public string? ScopeId { get; set; }
        public string CreatedAt { get; set; } = "";

        public BlockingOperationLogEntry ToModel() => new(
            BlockingOperationLogEntryId.Parse(EntryId),
            BlockingOperationId.Parse(OperationId),
            Level,
            Message,
            Detail,
            ScopeType,
            ScopeId,
            DateTimeOffset.Parse(CreatedAt));
    }

    private static IReadOnlyList<string> ParseActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
