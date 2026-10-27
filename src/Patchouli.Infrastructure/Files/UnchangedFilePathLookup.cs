using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Import;

namespace Patchouli.Infrastructure.Files;

/// <summary>Finds remembered copies that a reconciliation scan can skip without reimporting.</summary>
public sealed class UnchangedFilePathLookup(SqliteConnectionFactory connectionFactory)
{
    public async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = connectionFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);
        KnownPath[] paths = (await connection.QueryAsync<KnownPath>(new CommandDefinition(
            """
            select a.file_asset_id as FileAssetId, a.original_path as OriginalPath,
                   a.original_path as Path, a.size_bytes as SizeBytes, a.mtime_utc as MtimeUtc,
                   (select k.status from known_file_locations k
                    where k.file_asset_id = a.file_asset_id and k.path = a.original_path) as PrimaryStatus
            from file_assets a where a.status = 'available'
            union all
            select a.file_asset_id, a.original_path, k.path, a.size_bytes, a.mtime_utc,
                   (select p.status from known_file_locations p
                    where p.file_asset_id = a.file_asset_id and p.path = a.original_path)
            from known_file_locations k join file_assets a on a.file_asset_id = k.file_asset_id
            where k.status = 'available' and a.status = 'available';
            """, cancellationToken: cancellationToken))).ToArray();
        HashSet<string> unchanged = new(FileOperationCoordinator.PathComparer);
        foreach (IGrouping<string, KnownPath> group in paths.GroupBy(path => path.FileAssetId))
        {
            // Reimport a discovered copy when its primary or a remembered copy disappeared.
            if (group.First().PrimaryStatus != FileAssetStatus.Available ||
                group.Any(path => !File.Exists(path.Path) && Directory.Exists(Path.GetDirectoryName(path.Path))) ||
                !MatchesMetadata(group.First(), true))
            {
                continue;
            }

            foreach (KnownPath path in group.Where(path => MatchesMetadata(path, false)))
            {
                unchanged.Add(Path.GetFullPath(path.Path));
            }
        }

        return unchanged;
    }

    private static bool MatchesMetadata(KnownPath path, bool primary)
    {
        try
        {
            FileInfo info = new(primary ? path.OriginalPath : path.Path);
            return info.Exists && info.Length == path.SizeBytes &&
                   DateTimeOffset.TryParse(path.MtimeUtc, out DateTimeOffset modified) &&
                   info.LastWriteTimeUtc == modified.UtcDateTime;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class KnownPath
    {
        public string FileAssetId { get; init; } = "";
        public string OriginalPath { get; init; } = "";
        public string Path { get; init; } = "";
        public long SizeBytes { get; init; }
        public string? MtimeUtc { get; init; }
        public string? PrimaryStatus { get; init; }
    }
}
