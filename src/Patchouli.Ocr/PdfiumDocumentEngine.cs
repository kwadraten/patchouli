using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using PDFiumCore;

namespace Patchouli.Ocr;

public sealed class PdfiumDocumentException : Exception
{
    public PdfiumDocumentException(string message) : base(message)
    {
    }
}

public sealed record PdfiumPageBitmap(byte[] BgraBytes, int Width, int Height, int Stride);

public sealed class PdfiumDocumentEngine
{
    public const string Version = "151.0.7920";
    private const int RenderAnnotations = 0x01;
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static readonly object InitializationGate = new();
    private static readonly ConcurrentDictionary<IntPtr, Stream> SaveStreams = new();
    private static bool _initialized;

    public async Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        _ = await ExecuteAsync(() => true, cancellationToken);
    }

    public async Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(() =>
        {
            FpdfDocumentT document = OpenDocument(pdfPath);
            try
            {
                return fpdfview.FPDF_GetPageCount(document);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }, cancellationToken);
    }

    public async Task<PdfiumPageBitmap> RenderPageAsync(string pdfPath, int pageIndex, int dpi,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(() =>
        {
            FpdfDocumentT document = OpenDocument(pdfPath);
            try
            {
                int pageCount = fpdfview.FPDF_GetPageCount(document);
                if (pageIndex < 0 || pageIndex >= pageCount)
                {
                    throw new InvalidOperationException("Page index is outside the PDF page range.");
                }

                FpdfPageT page = fpdfview.FPDF_LoadPage(document, pageIndex);
                EnsureHandle(page, "page");
                try
                {
                    int width = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageWidthF(page) * dpi / 72d));
                    int height = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageHeightF(page) * dpi / 72d));
                    FpdfBitmapT bitmap = fpdfview.FPDFBitmapCreate(width, height, 1);
                    EnsureHandle(bitmap, "bitmap");
                    try
                    {
                        _ = fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations);
                        int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                        byte[] bytes = new byte[checked(stride * height)];
                        Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bitmap), bytes, 0, bytes.Length);
                        return new PdfiumPageBitmap(bytes, width, height, stride);
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(bitmap);
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }
        }, cancellationToken);
    }

    public async Task ExtractPagesAsync(string sourcePath, string outputPath, int startPageIndex, int pageCount,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(() =>
        {
            FpdfDocumentT source = OpenDocument(sourcePath);
            FpdfDocumentT destination = fpdf_edit.FPDF_CreateNewDocument();
            EnsureHandle(destination, "destination document");
            try
            {
                int sourcePageCount = fpdfview.FPDF_GetPageCount(source);
                if (startPageIndex < 0 || pageCount <= 0 || startPageIndex + pageCount > sourcePageCount)
                {
                    throw new InvalidOperationException("The requested PDF page range is invalid.");
                }

                string range = $"{startPageIndex + 1}-{startPageIndex + pageCount}";
                if (fpdf_ppo.FPDF_ImportPages(destination, source, range, 0) == 0)
                {
                    throw LastError("PDF pages could not be imported");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                using FileStream stream = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                FPDF_FILEWRITE_ writer = new() { Version = 1 };
                writer.WriteBlock = WriteRegisteredBlock;
                SaveStreams[writer.__Instance] = stream;
                try
                {
                    if (fpdf_save.FPDF_SaveAsCopy(destination, writer, 0) == 0)
                    {
                        throw LastError("PDF pages could not be saved");
                    }
                }
                finally
                {
                    _ = SaveStreams.TryRemove(writer.__Instance, out _);
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(destination);
                fpdfview.FPDF_CloseDocument(source);
            }
        }, cancellationToken);
    }

    private static async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await NativeGate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        catch (DllNotFoundException exception)
        {
            throw new PdfRendererUnavailableException("PDFium native runtime could not be loaded.", exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new PdfRendererUnavailableException("PDFium native runtime has an incompatible architecture.",
                exception);
        }
        finally
        {
            NativeGate.Release();
        }
    }

    private static Task ExecuteAsync(Action action, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);
    }

    private static void EnsureInitialized()
    {
        lock (InitializationGate)
        {
            if (_initialized)
            {
                return;
            }

            fpdfview.FPDF_InitLibrary();
            _initialized = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (InitializationGate)
                {
                    if (_initialized)
                    {
                        fpdfview.FPDF_DestroyLibrary();
                        _initialized = false;
                    }
                }
            };
        }
    }

    private static FpdfDocumentT OpenDocument(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("PDF source file was not found.", path);
        }

        FpdfDocumentT document = fpdfview.FPDF_LoadDocument(path, null!);
        EnsureHandle(document, "document");
        return document;
    }

    private static void EnsureHandle(object handle, string description)
    {
        IntPtr instance = handle switch
        {
            FpdfDocumentT value => value.__Instance,
            FpdfPageT value => value.__Instance,
            FpdfBitmapT value => value.__Instance,
            _ => IntPtr.Zero
        };
        if (instance == IntPtr.Zero)
        {
            throw LastError($"PDFium did not create the {description}");
        }
    }

    private static PdfiumDocumentException LastError(string operation)
    {
        ulong error = fpdfview.FPDF_GetLastError();
        return new PdfiumDocumentException($"{operation} (PDFium error {error}).");
    }

    private static int WriteBlock(Stream stream, IntPtr data, ulong size)
    {
        const int bufferSize = 81920;
        byte[] buffer = new byte[bufferSize];
        ulong offset = 0;
        while (offset < size)
        {
            int count = (int)Math.Min((ulong)buffer.Length, size - offset);
            Marshal.Copy(IntPtr.Add(data, checked((int)offset)), buffer, 0, count);
            stream.Write(buffer, 0, count);
            offset += (ulong)count;
        }

        return 1;
    }

    private static int WriteRegisteredBlock(IntPtr writer, IntPtr data, ulong size)
    {
        return SaveStreams.TryGetValue(writer, out Stream? stream) ? WriteBlock(stream, data, size) : 0;
    }
}
