using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Import;

public sealed class FileImportOwner
{
    public string LibraryId { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string FileAssetId { get; init; } = string.Empty;
    public string DocumentInstanceId { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
}

public static class FileImportDeduplication
{
    // Resolved missing files and confirmed changed sources retain their original asset IDs.
    // Match the persisted content hash, using the hash index rather than assuming canonical IDs.
    internal const string OwnerQuery = """
                                       select a.library_id as LibraryId, i.item_id as ItemId,
                                              a.file_asset_id as FileAssetId, d.document_instance_id as DocumentInstanceId,
                                              i.deleted_at is not null or i.merged_into_item_id is not null as IsDeleted
                                       from file_assets a indexed by idx_file_assets_library_full_blake3
                                       join document_instances d on d.file_asset_id = a.file_asset_id
                                       join items i on i.item_id = d.item_id
                                       where a.library_id = @LibraryId and a.full_blake3 = @FullBlake3
                                       order by IsDeleted, d.is_primary desc, d.document_instance_id
                                       limit 1;
                                       """;

    internal const string OwnerByIdQuery = """
                                           select a.library_id as LibraryId, i.item_id as ItemId,
                                                  a.file_asset_id as FileAssetId, d.document_instance_id as DocumentInstanceId,
                                                  i.deleted_at is not null or i.merged_into_item_id is not null as IsDeleted
                                           from file_assets a
                                           join document_instances d on d.file_asset_id = a.file_asset_id
                                           join items i on i.item_id = d.item_id
                                           where a.library_id = @LibraryId and a.file_asset_id = @FileAssetId
                                           order by IsDeleted, d.is_primary desc, d.document_instance_id
                                           limit 1;
                                           """;

    public static Task<FileImportOwner?> FindOwnerAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        LibraryId libraryId,
        FileAssetId fileAssetId,
        string? fullBlake3,
        CancellationToken cancellationToken = default)
    {
        return connection.QuerySingleOrDefaultAsync<FileImportOwner>(new CommandDefinition(
            string.IsNullOrWhiteSpace(fullBlake3) ? OwnerByIdQuery : OwnerQuery,
            new
            {
                LibraryId = libraryId.ToString(), FileAssetId = fileAssetId.ToString(), FullBlake3 = fullBlake3
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
