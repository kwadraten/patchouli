using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Import;

public sealed record ImportResidueGcOptions(TimeSpan? MinAge = null, bool IncludeUnreferencedFileAssets = true);

public sealed record ImportResidueGcResult(
    int InstancesRemoved,
    int ItemsRemoved,
    int FileAssetsRemoved,
    IReadOnlyList<string> Notes)
{
    public bool IsSuccess { get; init; } = true;
}

/// <summary>
/// One-time startup cleanup for pre-atomic PDF import residue: zero-page DocumentInstances and
/// the orphaned Item/FileAsset pairs a half import leaves behind. Every candidate row must be
/// older than <see cref="ImportResidueGcOptions.MinAge"/> (default one hour) so in-flight
/// imports are never touched; remaining unreferenced FileAssets are delegated to
/// <see cref="IFileAssetGcService"/>. ADR 0035.
/// </summary>
public sealed class ImportResidueGcService
{
    private static readonly TimeSpan DefaultMinAge = TimeSpan.FromHours(1);

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly ILibraryRevisionService? _revisions;
    private readonly IFileAssetGcService? _fileAssetGc;
    private readonly IAppLogger? _logger;

    public ImportResidueGcService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        ILibraryRevisionService? revisions = null,
        IFileAssetGcService? fileAssetGc = null,
        IAppLogger? logger = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _revisions = revisions;
        _fileAssetGc = fileAssetGc;
        _logger = logger;
    }

    public async Task<ImportResidueGcResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        bool completed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from local_maintenance_state where task_id = 'atomic_import_residue_v1');",
            cancellationToken: cancellationToken));
        if (completed)
        {
            return new ImportResidueGcResult(0, 0, 0, []);
        }

        ImportResidueGcResult result = await RunAsync(cancellationToken: cancellationToken);
        bool recentCandidates = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists(select 1 from document_instances d where d.created_at >= @Cutoff
                and not exists(select 1 from pages p where p.document_instance_id = d.document_instance_id))
                or exists(select 1 from file_assets f where f.created_at >= @Cutoff
                and not exists(select 1 from document_instances d where d.file_asset_id = f.file_asset_id));
            """,
            new { Cutoff = FormatUtc(_clock.UtcNow - DefaultMinAge) }, cancellationToken: cancellationToken));
        if (result.IsSuccess && !recentCandidates)
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "insert or ignore into local_maintenance_state(task_id, completed_at) values ('atomic_import_residue_v1', @Now);",
                new { Now = FormatUtc(_clock.UtcNow) }, cancellationToken: cancellationToken));
        }

        return result;
    }

    public async Task<ImportResidueGcResult> RunAsync(
        ImportResidueGcOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ImportResidueGcOptions();
        TimeSpan minAge = options.MinAge ?? DefaultMinAge;
        if (minAge < TimeSpan.Zero)
        {
            minAge = TimeSpan.Zero;
        }

        string cutoff = FormatUtc(_clock.UtcNow - minAge);
        List<string> notes = new();

        try
        {
            int instancesRemoved;
            int itemsRemoved;
            int fileAssetsRemoved;
            bool success = true;

            using (IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken))
            {
                await using SqliteConnection connection = _connectionFactory.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

                List<ItemId> deletedItemIds = new();
                List<DocumentInstanceId> deletedInstanceIds = new();
                instancesRemoved = 0;
                itemsRemoved = 0;
                fileAssetsRemoved = 0;

                List<InstanceRow> zeroPageInstances = (await connection.QueryAsync<InstanceRow>(
                    new CommandDefinition(
                        """
                        select d.document_instance_id as DocumentInstanceId, d.item_id as ItemId
                        from document_instances d
                        where d.created_at < @Cutoff
                          and not exists (
                              select 1 from pages p
                              where p.document_instance_id = d.document_instance_id);
                        """,
                        new { Cutoff = cutoff },
                        transaction,
                        cancellationToken: cancellationToken))).ToList();

                if (zeroPageInstances.Count > 0)
                {
                    string[] instanceIds = zeroPageInstances.Select(row => row.DocumentInstanceId).ToArray();
                    string[] affectedItemIds = zeroPageInstances.Select(row => row.ItemId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            """
                            delete from ocr_candidate_adoptions
                            where document_instance_id in @InstanceIds
                               or ocr_run_id in (
                                   select ocr_run_id from ocr_runs where document_instance_id in @InstanceIds);
                            """,
                            new { InstanceIds = instanceIds },
                            transaction,
                            cancellationToken: cancellationToken));

                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            "delete from document_instances where document_instance_id in @InstanceIds;",
                            new { InstanceIds = instanceIds },
                            transaction,
                            cancellationToken: cancellationToken));

                    string[] emptyItemIds = (await connection.QueryAsync<string>(
                        new CommandDefinition(
                            """
                            select i.item_id
                            from items i
                            where i.item_id in @ItemIds
                              and not exists (
                                  select 1 from document_instances d
                                  where d.item_id = i.item_id);
                            """,
                            new { ItemIds = affectedItemIds },
                            transaction,
                            cancellationToken: cancellationToken))).ToArray();

                    if (emptyItemIds.Length > 0)
                    {
                        await connection.ExecuteAsync(
                            new CommandDefinition(
                                "delete from items where item_id in @ItemIds;",
                                new { ItemIds = emptyItemIds },
                                transaction,
                                cancellationToken: cancellationToken));
                        itemsRemoved += emptyItemIds.Length;
                        deletedItemIds.AddRange(emptyItemIds.Select(ItemId.Parse));
                    }

                    instancesRemoved = instanceIds.Length;
                    deletedInstanceIds.AddRange(instanceIds.Select(DocumentInstanceId.Parse));
                    await NoteAsync(notes,
                        $"Removed {instanceIds.Length} zero-page document instance(s) and {emptyItemIds.Length} item(s) left without instances.");
                }

                List<OrphanItemRow> orphanItems = (await connection.QueryAsync<OrphanItemRow>(
                    new CommandDefinition(
                        """
                        select i.item_id as ItemId, i.title as Title
                        from items i
                        where i.created_at < @Cutoff
                          and i.deleted_at is null
                          and i.merged_into_item_id is null
                          and not exists (select 1 from document_instances d where d.item_id = i.item_id)
                          and not exists (select 1 from item_identifiers x where x.item_id = i.item_id)
                          and not exists (select 1 from item_collections c where c.item_id = i.item_id)
                        order by i.created_at, i.item_id;
                        """,
                        new { Cutoff = cutoff },
                        transaction,
                        cancellationToken: cancellationToken))).ToList();

                if (orphanItems.Count > 0)
                {
                    List<UnreferencedAssetRow> unreferencedAssets = (await connection.QueryAsync<UnreferencedAssetRow>(
                        new CommandDefinition(
                            """
                            select f.file_asset_id as FileAssetId, f.original_path as OriginalPath
                            from file_assets f
                            where f.created_at < @Cutoff
                              and not exists (
                                  select 1 from document_instances d
                                  where d.file_asset_id = f.file_asset_id)
                            order by f.created_at, f.file_asset_id;
                            """,
                            new { Cutoff = cutoff },
                            transaction,
                            cancellationToken: cancellationToken))).ToList();

                    List<(string ItemId, string AssetId)> pairs =
                        PairOrphanItemsWithAssets(orphanItems, unreferencedAssets);

                    if (pairs.Count > 0)
                    {
                        string[] itemIds = pairs.Select(pair => pair.ItemId).ToArray();
                        string[] assetIds = pairs.Select(pair => pair.AssetId)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();

                        await connection.ExecuteAsync(
                            new CommandDefinition(
                                "delete from known_file_locations where file_asset_id in @AssetIds;",
                                new { AssetIds = assetIds },
                                transaction,
                                cancellationToken: cancellationToken));

                        await connection.ExecuteAsync(
                            new CommandDefinition(
                                "delete from file_assets where file_asset_id in @AssetIds;",
                                new { AssetIds = assetIds },
                                transaction,
                                cancellationToken: cancellationToken));

                        await connection.ExecuteAsync(
                            new CommandDefinition(
                                "delete from items where item_id in @ItemIds;",
                                new { ItemIds = itemIds },
                                transaction,
                                cancellationToken: cancellationToken));

                        itemsRemoved += itemIds.Length;
                        fileAssetsRemoved += assetIds.Length;
                        deletedItemIds.AddRange(itemIds.Select(ItemId.Parse));
                        await NoteAsync(notes,
                            $"Removed {itemIds.Length} orphan import item(s) paired with {assetIds.Length} unreferenced file asset(s).");
                    }
                }

                if (deletedItemIds.Count + deletedInstanceIds.Count > 0 && _revisions is not null)
                {
                    Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(
                        connection,
                        transaction,
                        LibraryChangeSet.Empty with
                        {
                            ItemIds = deletedItemIds,
                            DocumentInstanceIds = deletedInstanceIds
                        },
                        cancellationToken);
                    if (revision.IsFailure)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        await NoteAsync(notes, $"Import residue GC aborted: {revision.ErrorMessage}");
                        return new ImportResidueGcResult(0, 0, 0, notes) { IsSuccess = false };
                    }

                    await transaction.CommitAsync(cancellationToken);
                    _revisions.PublishCommitted(revision.Value);
                }
                else
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }

            if (options.IncludeUnreferencedFileAssets && _fileAssetGc is not null)
            {
                try
                {
                    FileAssetGcResult delegated =
                        await _fileAssetGc.RunAsync(new FileAssetGcOptions(), cancellationToken);
                    fileAssetsRemoved += delegated.Deleted.Count;
                    if (delegated.Failed.Count > 0)
                    {
                        success = false;
                        notes.Add($"File asset GC reported {delegated.Failed.Count} failure(s).");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                      exception,
                                                      "infrastructure.import-residue-gc",
                                                      "file-asset-gc"))
                {
                    success = false;
                    string message = $"File asset GC delegation failed: {exception.Message}";
                    notes.Add(message);
                    if (_logger is not null)
                    {
                        await _logger.LogAsync("import-residue-gc", message);
                    }
                }
            }

            return new ImportResidueGcResult(instancesRemoved, itemsRemoved, fileAssetsRemoved, notes)
                { IsSuccess = success };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.import-residue-gc"))
        {
            string message = $"Import residue GC failed: {exception.Message}";
            notes.Add(message);
            if (_logger is not null)
            {
                await _logger.LogAsync("import-residue-gc", message);
            }

            return new ImportResidueGcResult(0, 0, 0, notes) { IsSuccess = false };
        }
    }

    private static List<(string ItemId, string AssetId)> PairOrphanItemsWithAssets(
        List<OrphanItemRow> orphanItems,
        List<UnreferencedAssetRow> unreferencedAssets)
    {
        List<(string ItemId, string AssetId)> pairs = new();
        Dictionary<string, Queue<UnreferencedAssetRow>> assetsByTitle = new(StringComparer.Ordinal);
        foreach (UnreferencedAssetRow asset in unreferencedAssets)
        {
            string title = Normalize(Path.GetFileNameWithoutExtension(asset.OriginalPath) ?? string.Empty);
            if (!assetsByTitle.TryGetValue(title, out Queue<UnreferencedAssetRow>? candidates))
            {
                candidates = new Queue<UnreferencedAssetRow>();
                assetsByTitle.Add(title, candidates);
            }

            candidates.Enqueue(asset);
        }

        foreach (OrphanItemRow item in orphanItems)
        {
            string title = Normalize(item.Title);
            if (title.Length > 0 && assetsByTitle.TryGetValue(title, out Queue<UnreferencedAssetRow>? candidates) &&
                candidates.TryDequeue(out UnreferencedAssetRow? match))
            {
                pairs.Add((item.ItemId, match.FileAssetId));
            }
        }

        return pairs;
    }

    private async Task NoteAsync(List<string> notes, string message)
    {
        notes.Add(message);
        if (_logger is not null)
        {
            await _logger.LogAsync("import-residue-gc", message);
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class InstanceRow
    {
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
    }

    private sealed class OrphanItemRow
    {
        public string ItemId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    private sealed class UnreferencedAssetRow
    {
        public string FileAssetId { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
    }
}
