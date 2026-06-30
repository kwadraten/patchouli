using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Search;

/// <summary>Searches durable search units through the configured local search provider.</summary>
public interface ISearchService
{
    Task<Result<SearchResultPage>> SearchLibraryAsync(SearchRequest request, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SearchMatchedUnit>>> GetSearchResultContextAsync(SearchUnitId unitId, int before = 2, int after = 2, CancellationToken cancellationToken = default);
}
