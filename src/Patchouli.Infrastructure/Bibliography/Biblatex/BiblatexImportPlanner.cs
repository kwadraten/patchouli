using System.Text;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Conflicts;

namespace Patchouli.Infrastructure.Bibliography.Biblatex;

public static class BiblatexImportPlanner
{
    public static Result<IReadOnlyList<BiblatexMappedItem>> MapVisibleEntries(
        IEnumerable<BiblatexEntryDto> entries)
    {
        List<BiblatexMappedItem> mapped = [];
        foreach (BiblatexEntryDto entry in entries)
        {
            if (entry.IsXdata)
            {
                continue;
            }

            Result<BiblatexMappedItem> item = BiblatexFieldMapper.MapVisibleEntry(entry);
            if (item.IsFailure)
            {
                return Result<IReadOnlyList<BiblatexMappedItem>>.Failure(item.ErrorCode!, item.ErrorMessage!);
            }

            mapped.Add(item.Value);
        }

        return Result<IReadOnlyList<BiblatexMappedItem>>.Success(mapped);
    }

    public static Result<BiblatexSingleImportPlan> PlanSingleImport(
        BiblatexEntryDto entry,
        ItemMetadata? target)
    {
        if (entry.IsXdata)
        {
            return Result<BiblatexSingleImportPlan>.Failure(
                AppErrorCodes.ValidationFailed,
                "@xdata entries cannot be imported on the item editor path.");
        }

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        if (mapped.IsFailure)
        {
            return Result<BiblatexSingleImportPlan>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        if (target is null)
        {
            return Result<BiblatexSingleImportPlan>.Success(new BiblatexSingleImportPlan(
                mapped.Value,
                [],
                null));
        }

        IReadOnlyList<BiblatexFieldConflict> conflicts =
            BiblatexFieldConflictAnalyzer.FindConflicts(target, mapped.Value);
        ConflictDescriptor? descriptor = null;
        if (conflicts.Count > 0)
        {
            descriptor = ConflictDescriptorMapper.BiblatexItemFieldConflict(
                target.ItemId.ToString(),
                mapped.Value.SourceEntryKey,
                conflicts.Select(static conflict => (
                    conflict.FieldKey,
                    conflict.Label,
                    conflict.LocalValue,
                    conflict.IncomingValue)).ToArray());
        }

        return Result<BiblatexSingleImportPlan>.Success(new BiblatexSingleImportPlan(
            mapped.Value,
            conflicts,
            descriptor));
    }

    public static Result<BiblatexBatchImportPlan> PlanBatchImport(
        IEnumerable<BiblatexEntryDto> entries,
        IEnumerable<BiblatexMatchCandidateSeed> existingItems,
        string? batchId = null)
    {
        Result<IReadOnlyList<BiblatexMappedItem>> mapped = MapVisibleEntries(entries);
        if (mapped.IsFailure)
        {
            return Result<BiblatexBatchImportPlan>.Failure(mapped.ErrorCode!, mapped.ErrorMessage!);
        }

        BiblatexMatchCandidateSeed[] seeds = existingItems.ToArray();
        List<BiblatexSourceMatchGroup> groups = [];
        bool anyCandidates = false;
        foreach (BiblatexMappedItem source in mapped.Value)
        {
            IReadOnlyList<BiblatexMatchCandidate> candidates =
                BiblatexCandidateMatcher.FindCandidates(source, seeds);
            if (candidates.Count > 0)
            {
                anyCandidates = true;
            }

            groups.Add(new BiblatexSourceMatchGroup(source, candidates));
        }

        ConflictDescriptor? descriptor = null;
        if (anyCandidates)
        {
            string id = string.IsNullOrWhiteSpace(batchId) ? Guid.NewGuid().ToString("N") : batchId;
            descriptor = ConflictDescriptorMapper.BiblatexBatchLinkCandidates(
                id,
                groups.Select(static group => (
                    group.Source.SourceEntryKey,
                    group.Source.Title,
                    group.Candidates.Select(static candidate => (
                            candidate.ItemId,
                            candidate.Title,
                            candidate.MatchCount)).ToArray() as
                        IReadOnlyList<(string ItemId, string Title, int MatchCount)>)).ToArray());
        }

        return Result<BiblatexBatchImportPlan>.Success(new BiblatexBatchImportPlan(
            groups,
            anyCandidates,
            descriptor));
    }

    public static Result ReadUtf8Strict(byte[] bytes, out string text)
    {
        Encoding utf8 = new UTF8Encoding(false, true);
        try
        {
            text = utf8.GetString(bytes);
            return Result.Success();
        }
        catch (DecoderFallbackException ex)
        {
            text = string.Empty;
            return Result.Failure(
                AppErrorCodes.BiblatexEncodingError,
                $"BibLaTeX input is not valid UTF-8: {ex.Message}");
        }
    }
}
