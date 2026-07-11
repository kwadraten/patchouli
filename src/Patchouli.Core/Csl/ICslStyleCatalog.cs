using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

public interface ICslStyleCatalog
{
    IReadOnlyList<CslCatalogSource> Sources { get; }
    CslCatalogSource CurrentSource { get; }
    Result SetSource(string sourceId);
    Task<Result<IReadOnlyList<CslCatalogStyle>>> RefreshAsync(CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<CslCatalogStyle>>> SearchAsync(string? query = null,
        CancellationToken cancellationToken = default);
}
