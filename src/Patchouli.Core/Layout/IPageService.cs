using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Layout;

public interface IPageService
{
    Task<Result<Page>> CreatePageAsync(
        DocumentInstanceId documentInstanceId,
        int pageIndex,
        string? pageLabel,
        double? width,
        double? height,
        int rotation,
        string coordinateBasis,
        double? basisWidth,
        double? basisHeight,
        string rendererBasisVersion,
        string? sourceFileHash,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<Page>>> ListPagesAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<Page>> GetPageAsync(
        PageId pageId,
        CancellationToken cancellationToken = default);
}
