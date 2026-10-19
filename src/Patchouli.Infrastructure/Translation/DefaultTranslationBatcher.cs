using Patchouli.Core.Translation;

namespace Patchouli.Infrastructure.Translation;

/// <summary>
/// Default greedy batcher for the direct translation pipeline. It fills one batch with consecutive segments
/// while the segment count, character budget and optional token budget all allow it, then starts the next
/// batch. A segment that alone exceeds a budget is placed in a batch of its own: splitting is the
/// segmenter's responsibility, never the batcher's.
/// </summary>
/// <remarks>
/// The token budget uses the same conservative estimate as the pipeline: four characters per token, rounded
/// up. The estimate is only a guard rail; an over-budget single segment is still sent so the pipeline can
/// decide whether to retry or report it.
/// </remarks>
public sealed class DefaultTranslationBatcher : ITranslationBatcher
{
    /// <summary>Characters per token used by the budget estimate.</summary>
    public const int CharactersPerToken = 4;

    /// <inheritdoc />
    public IReadOnlyList<TranslationBatch> CreateBatches(
        IReadOnlyList<TranslationSegment> segments,
        TranslationSettings settings,
        TranslationBatchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(settings);

        if (segments.Count == 0)
        {
            return [];
        }

        int maxSegments = Math.Max(1, settings.BatchSize);
        int maxCharacters = Math.Max(1, settings.MaxBatchCharacters);
        int? maxTokens = settings.MaxBatchTokens is > 0 ? settings.MaxBatchTokens.Value : null;

        List<TranslationBatch> batches = [];
        List<TranslationSegment> current = [];
        int currentCharacters = 0;
        int currentTokens = 0;

        foreach (TranslationSegment segment in segments)
        {
            int segmentCharacters = segment.Text?.Length ?? 0;
            int segmentTokens = EstimateTokens(segmentCharacters);
            bool fits = current.Count > 0 &&
                        current.Count < maxSegments &&
                        currentCharacters + segmentCharacters <= maxCharacters &&
                        (maxTokens is null || currentTokens + segmentTokens <= maxTokens);

            if (!fits && current.Count > 0)
            {
                batches.Add(CreateBatch(batches.Count, current, context));
                current = [];
                currentCharacters = 0;
                currentTokens = 0;
            }

            current.Add(segment);
            currentCharacters += segmentCharacters;
            currentTokens += segmentTokens;
        }

        batches.Add(CreateBatch(batches.Count, current, context));
        return batches;
    }

    /// <summary>Estimates the token cost of a text length; four characters per token, rounded up.</summary>
    public static int EstimateTokens(int characterCount)
    {
        return characterCount <= 0 ? 0 : (characterCount + CharactersPerToken - 1) / CharactersPerToken;
    }

    private static TranslationBatch CreateBatch(
        int index,
        IReadOnlyList<TranslationSegment> segments,
        TranslationBatchContext? context)
    {
        return new TranslationBatch(index, [.. segments], context);
    }
}
