using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Bibliography.Biblatex;

public sealed class BiblatexImportService : IBiblatexImportService
{
    private readonly IBiblatexHelperClient _helper;
    private readonly IItemService _items;
    private readonly IFileAssetService _files;
    private readonly IDocumentInstanceService _documents;

    public BiblatexImportService(
        IBiblatexHelperClient helper,
        IItemService items,
        IFileAssetService files,
        IDocumentInstanceService documents)
    {
        _helper = helper;
        _items = items;
        _files = files;
        _documents = documents;
    }

    public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        return _helper.ParseAsync(text, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return Result<IReadOnlyList<BiblatexEntryDto>>.Failure(
                AppErrorCodes.NotFound,
                $"BibLaTeX file was not found: {path}");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        Result utf8 = BiblatexImportPlanner.ReadUtf8Strict(bytes, out string text);
        if (utf8.IsFailure)
        {
            return Result<IReadOnlyList<BiblatexEntryDto>>.Failure(utf8.ErrorCode!, utf8.ErrorMessage!);
        }

        return await _helper.ParseAsync(text, cancellationToken);
    }

    public async Task<Result<BiblatexSingleImportPreview>> PreviewSingleAsync(
        BiblatexEntryDto entry,
        ItemId? targetItemId,
        CancellationToken cancellationToken = default)
    {
        ItemMetadata? target = null;
        if (targetItemId is { } existingTarget)
        {
            Result<ItemMetadata> loaded = await _items.GetItemAsync(existingTarget, cancellationToken);
            if (loaded.IsFailure)
            {
                return Result<BiblatexSingleImportPreview>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
            }

            target = loaded.Value;
        }

        Result<BiblatexSingleImportPlan> plan = BiblatexImportPlanner.PlanSingleImport(entry, target);
        return plan.IsFailure
            ? Result<BiblatexSingleImportPreview>.Failure(plan.ErrorCode!, plan.ErrorMessage!)
            : Result<BiblatexSingleImportPreview>.Success(new BiblatexSingleImportPreview(
                plan.Value.Source,
                plan.Value.FieldConflicts,
                plan.Value.FieldConflictDescriptor));
    }

    public async Task<Result<BiblatexImportApplyResult>> ApplySingleAsync(
        BiblatexMappedItem source,
        ItemId? targetItemId,
        IReadOnlyDictionary<string, string>? fieldChoices,
        string? bibFileDirectory,
        CancellationToken cancellationToken = default)
    {
        List<string> created = [];
        List<string> updated = [];
        List<BiblatexFileSkip> skips = [];

        try
        {
            if (targetItemId is null)
            {
                Result<ItemMetadata> createdItem =
                    await _items.CreateItemAsync(BiblatexMappedItemMerge.ToCreateRequest(source), cancellationToken);
                if (createdItem.IsFailure)
                {
                    return Result<BiblatexImportApplyResult>.Failure(createdItem.ErrorCode!, createdItem.ErrorMessage!);
                }

                created.Add(createdItem.Value.ItemId.ToString());
                await AttachFileAsync(source, createdItem.Value.ItemId, true, bibFileDirectory, skips,
                    cancellationToken);
            }
            else
            {
                ItemId target = targetItemId!.Value;
                Result<ItemMetadata> local = await _items.GetItemAsync(target, cancellationToken);
                if (local.IsFailure)
                {
                    return Result<BiblatexImportApplyResult>.Failure(local.ErrorCode!, local.ErrorMessage!);
                }

                IReadOnlyList<BiblatexFieldConflict> conflicts =
                    BiblatexFieldConflictAnalyzer.FindConflicts(local.Value, source);
                UpdateItemRequest request;
                IReadOnlyList<ItemIdentifierInput> identifiersAfter;
                if (conflicts.Count == 0)
                {
                    request = BiblatexMappedItemMerge.ToAcceptedUpdateRequest(local.Value, source,
                        out identifiersAfter);
                }
                else
                {
                    if (fieldChoices is null || !ValidateFieldChoices(conflicts, fieldChoices))
                    {
                        return Result<BiblatexImportApplyResult>.Failure(
                            AppErrorCodes.ValidationFailed,
                            "Field conflict choices are incomplete.");
                    }

                    request = BiblatexMappedItemMerge.ToFieldChoiceUpdateRequest(
                        local.Value, source, fieldChoices, out identifiersAfter);
                }

                Result<ItemMetadata> updatedItem =
                    await _items.UpdateItemAsync(target, request, cancellationToken);
                if (updatedItem.IsFailure)
                {
                    return Result<BiblatexImportApplyResult>.Failure(updatedItem.ErrorCode!, updatedItem.ErrorMessage!);
                }

                Result idSync = await SyncIdentifiersAsync(target, local.Value, identifiersAfter,
                    cancellationToken);
                if (idSync.IsFailure)
                {
                    return Result<BiblatexImportApplyResult>.Failure(idSync.ErrorCode!, idSync.ErrorMessage!);
                }

                updated.Add(target.ToString());
                await AttachFileAsync(source, target, false, bibFileDirectory, skips,
                    cancellationToken);
            }
        }
        catch (Exception)
        {
            await CompensateDeletesAsync(created, CancellationToken.None);
            throw;
        }

        return Result<BiblatexImportApplyResult>.Success(new BiblatexImportApplyResult(
            created,
            updated,
            CollapseSkips(skips),
            BuildStatusMessage(created.Count, updated.Count, skips)));
    }

    public async Task<Result<BiblatexBatchImportPreview>> PreviewBatchAsync(
        IReadOnlyList<BiblatexEntryDto> entries,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<BiblatexMatchCandidateSeed>> seeds = await LoadSeedsAsync(cancellationToken);
        if (seeds.IsFailure)
        {
            return Result<BiblatexBatchImportPreview>.Failure(seeds.ErrorCode!, seeds.ErrorMessage!);
        }

        Result<BiblatexBatchImportPlan> plan = BiblatexImportPlanner.PlanBatchImport(entries, seeds.Value);
        return plan.IsFailure
            ? Result<BiblatexBatchImportPreview>.Failure(plan.ErrorCode!, plan.ErrorMessage!)
            : Result<BiblatexBatchImportPreview>.Success(new BiblatexBatchImportPreview(plan.Value));
    }

    public async Task<Result<BiblatexImportApplyResult>> ApplyBatchAsync(
        BiblatexBatchImportPlan plan,
        IReadOnlyDictionary<string, string>? linkChoices,
        string? bibFileDirectory,
        CancellationToken cancellationToken = default)
    {
        if (plan.HasCandidates)
        {
            if (linkChoices is null || !ValidateLinkChoices(plan, linkChoices))
            {
                return Result<BiblatexImportApplyResult>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "Batch link choices are incomplete.");
            }
        }

        List<string> created = [];
        List<string> updated = [];
        List<BiblatexFileSkip> skips = [];

        try
        {
            foreach (BiblatexSourceMatchGroup group in plan.Groups)
            {
                bool createNew = !plan.HasCandidates;
                ItemId? targetId = null;
                if (plan.HasCandidates)
                {
                    string choice = linkChoices![group.Source.SourceEntryKey];
                    if (string.Equals(choice, "new", StringComparison.Ordinal))
                    {
                        createNew = true;
                    }
                    else
                    {
                        targetId = ItemId.Parse(choice);
                        createNew = false;
                    }
                }

                if (createNew)
                {
                    Result<ItemMetadata> createdItem = await _items.CreateItemAsync(
                        BiblatexMappedItemMerge.ToCreateRequest(group.Source),
                        cancellationToken);
                    if (createdItem.IsFailure)
                    {
                        await CompensateDeletesAsync(created, CancellationToken.None);
                        return Result<BiblatexImportApplyResult>.Failure(
                            createdItem.ErrorCode!,
                            createdItem.ErrorMessage!);
                    }

                    created.Add(createdItem.Value.ItemId.ToString());
                    await AttachFileAsync(group.Source, createdItem.Value.ItemId, true, bibFileDirectory,
                        skips, cancellationToken);
                    continue;
                }

                ItemId linkedTarget = targetId!.Value;
                Result<ItemMetadata> local = await _items.GetItemAsync(linkedTarget, cancellationToken);
                if (local.IsFailure)
                {
                    await CompensateDeletesAsync(created, CancellationToken.None);
                    return Result<BiblatexImportApplyResult>.Failure(local.ErrorCode!, local.ErrorMessage!);
                }

                // CF-07 acceptance adopts provided fields without CF-06.
                UpdateItemRequest request = BiblatexMappedItemMerge.ToAcceptedUpdateRequest(
                    local.Value, group.Source, out IReadOnlyList<ItemIdentifierInput> identifiersAfter);

                Result<ItemMetadata> updatedItem =
                    await _items.UpdateItemAsync(linkedTarget, request, cancellationToken);
                if (updatedItem.IsFailure)
                {
                    await CompensateDeletesAsync(created, CancellationToken.None);
                    return Result<BiblatexImportApplyResult>.Failure(updatedItem.ErrorCode!, updatedItem.ErrorMessage!);
                }

                Result idSync =
                    await SyncIdentifiersAsync(linkedTarget, local.Value, identifiersAfter, cancellationToken);
                if (idSync.IsFailure)
                {
                    await CompensateDeletesAsync(created, CancellationToken.None);
                    return Result<BiblatexImportApplyResult>.Failure(idSync.ErrorCode!, idSync.ErrorMessage!);
                }

                updated.Add(linkedTarget.ToString());
                await AttachFileAsync(group.Source, linkedTarget, false, bibFileDirectory, skips,
                    cancellationToken);
            }
        }
        catch (Exception)
        {
            await CompensateDeletesAsync(created, CancellationToken.None);
            throw;
        }

        return Result<BiblatexImportApplyResult>.Success(new BiblatexImportApplyResult(
            created,
            updated,
            CollapseSkips(skips),
            BuildStatusMessage(created.Count, updated.Count, skips)));
    }

    public async Task<Result<string>> ExportItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        List<ItemMetadata> items = [];
        foreach (ItemId itemId in itemIds)
        {
            Result<ItemMetadata> item = await _items.GetItemAsync(itemId, cancellationToken);
            if (item.IsFailure)
            {
                return Result<string>.Failure(item.ErrorCode!, item.ErrorMessage!);
            }

            items.Add(item.Value);
        }

        Result<IReadOnlyList<BiblatexWriteEntryDto>> mapped = BiblatexExportMapper.MapItems(items);
        if (mapped.IsFailure)
        {
            return Result<string>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        return await _helper.WriteAsync(mapped.Value, cancellationToken);
    }

    private async Task<Result<IReadOnlyList<BiblatexMatchCandidateSeed>>> LoadSeedsAsync(
        CancellationToken cancellationToken)
    {
        List<BiblatexMatchCandidateSeed> seeds = [];
        string? cursor = null;
        while (true)
        {
            Result<ItemListPage> page = await _items.ListItemsAsync(
                new ListItemsRequest(PageSize: 200, Cursor: cursor),
                cancellationToken);
            if (page.IsFailure)
            {
                return Result<IReadOnlyList<BiblatexMatchCandidateSeed>>.Failure(
                    page.ErrorCode!,
                    page.ErrorMessage!);
            }

            foreach (ItemMetadata item in page.Value.Items)
            {
                seeds.Add(ToSeed(item));
            }

            if (string.IsNullOrWhiteSpace(page.Value.NextCursor) || page.Value.Items.Count == 0)
            {
                break;
            }

            cursor = page.Value.NextCursor;
        }

        return Result<IReadOnlyList<BiblatexMatchCandidateSeed>>.Success(seeds);
    }

    private static BiblatexMatchCandidateSeed ToSeed(ItemMetadata item)
    {
        HashSet<int> years = [];
        foreach (ItemDate date in item.Dates.Where(static d =>
                     string.Equals(d.Role, "issued", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                int[][]? parts = JsonSerializer.Deserialize<int[][]>(date.DatePartsJson);
                if (parts is null)
                {
                    continue;
                }

                foreach (int[] part in parts)
                {
                    if (part.Length > 0)
                    {
                        years.Add(part[0]);
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore unparsable date parts for matching seeds.
            }
        }

        string[] authors = item.Creators
            .Where(static creator =>
                string.Equals(creator.Role, "author", StringComparison.OrdinalIgnoreCase))
            .Select(static creator =>
            {
                if (!string.IsNullOrWhiteSpace(creator.Literal))
                {
                    return creator.Literal.Trim();
                }

                string family = creator.Family?.Trim() ?? "";
                string given = creator.Given?.Trim() ?? "";
                return string.Join(" ", new[] { given, family }.Where(static part => part.Length > 0));
            })
            .Where(static value => value.Length > 0)
            .ToArray();

        return new BiblatexMatchCandidateSeed(
            item.ItemId.ToString(),
            item.Title,
            item.PublicationTitle,
            item.Publisher,
            authors,
            years);
    }

    private async Task AttachFileAsync(
        BiblatexMappedItem source,
        ItemId itemId,
        bool makePrimary,
        string? bibFileDirectory,
        List<BiblatexFileSkip> skips,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.FilePath))
        {
            return;
        }

        string raw = source.FilePath.Trim();
        string resolved;
        if (Path.IsPathRooted(raw))
        {
            resolved = raw;
        }
        else if (string.IsNullOrWhiteSpace(bibFileDirectory))
        {
            skips.Add(new BiblatexFileSkip("clipboard relative path has no base directory", 1));
            return;
        }
        else
        {
            resolved = Path.GetFullPath(Path.Combine(bibFileDirectory, raw));
        }

        if (!File.Exists(resolved))
        {
            skips.Add(new BiblatexFileSkip("path does not exist", 1));
            return;
        }

        try
        {
            Result<FileAsset> asset = await _files.RegisterFileAsync(resolved, cancellationToken);
            if (asset.IsFailure)
            {
                skips.Add(new BiblatexFileSkip(asset.ErrorMessage ?? "register failed", 1));
                return;
            }

            Result<DocumentInstance> document = await _documents.AttachDocumentInstanceAsync(
                itemId,
                asset.Value.FileAssetId,
                makePrimary ? DocumentInstanceType.PrimaryScan : DocumentInstanceType.Supplement,
                Path.GetFileName(resolved),
                makePrimary,
                cancellationToken);
            if (document.IsFailure)
            {
                skips.Add(new BiblatexFileSkip(document.ErrorMessage ?? "attach failed", 1));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            skips.Add(new BiblatexFileSkip(ex.Message, 1));
        }
    }

    private async Task<Result> SyncIdentifiersAsync(
        ItemId itemId,
        ItemMetadata before,
        IReadOnlyList<ItemIdentifierInput> after,
        CancellationToken cancellationToken)
    {
        HashSet<string> desired = new(
            after.Select(static id => id.Scheme.Trim().ToLowerInvariant() + "\u001f" + id.Value.Trim()),
            StringComparer.Ordinal);

        foreach (ItemIdentifier existing in before.Identifiers)
        {
            string key = existing.Scheme.Trim().ToLowerInvariant() + "\u001f" + existing.Value.Trim();
            if (!desired.Contains(key))
            {
                Result removed = await _items.RemoveIdentifierAsync(itemId, existing.IdentifierId, cancellationToken);
                if (removed.IsFailure)
                {
                    return removed;
                }
            }
        }

        HashSet<string> present = new(
            before.Identifiers.Select(static id =>
                id.Scheme.Trim().ToLowerInvariant() + "\u001f" + id.Value.Trim()),
            StringComparer.Ordinal);

        foreach (ItemIdentifierInput identifier in after)
        {
            string key = identifier.Scheme.Trim().ToLowerInvariant() + "\u001f" + identifier.Value.Trim();
            if (present.Contains(key))
            {
                continue;
            }

            Result<ItemIdentifier> added = await _items.AddIdentifierAsync(
                itemId,
                identifier.Scheme,
                identifier.Value,
                identifier.Note,
                cancellationToken);
            if (added.IsFailure)
            {
                return Result.Failure(added.ErrorCode!, added.ErrorMessage!);
            }

            present.Add(key);
        }

        return Result.Success();
    }

    private async Task CompensateDeletesAsync(IReadOnlyList<string> createdItemIds, CancellationToken cancellationToken)
    {
        foreach (string id in createdItemIds)
        {
            try
            {
                await _items.DeleteItemAsync(ItemId.Parse(id), cancellationToken);
            }
            catch
            {
                // Best-effort compensation only.
            }
        }
    }

    private static bool ValidateFieldChoices(
        IReadOnlyList<BiblatexFieldConflict> conflicts,
        IReadOnlyDictionary<string, string> choices)
    {
        foreach (BiblatexFieldConflict conflict in conflicts)
        {
            if (!choices.TryGetValue(conflict.FieldKey, out string? choice))
            {
                return false;
            }

            if (!string.Equals(choice, BiblatexMappedItemMerge.ChoiceLocal, StringComparison.Ordinal) &&
                !string.Equals(choice, BiblatexMappedItemMerge.ChoiceIncoming, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateLinkChoices(
        BiblatexBatchImportPlan plan,
        IReadOnlyDictionary<string, string> choices)
    {
        foreach (BiblatexSourceMatchGroup group in plan.Groups)
        {
            if (!choices.TryGetValue(group.Source.SourceEntryKey, out string? choice) ||
                string.IsNullOrWhiteSpace(choice))
            {
                return false;
            }

            if (string.Equals(choice, "new", StringComparison.Ordinal))
            {
                continue;
            }

            if (!group.Candidates.Any(candidate =>
                    string.Equals(candidate.ItemId, choice, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<BiblatexFileSkip> CollapseSkips(IEnumerable<BiblatexFileSkip> skips)
    {
        return skips
            .GroupBy(static skip => skip.Reason, StringComparer.Ordinal)
            .Select(static group => new BiblatexFileSkip(group.Key, group.Sum(static skip => skip.Count)))
            .ToArray();
    }

    private static string BuildStatusMessage(int created, int updated, IReadOnlyList<BiblatexFileSkip> skips)
    {
        int success = created + updated;
        int skippedFiles = skips.Sum(static skip => skip.Count);
        if (skippedFiles == 0)
        {
            return $"成功导入 {success} 条。";
        }

        string reasons = string.Join("；", skips.Select(static skip => $"{skip.Reason}×{skip.Count}"));
        return $"成功导入 {success} 条，跳过 {skippedFiles} 个文件，错误原因为{reasons}";
    }
}
