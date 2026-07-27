using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

/// <summary>
/// Execution engine used by the OCR queue task executor. External callers should use
/// <see cref="IOcrRunCoordinator"/> instead.
/// </summary>
public interface IOcrRunEngine : IOcrRunCoordinator
{
    Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId presetId,
        CancellationToken cancellationToken = default);

    Task<Result> ReconcileInterruptedRunsAsync(CancellationToken cancellationToken = default);
}
