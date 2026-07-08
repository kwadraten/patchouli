using Patchouli.Core.Results;

namespace Patchouli.Core.Csl;

public interface ICslRenderer
{
    Task<Result<CslRenderResult>> RenderAsync(CslRenderRequest request, CancellationToken cancellationToken = default);
}
