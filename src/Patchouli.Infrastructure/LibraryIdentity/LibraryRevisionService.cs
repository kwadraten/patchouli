using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.LibraryIdentity;

/// <summary>
/// The persistent, monotonic Library revision. The current value is stored on the single
/// <c>library_metadata</c> row so desktop/headless host handoffs never reset it. Only
/// protocol-visible commits bump it; staging and rebuildable local FTS maintenance do not.
/// </summary>
public sealed class LibraryRevisionService : ILibraryRevisionService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public LibraryRevisionService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted;

    public async Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            long revision = await connection.ExecuteScalarAsync<long>(
                """
                select coalesce(library_revision, 0)
                from library_metadata
                order by created_at, library_id
                limit 1;
                """);
            return Result<long>.Success(revision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-revision"))
        {
            return Result<long>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<long>> CommitAsync(
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateWriteConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            Result<LibraryChangeSet> incremented = await IncrementInTransactionAsync(
                connection, transaction, changeSet, cancellationToken);
            if (incremented.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<long>.Failure(incremented.ErrorCode!, incremented.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishCommitted(incremented.Value);
            return Result<long>.Success(incremented.Value.NewRevision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-revision"))
        {
            return Result<long>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        try
        {
            int affected = await connection.ExecuteAsync(
                """
                update library_metadata
                set library_revision = library_revision + 1
                where library_id = (
                    select library_id
                    from library_metadata
                    order by created_at, library_id
                    limit 1);
                """,
                transaction: transaction);
            if (affected == 0)
            {
                return Result<LibraryChangeSet>.Failure(AppErrorCodes.InvalidState,
                    "Current library metadata was not found.");
            }

            long newRevision = await connection.ExecuteScalarAsync<long>(
                """
                select library_revision from library_metadata
                order by created_at, library_id limit 1;
                """,
                transaction: transaction);
            return Result<LibraryChangeSet>.Success(changeSet with { NewRevision = newRevision });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-revision"))
        {
            return Result<LibraryChangeSet>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public void PublishCommitted(LibraryChangeSet changeSet)
    {
        ChangeCommitted?.Invoke(this, new LibraryRevisionCommittedEventArgs(changeSet));
    }
}
