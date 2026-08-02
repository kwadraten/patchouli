using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.LibraryIdentity;

public sealed class LibraryIdentityService : ILibraryIdentityService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly int _schemaVersion;

    public LibraryIdentityService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        int schemaVersion = AppSchemaVersion.Current)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _schemaVersion = schemaVersion;
    }

    public async Task<Result<LibraryMetadata>> CreateLibraryAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Result<LibraryMetadata>.Failure(
                AppErrorCodes.ValidationFailed,
                "Library display name is required.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            int existingCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from library_metadata;",
                transaction: transaction);

            if (existingCount > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<LibraryMetadata>.Failure(
                    AppErrorCodes.InvalidState,
                    "This runtime database already contains a library identity.");
            }

            DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
            LibraryMetadata metadata = new(
                LibraryId.New(),
                displayName.Trim(),
                _schemaVersion,
                now,
                now);

            await connection.ExecuteAsync(
                """
                insert into library_metadata (
                    library_id,
                    display_name,
                    schema_version,
                    created_at,
                    updated_at
                )
                values (
                    @LibraryId,
                    @DisplayName,
                    @SchemaVersion,
                    @CreatedAt,
                    @UpdatedAt
                );
                """,
                ToParameters(metadata),
                transaction);

            long revision = await connection.ExecuteScalarAsync<long>(
                """
                select library_revision
                from library_metadata
                order by created_at, library_id
                limit 1;
                """,
                transaction: transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<LibraryMetadata>.Success(metadata with { LibraryRevision = revision });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-identity"))
        {
            return DatabaseFailure<LibraryMetadata>(exception);
        }
    }

    public async Task<Result<LibraryMetadata>> GetCurrentLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            LibraryMetadataRow[] rows = (await connection.QueryAsync<LibraryMetadataRow>(
                """
                select
                    library_id as LibraryId,
                    display_name as DisplayName,
                    schema_version as SchemaVersion,
                    library_revision as LibraryRevision,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                from library_metadata
                order by created_at, library_id;
                """)).ToArray();

            return FromRows(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-identity"))
        {
            return DatabaseFailure<LibraryMetadata>(exception);
        }
    }

    public async Task<Result<LibraryMetadata>> RenameLibraryAsync(
        string newDisplayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            return Result<LibraryMetadata>.Failure(
                AppErrorCodes.ValidationFailed,
                "Library display name is required.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            LibraryMetadataRow[] rows = (await connection.QueryAsync<LibraryMetadataRow>(
                """
                select
                    library_id as LibraryId,
                    display_name as DisplayName,
                    schema_version as SchemaVersion,
                    library_revision as LibraryRevision,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                from library_metadata
                order by created_at, library_id;
                """,
                transaction: transaction)).ToArray();

            Result<LibraryMetadata> currentResult = FromRows(rows);
            if (currentResult.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return currentResult;
            }

            LibraryMetadata current = currentResult.Value;
            DateTimeOffset updatedAt = _clock.UtcNow.ToUniversalTime();

            await connection.ExecuteAsync(
                """
                update library_metadata
                set display_name = @DisplayName,
                    updated_at = @UpdatedAt
                where library_id = @LibraryId;
                """,
                new
                {
                    DisplayName = newDisplayName.Trim(),
                    UpdatedAt = FormatUtc(updatedAt),
                    LibraryId = current.LibraryId.ToString()
                },
                transaction);

            await transaction.CommitAsync(cancellationToken);

            return Result<LibraryMetadata>.Success(current with
            {
                DisplayName = newDisplayName.Trim(),
                UpdatedAt = updatedAt
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-identity"))
        {
            return DatabaseFailure<LibraryMetadata>(exception);
        }
    }

    public async Task<Result> ValidateLibraryIdAsync(
        LibraryId expectedLibraryId,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> currentResult = await GetCurrentLibraryAsync(cancellationToken);
        if (currentResult.IsFailure)
        {
            return Result.Failure(currentResult.ErrorCode!, currentResult.ErrorMessage!);
        }

        if (currentResult.Value.LibraryId != expectedLibraryId)
        {
            return Result.Failure(
                AppErrorCodes.LibraryMismatch,
                "The current library does not match the expected library id.");
        }

        return Result.Success();
    }

    private static Result<LibraryMetadata> FromRows(IReadOnlyCollection<LibraryMetadataRow> rows)
    {
        if (rows.Count == 0)
        {
            return Result<LibraryMetadata>.Failure(
                AppErrorCodes.NotFound,
                "No library identity exists in this runtime database.");
        }

        if (rows.Count > 1)
        {
            return Result<LibraryMetadata>.Failure(
                AppErrorCodes.InvalidState,
                "This runtime database contains more than one library identity.");
        }

        return Result<LibraryMetadata>.Success(rows.Single().ToMetadata());
    }

    private static object ToParameters(LibraryMetadata metadata)
    {
        return new
        {
            LibraryId = metadata.LibraryId.ToString(),
            metadata.DisplayName,
            metadata.SchemaVersion,
            CreatedAt = FormatUtc(metadata.CreatedAt),
            UpdatedAt = FormatUtc(metadata.UpdatedAt)
        };
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(
            AppErrorCodes.DatabaseError,
            $"Database operation failed: {exception.Message}");
    }

    private sealed class LibraryMetadataRow
    {
        public string LibraryId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public long LibraryRevision { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public LibraryMetadata ToMetadata()
        {
            return new LibraryMetadata(
                Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
                DisplayName,
                SchemaVersion,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt),
                LibraryRevision);
        }
    }
}
