using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

public interface IItemTypeInferenceService
{
    Task<Result<ItemTypeInference>> SuggestAsync(
        ItemId itemId,
        string suggestedType,
        double confidence,
        string source,
        string? evidenceSummary,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ItemTypeInference>>> ListSuggestionsAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result<ItemTypeInference>> AcceptSuggestionAsync(
        string inferenceId,
        CancellationToken cancellationToken = default);
}
