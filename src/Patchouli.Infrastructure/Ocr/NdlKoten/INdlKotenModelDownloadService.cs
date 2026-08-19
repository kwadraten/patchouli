using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public interface INdlKotenModelDownloadService
{
    /// <summary>
    /// Downloads all missing or incomplete model/config files to the configured models directory.
    /// Already-complete files are skipped. Progress is reported as a value between 0.0 and 1.0.
    /// </summary>
    Task<Result> DownloadAllAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
