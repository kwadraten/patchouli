using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Default merger for validated batch results. Alignment is strictly by
/// <see cref="TranslationSegmentTranslation.SegmentId"/>; the order of results and of translations inside a
/// result is never meaningful. Missing, duplicate, unknown and structurally inconsistent ids fail with
/// <see cref="TranslationErrorCodes.BatchMismatch"/> and expose the offending ids through
/// <see cref="TranslationBatchMismatchDetails"/>. For one box, parts are ordered by ascending
/// <see cref="TranslationSegmentTranslation.PartIndex"/> before concatenation.
/// </summary>
public sealed class DefaultTranslationResultProcessor : ITranslationResultProcessor
{
    /// <inheritdoc />
    public Result<IReadOnlyDictionary<DocumentBoxId, string>> Merge(
        IReadOnlyList<TranslationBatchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return Result<IReadOnlyDictionary<DocumentBoxId, string>>.Success(
                new Dictionary<DocumentBoxId, string>());
        }

        Dictionary<string, int> expectedCounts = new(StringComparer.Ordinal);
        Dictionary<string, TranslationSegmentTranslation> translations = new(StringComparer.Ordinal);
        HashSet<string> duplicateIds = new(StringComparer.Ordinal);
        HashSet<string> unknownIds = new(StringComparer.Ordinal);
        List<string> structuralErrors = [];

        foreach (TranslationBatchResult result in results)
        {
            if (result is null)
            {
                structuralErrors.Add("A null batch result was supplied.");
                continue;
            }

            IReadOnlyList<string> requested = result.RequestedSegmentIds ?? [];
            IReadOnlyList<TranslationSegmentTranslation> resultTranslations = result.Translations ?? [];
            List<string> requestedIds = [.. requested];
            List<string> localExpected = [.. requestedIds.Distinct(StringComparer.Ordinal)];
            foreach (string requestedId in requestedIds)
            {
                if (string.IsNullOrWhiteSpace(requestedId))
                {
                    structuralErrors.Add("A batch result requested an empty segment id.");
                    continue;
                }

                expectedCounts.TryGetValue(requestedId, out int count);
                expectedCounts[requestedId] = count + 1;
                if (count > 0)
                {
                    duplicateIds.Add(requestedId);
                }
            }

            foreach (TranslationSegmentTranslation translation in resultTranslations)
            {
                if (translation is null)
                {
                    structuralErrors.Add("A batch result contained a null translation.");
                    continue;
                }

                string segmentId = translation.SegmentId ?? "";
                if (!localExpected.Contains(segmentId, StringComparer.Ordinal))
                {
                    unknownIds.Add(segmentId);
                    continue;
                }

                if (!translations.TryAdd(segmentId, translation))
                {
                    duplicateIds.Add(segmentId);
                    continue;
                }

                if (translation.PartIndex < 1 ||
                    !string.Equals(
                        TranslationSegmentIds.Create(translation.BoxId, translation.PartIndex),
                        segmentId,
                        StringComparison.Ordinal))
                {
                    structuralErrors.Add(
                        $"Segment '{segmentId}' declares box '{translation.BoxId}' part " +
                        $"{translation.PartIndex}, which does not form that segment id.");
                }
            }
        }

        List<string> missingIds = expectedCounts
            .Where(pair => !translations.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (missingIds.Count > 0 || duplicateIds.Count > 0 || unknownIds.Count > 0 ||
            structuralErrors.Count > 0)
        {
            return Result<IReadOnlyDictionary<DocumentBoxId, string>>.Failure(
                TranslationErrorCodes.BatchMismatch,
                DescribeMismatch(missingIds, duplicateIds, unknownIds, structuralErrors),
                details: CreateDetails(missingIds, duplicateIds, unknownIds));
        }

        Dictionary<DocumentBoxId, List<TranslationSegmentTranslation>> byBox = [];
        foreach (TranslationSegmentTranslation translation in translations.Values)
        {
            if (!byBox.TryGetValue(translation.BoxId, out List<TranslationSegmentTranslation>? parts))
            {
                parts = [];
                byBox[translation.BoxId] = parts;
            }

            parts.Add(translation);
        }

        Dictionary<DocumentBoxId, string> merged = new(byBox.Count);
        foreach ((DocumentBoxId boxId, List<TranslationSegmentTranslation> parts) in byBox)
        {
            parts.Sort(static (left, right) => left.PartIndex.CompareTo(right.PartIndex));
            merged[boxId] = string.Concat(parts.Select(static part => part.TranslatedText ?? ""));
        }

        return Result<IReadOnlyDictionary<DocumentBoxId, string>>.Success(merged);
    }

    private static TranslationBatchMismatchDetails? CreateDetails(
        IReadOnlyList<string> missingIds,
        IEnumerable<string> duplicateIds,
        IEnumerable<string> unknownIds)
    {
        string[] missing = [.. missingIds.OrderBy(id => id, StringComparer.Ordinal)];
        string[] duplicate = [.. duplicateIds.OrderBy(id => id, StringComparer.Ordinal)];
        string[] unknown = [.. unknownIds.OrderBy(id => id, StringComparer.Ordinal)];
        return missing.Length == 0 && duplicate.Length == 0 && unknown.Length == 0
            ? null
            : new TranslationBatchMismatchDetails(missing, duplicate, unknown);
    }

    private static string DescribeMismatch(
        IReadOnlyList<string> missingIds,
        IEnumerable<string> duplicateIds,
        IEnumerable<string> unknownIds,
        IReadOnlyList<string> structuralErrors)
    {
        List<string> parts = [];
        if (missingIds.Count > 0)
        {
            parts.Add($"missing ids: {string.Join(", ", missingIds)}");
        }

        string[] duplicate = [.. duplicateIds.OrderBy(id => id, StringComparer.Ordinal)];
        if (duplicate.Length > 0)
        {
            parts.Add($"duplicate ids: {string.Join(", ", duplicate)}");
        }

        string[] unknown = [.. unknownIds.OrderBy(id => id, StringComparer.Ordinal)];
        if (unknown.Length > 0)
        {
            parts.Add($"unknown ids: {string.Join(", ", unknown)}");
        }

        parts.AddRange(structuralErrors);
        return $"Translation batch results do not align with the requested segments ({string.Join("; ", parts)}).";
    }
}
