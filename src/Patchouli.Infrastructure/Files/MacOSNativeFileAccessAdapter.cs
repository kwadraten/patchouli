namespace Patchouli.Infrastructure.Files;

/// <summary>
/// macOS-specific file access adapter that resolves Finder aliases and materializes
/// iCloud Drive placeholders before Patchouli reads PDF files.
/// </summary>
public sealed class MacOSNativeFileAccessAdapter : INativeFileAccessAdapter
{
    private const int DefaultNativeCallTimeoutMs = 3000;
    private readonly IMacOSFileSystemInterop _interop;
    private readonly TimeSpan _nativeCallTimeout;

    public MacOSNativeFileAccessAdapter()
        : this(new PInvokeMacOSFileSystemInterop(), TimeSpan.FromMilliseconds(DefaultNativeCallTimeoutMs))
    {
    }

    internal MacOSNativeFileAccessAdapter(IMacOSFileSystemInterop interop, TimeSpan nativeCallTimeout)
    {
        _interop = interop;
        _nativeCallTimeout = nativeCallTimeout;
    }

    public async ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            MacOSNativeCallResult result = await RunNativeCall(() => _interop.ResolvePath(path),
                cancellationToken);
            return result.Code == 0
                ? new NativeDirectoryResolution(result.Path ?? path)
                : new NativeDirectoryResolution(null,
                    result.Code == -2 ? "access_denied" : "io_error", result.Error);
        }
        catch (TimeoutException)
        {
            return new NativeDirectoryResolution(null, "resolve_timeout",
                $"Directory resolution timed out after {_nativeCallTimeout.TotalMilliseconds:0} ms: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new NativeDirectoryResolution(null, PortableNativeFileAccessAdapter.Classify(exception),
                exception.Message);
        }
    }

    public async ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            MacOSNativeCallResult result = await RunNativeCall(() => _interop.MaterializeFile(path),
                cancellationToken);
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
            // Skip the file instead of polling: the file-search-root watcher triggers a rescan
            // once the download lands on disk, which picks the file up.
            return new NativeFileMaterialization(false, "icloud_not_downloaded",
                "iCloud file is not downloaded; the download was started and the file will be " +
                "picked up by the next rescan.");
        }
        catch (TimeoutException)
        {
            return new NativeFileMaterialization(false, "icloud_not_downloaded",
                $"File materialization timed out after {_nativeCallTimeout.TotalMilliseconds:0} ms: {path}");
        }
    }

    private async Task<MacOSNativeCallResult> RunNativeCall(Func<MacOSNativeCallResult> call,
        CancellationToken cancellationToken)
    {
        // Native Foundation calls cannot be cancelled once entered; run them on a background
        // thread and bound the wait instead. On timeout the abandoned task may stay blocked
        // inside the OS call until process exit. That leak is bounded by the number of wedged
        // paths and only occurs when the OS call itself hangs (dead Finder alias, unreachable
        // network volume, pending TCC handling).
        return await Task.Run(call, cancellationToken).WaitAsync(_nativeCallTimeout, cancellationToken);
    }
}
