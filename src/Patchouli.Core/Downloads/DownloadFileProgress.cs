using Patchouli.Core.Results;

namespace Patchouli.Core.Downloads;

public sealed record DownloadFileProgress(
    string RelativePath,
    string State,
    long DownloadedBytes,
    long? TotalBytes,
    string? ErrorMessage = null);

public interface IDownloadProgress : IProgress<double>
{
    void ReportFile(DownloadFileProgress value);
}

public interface IDownloadTaskRunner
{
    Task<Result> RunAsync(string displayName,
        Func<IProgress<double>?, CancellationToken, Task<Result>> download,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<DownloadFileProgress>? files = null);
}
