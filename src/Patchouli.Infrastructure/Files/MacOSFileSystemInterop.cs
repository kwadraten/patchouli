using System.Runtime.InteropServices;
using System.Text;

namespace Patchouli.Infrastructure.Files;

internal interface IMacOSFileSystemInterop
{
    MacOSNativeCallResult ResolvePath(string path);

    MacOSNativeCallResult MaterializeFile(string path);
}

internal sealed record MacOSNativeCallResult(int Code, string? Path, string Error);

internal sealed class PInvokeMacOSFileSystemInterop : IMacOSFileSystemInterop
{
    private const string DllName = "libpatchouli-macos-fs";
    private const int PathBufferSize = 4096;
    private const int ErrorBufferSize = 1024;

    public MacOSNativeCallResult ResolvePath(string path)
    {
        return InvokeWithBuffers((outBuf, errBuf) => patchouli_resolve_path(path, outBuf, (nuint)PathBufferSize,
            errBuf, (nuint)ErrorBufferSize));
    }

    public MacOSNativeCallResult MaterializeFile(string path)
    {
        return InvokeWithBuffers((outBuf, errBuf) => patchouli_materialize_file(path, outBuf,
            (nuint)PathBufferSize, errBuf, (nuint)ErrorBufferSize, 0));
    }

    private static MacOSNativeCallResult InvokeWithBuffers(Func<IntPtr, IntPtr, int> invoke)
    {
        IntPtr outBuffer = IntPtr.Zero;
        IntPtr errBuffer = IntPtr.Zero;
        try
        {
            outBuffer = Marshal.AllocHGlobal(PathBufferSize);
            errBuffer = Marshal.AllocHGlobal(ErrorBufferSize);
            Marshal.Copy(new byte[PathBufferSize], 0, outBuffer, PathBufferSize);
            Marshal.Copy(new byte[ErrorBufferSize], 0, errBuffer, ErrorBufferSize);
            int result = invoke(outBuffer, errBuffer);
            return new MacOSNativeCallResult(result, PtrToStringUtf8(outBuffer, PathBufferSize),
                PtrToStringUtf8(errBuffer, ErrorBufferSize) ?? "Unknown error.");
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

    private static string? PtrToStringUtf8(IntPtr pointer, int bufferSize)
    {
        int length = 0;
        while (length < bufferSize && Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
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
}
