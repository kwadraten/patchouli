using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Pagination;

public interface IChapterScanService
{
    Task<Result<ChapterScanResult>> ScanAsync(
        DocumentInstanceId documentInstanceId,
        ChapterScanOptions? options = null,
        IProgress<ChapterScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
