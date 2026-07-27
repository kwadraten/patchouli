namespace Patchouli.Infrastructure.Files;

/// <summary>
/// macOS-specific file access adapter that resolves Finder aliases and materializes
/// iCloud Drive placeholders before Patchouli reads PDF files.
/// </summary>
public sealed class MacOSNativeFileAccessAdapter : INativeFileAccessAdapter
{
    private const int DefaultMaterializeTimeoutMs = 5000;
    private readonly IMacOSFileSystemInterop _interop;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _materializeTimeout;

    public MacOSNativeFileAccessAdapter()
        : this(new PInvokeMacOSFileSystemInterop(), TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(DefaultMaterializeTimeoutMs))
    {
    }

    internal MacOSNativeFileAccessAdapter(IMacOSFileSystemInterop interop, TimeSpan pollInterval,
        TimeSpan materializeTimeout)
    {
        _interop = interop;
        _pollInterval = pollInterval;
        _materializeTimeout = materializeTimeout;
    }

    public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            MacOSNativeCallResult result = _interop.ResolvePath(path);
            return result.Code == 0
                ? ValueTask.FromResult(new NativeDirectoryResolution(result.Path ?? path))
                : ValueTask.FromResult(new NativeDirectoryResolution(null,
                    result.Code == -2 ? "access_denied" : "io_error", result.Error));
        }
        catch (UnauthorizedAccessException exception)
        {
            return ValueTask.FromResult(
                new NativeDirectoryResolution(null, PortableNativeFileAccessAdapter.Classify(exception),
                    exception.Message));
        }
        catch (IOException exception)
        {
            return ValueTask.FromResult(
                new NativeDirectoryResolution(null, PortableNativeFileAccessAdapter.Classify(exception),
                    exception.Message));
        }
    }

    public async ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource timeoutSource = new(_materializeTimeout);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            while (!linkedSource.Token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MacOSNativeCallResult result = _interop.MaterializeFile(path);
                if (result.Code == 0)
                {
                    return new NativeFileMaterialization(true);
                }

                if (result.Code < 0)
                {
                    return new NativeFileMaterialization(false, result.Code == -2 ? "access_denied" : "io_error",
                        result.Error);
                }

                // Code 1 means the file is an iCloud placeholder and the download was started.
                // Wait a short interval before asking the helper again.
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }

            return timeoutSource.Token.IsCancellationRequested
                ? new NativeFileMaterialization(false, "icloud_not_downloaded",
                    "iCloud file download timed out.")
                : new NativeFileMaterialization(false, "cancelled", "File materialization was cancelled.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NativeFileMaterialization(false, "icloud_not_downloaded",
                "iCloud file download timed out.");
        }
    }
}
