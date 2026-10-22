using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Import;

/// <summary>
/// Issue #30: <see cref="ICommittedFileLocationLookup"/> over the library database. A path
/// counts as committed when an available file asset lists it as an available known
/// location (the primary location is mirrored there by the import commit).
/// </summary>
public sealed class CommittedFileLocationLookup : ICommittedFileLocationLookup
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public CommittedFileLocationLookup(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> HasCommittedLocationAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            // Match the claim registry's case semantics: paths compare case-insensitively
            // only on Windows. Unconditional "collate nocase" would report A.pdf as
            // committed on Linux when only a.pdf is, while the coordinator (Ordinal on
            // Linux) treats them as different files.
            string collation = OperatingSystem.IsWindows() ? " collate nocase" : string.Empty;
            int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"""
                select count(1) from known_file_locations k
                join file_assets a on a.file_asset_id = k.file_asset_id
                where k.path = @Path{collation} and k.status = 'available' and a.status = 'available';
                """,
                new { Path = fullPath },
                cancellationToken: cancellationToken));
            return count > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.file-import", "committed-location-lookup"))
        {
            // A failed probe must never block recovery: fall back to the plain restore.
            return false;
        }
    }
}
