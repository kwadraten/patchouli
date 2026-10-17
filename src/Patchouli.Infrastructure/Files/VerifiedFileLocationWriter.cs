using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Files;

internal static class VerifiedFileLocationWriter
{
    // The caller has verified the observed path against the asset's full content hash.
    public static async Task<bool> RecordAsync(
        SqliteConnection connection, DbTransaction transaction, FileAssetId fileAssetId,
        FileFingerprint observed, DateTimeOffset now, IFileFingerprintService fingerprints,
        CancellationToken cancellationToken)
    {
        string id = fileAssetId.ToString();
        AssetLocation asset = await connection.QuerySingleAsync<AssetLocation>(new CommandDefinition(
            """
            select original_path as OriginalPath, full_blake3 as FullBlake3
            from file_assets where file_asset_id = @Id;
            """, new { Id = id }, transaction, cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(asset.FullBlake3) ||
            !string.Equals(asset.FullBlake3, observed.FullBlake3, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recording a file location requires the asset's complete fingerprint.");
        }

        bool samePath = string.Equals(Path.GetFullPath(asset.OriginalPath), observed.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        FileFingerprint preferred = observed;
        string? previousStatus = null;
        if (!samePath)
        {
            if (File.Exists(asset.OriginalPath))
            {
                Result<FileFingerprint> original =
                    await fingerprints.GetFileMetadataAsync(asset.OriginalPath, cancellationToken);
                if (original.IsSuccess)
                {
                    if (string.Equals(original.Value.FullBlake3, asset.FullBlake3, StringComparison.Ordinal))
                    {
                        preferred = original.Value;
                    }
                    else
                    {
                        previousStatus = FileAssetStatus.Changed;
                    }
                }
            }
            else if (IsConfirmedMissing(asset.OriginalPath))
            {
                previousStatus = FileAssetStatus.Missing;
            }
        }

        int changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            update file_assets
            set original_path = @Path, file_name = @FileName, size_bytes = @SizeBytes,
                mtime_utc = @MtimeUtc, quick_hash = @QuickHash, status = 'available', updated_at = @Now
            where file_asset_id = @Id and
                (original_path <> @Path or file_name <> @FileName or size_bytes <> @SizeBytes or
                 mtime_utc is not @MtimeUtc or quick_hash is not @QuickHash or status <> 'available');
            """, new
            {
                Id = id, preferred.Path, preferred.FileName, preferred.SizeBytes,
                MtimeUtc = preferred.MtimeUtc.ToUniversalTime().ToString("O"), preferred.QuickHash,
                Now = now.ToUniversalTime().ToString("O")
            }, transaction, cancellationToken: cancellationToken));

        string? locationStatus = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "select status from known_file_locations where file_asset_id = @Id and path = @Path;",
            new { Id = id, observed.Path }, transaction, cancellationToken: cancellationToken));
        int displaced = await FileAssetService.UpsertKnownLocationAsync(connection, transaction, fileAssetId,
            observed.Path, FileAssetStatus.Available, now);
        if (previousStatus is not null)
        {
            changed += await connection.ExecuteAsync(new CommandDefinition(
                """
                update known_file_locations set status = @Status
                where file_asset_id = @Id and path = @Path and status <> @Status;
                """, new { Id = id, Path = asset.OriginalPath, Status = previousStatus }, transaction,
                cancellationToken: cancellationToken));
        }

        string[] remembered = (await connection.QueryAsync<string>(new CommandDefinition(
            "select path from known_file_locations where file_asset_id = @Id and status = 'available';",
            new { Id = id }, transaction, cancellationToken: cancellationToken))).ToArray();
        foreach (string path in remembered.Where(path => path != observed.Path && IsConfirmedMissing(path)))
        {
            changed += await connection.ExecuteAsync(new CommandDefinition(
                "update known_file_locations set status = 'missing' where file_asset_id = @Id and path = @Path;",
                new { Id = id, Path = path }, transaction, cancellationToken: cancellationToken));
        }

        return changed > 0 || displaced > 0 || locationStatus != FileAssetStatus.Available;
    }

    private static bool IsConfirmedMissing(string path)
    {
        // File.Exists also returns false for access failures. Only a missing directory entry
        // in an accessible parent establishes absence; an offline root remains remembered.
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return false;
        }

        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
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

    private sealed class AssetLocation
    {
        public string OriginalPath { get; init; } = "";
        public string? FullBlake3 { get; init; }
    }
}
