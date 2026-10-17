using Patchouli.Core.Results;

namespace Patchouli.Core.View;

/// <summary>
/// Issue #11: reads source images from the ORIGINAL PDF for model text-image collation.
/// Images are never re-rendered from OCR text and never substituted across revisions,
/// pages or sources: a missing/changed source, a missing target or stale coordinates
/// is an explicit failure.
/// </summary>
public interface IPageImageViewService
{
    Task<Result<PageImageViewResult>> ViewAsync(
        PageImageViewRequest request,
        CancellationToken cancellationToken = default);
}
