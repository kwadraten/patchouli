using System.Runtime.InteropServices;
using System.Text;

namespace Patchouli.Infrastructure.Files;

/// <summary>
/// macOS-specific file access adapter that resolves Finder aliases and materializes
/// iCloud Drive placeholders before Patchouli reads PDF files.
/// </summary>
public sealed class MacOSNativeFileAccessAdapter : INativeFileAccessAdapter
{
    private const string DllName = "libpatchouli-macos-fs";
    private const int DefaultMaterializeTimeoutMs = 5000;
    private const int PollIntervalMs = 50;
    private const int PathBufferSize = 4096;
    private const int ErrorBufferSize = 1024;

    public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string resolved = ResolvePath(path);
            return ValueTask.FromResult(new NativeDirectoryResolution(resolved));
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

        using CancellationTokenSource timeoutSource = new(DefaultMaterializeTimeoutMs);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            while (!linkedSource.Token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                MaterializeResult result = MaterializeFile(path, 0);
                if (result.Code == 0)
                {
                    return new NativeFileMaterialization(true);
                }

                if (result.Code == -1)
                {
                    return new NativeFileMaterialization(false, "io_error", result.Error);
                }

                // Code 1 means the file is an iCloud placeholder and the download was started.
                // Wait a short interval before asking the helper again.
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
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

    private static string ResolvePath(string path)
    {
        (int code, string? resolved, string error) = InvokeWithBuffers((outBuf, errBuf) => patchouli_resolve_path(path,
            outBuf, (nuint)PathBufferSize, errBuf,
            (nuint)ErrorBufferSize));
        if (code != 0)
        {
            throw new IOException(error);
        }

        return resolved ?? path;
    }

    private static MaterializeResult MaterializeFile(string path, uint timeoutMs)
    {
        (int code, string? resolved, string error) = InvokeWithBuffers((outBuf, errBuf) => patchouli_materialize_file(
            path, outBuf, (nuint)PathBufferSize, errBuf,
            (nuint)ErrorBufferSize, timeoutMs));
        return new MaterializeResult(code, error);
    }

    private static (int Code, string? Path, string Error) InvokeWithBuffers(Func<IntPtr, IntPtr, int> invoke)
    {
        IntPtr outBuffer = IntPtr.Zero;
        IntPtr errBuffer = IntPtr.Zero;
        try
        {
            outBuffer = Marshal.AllocHGlobal(PathBufferSize);
            errBuffer = Marshal.AllocHGlobal(ErrorBufferSize);
            int result = invoke(outBuffer, errBuffer);
            string? path = PtrToStringUtf8(outBuffer);
            string error = PtrToStringUtf8(errBuffer) ?? "Unknown error.";
            return (result, path, error);
        }
        finally
        {
            if (outBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(outBuffer);
            }

            if (errBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(errBuffer);
            }
        }
    }

    private static string? PtrToStringUtf8(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        int length = 0;
        while (Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
            if (length >= PathBufferSize)
            {
                break;
            }
        }

        if (length == 0)
        {
            return null;
        }

        byte[] bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int patchouli_resolve_path(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr outBuffer,
        nuint outLen,
        IntPtr errBuffer,
        nuint errLen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int patchouli_materialize_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr outBuffer,
        nuint outLen,
        IntPtr errBuffer,
        nuint errLen,
        uint timeoutMs);

    private sealed record MaterializeResult(int Code, string Error);
}
