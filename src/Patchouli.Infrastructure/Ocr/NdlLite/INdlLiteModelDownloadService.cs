using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Downloads the official ndl-lab/ndlocr-lite detector, recognizer cascade,
/// class list and character set into a model directory.
/// </summary>
public interface INdlLiteModelDownloadService
{
    /// <summary>
    /// Downloads all missing or incomplete manifest files. Already-complete files
    /// are skipped. Progress is reported as a value between 0.0 and 1.0.
    /// </summary>
    Task<Result> DownloadAllAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
