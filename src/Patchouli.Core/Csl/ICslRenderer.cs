using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

public interface ICslRenderer
{
    Task<Result<CslRenderResult>> RenderAsync(CslRenderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a single in-text citation cluster with per-item modifiers. Unlike
    /// <see cref="RenderAsync"/>, an empty bibliography is not a failure here:
    /// citation-only styles are valid cluster styles.
    /// </summary>
    Task<Result<CslClusterRenderResult>> RenderClusterAsync(CslClusterRenderRequest request,
        CancellationToken cancellationToken = default);
}
