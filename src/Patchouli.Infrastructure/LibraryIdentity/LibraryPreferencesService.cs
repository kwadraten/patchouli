using System.Text.Json;
using Dapper;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.LibraryIdentity;

public sealed class LibraryPreferencesService : ILibraryPreferencesService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IClock _clock;

    public LibraryPreferencesService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService,
        IClock clock)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _clock = clock;
    }

    public async Task<Result<LibraryPreferences>> GetPreferencesAsync(string scope = "default", CancellationToken cancellationToken = default)
    {
        var library = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<LibraryPreferences>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<Row>(
                """
                select library_id as LibraryId,
                       scope as Scope,
                       columns_json as ColumnsJson,
                       updated_at as UpdatedAt
                from library_preferences
                where library_id = @LibraryId and scope = @Scope;
                """,
                new { LibraryId = library.Value.LibraryId.ToString(), Scope = scope });

            return Result<LibraryPreferences>.Success(
                row is null
                    ? new LibraryPreferences(library.Value.LibraryId, scope, [], _clock.UtcNow.ToUniversalTime())
                    : row.ToModel());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<LibraryPreferences>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<LibraryPreferences>> SavePreferencesAsync(IReadOnlyList<LibraryColumnPreference> columns, string scope = "default", CancellationToken cancellationToken = default)
    {
        var library = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<LibraryPreferences>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        try
        {
            var saved = new LibraryPreferences(library.Value.LibraryId, scope, columns.OrderBy(column => column.Order).ToArray(), _clock.UtcNow.ToUniversalTime());
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(
                """
                insert into library_preferences (library_id, scope, columns_json, updated_at)
                values (@LibraryId, @Scope, @ColumnsJson, @UpdatedAt)
                on conflict(library_id, scope) do update set
                    columns_json = excluded.columns_json,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    LibraryId = saved.LibraryId.ToString(),
                    saved.Scope,
                    ColumnsJson = JsonSerializer.Serialize(saved.Columns),
                    UpdatedAt = saved.UpdatedAt.ToString("O")
                });

            return Result<LibraryPreferences>.Success(saved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<LibraryPreferences>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private sealed class Row
    {
        public string LibraryId { get; set; } = "";
        public string Scope { get; set; } = "";
        public string ColumnsJson { get; set; } = "[]";
        public string UpdatedAt { get; set; } = "";

        public LibraryPreferences ToModel()
            => new(
                Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
                Scope,
                JsonSerializer.Deserialize<LibraryColumnPreference[]>(ColumnsJson) ?? [],
                DateTimeOffset.Parse(UpdatedAt));
    }
}
