using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace Patchouli.Ocr.Native;

/// <summary>One page's size/rotation read inside a single scheduled native unit.</summary>
internal readonly record struct PdfiumPageInfoData(double Width, double Height, int Rotation);

/// <summary>Per-page outcome of a page-info read; failures stay per page (ADR 0035).</summary>
internal readonly record struct PdfiumPageInfoEntry(bool Success, PdfiumPageInfoData? Info, string? ErrorMessage);

/// <summary>
/// The only layer allowed to touch PDFium native entry points (issue #31). Every method in
/// this class must be called from inside a <see cref="PdfiumScheduler"/> unit, i.e. on the
/// dedicated PDFium worker thread; callers outside this namespace never see a native handle
/// type or a native entry point. Native error codes are read immediately after the failing
/// call, inside the same scheduled unit, and sealed into <see cref="PdfiumDocumentException"/>.
/// </summary>
internal static class PdfiumNative
{
    private const int RenderAnnotations = 0x01;
    private static readonly object InitializationGate = new();
    private static readonly ConcurrentDictionary<IntPtr, Stream> SaveStreams = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        lock (InitializationGate)
        {
            if (_initialized)
            {
                return;
            }

            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    /// <summary>Destroys the library. Must run on the worker thread after all documents close.</summary>
    internal static void DestroyLibrary()
    {
        lock (InitializationGate)
        {
            if (!_initialized)
            {
                return;
            }

            fpdfview.FPDF_DestroyLibrary();
            _initialized = false;
        }
    }

    internal static FpdfDocumentT LoadDocument(string path)
    {
        EnsureInitialized();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("PDF source file was not found.", path);
        }

        FpdfDocumentT document = fpdfview.FPDF_LoadDocument(path, null!);
        EnsureHandle(document, "document", path);
        return document;
    }

    internal static int GetPageCount(FpdfDocumentT document)
    {
        return fpdfview.FPDF_GetPageCount(document);
    }

    internal static int GetPageCountForPath(string path)
    {
        FpdfDocumentT document = LoadDocument(path);
        try
        {
            return fpdfview.FPDF_GetPageCount(document);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    internal static void CloseDocument(FpdfDocumentT document)
    {
        fpdfview.FPDF_CloseDocument(document);
    }

    /// <summary>
    /// Renders one page as a single native unit: load page, measure, create bitmap, render,
    /// copy pixels back to managed memory, destroy bitmap, close page. The page handle never
    /// leaves this unit.
    /// </summary>
    internal static PdfiumPageBitmap RenderPage(FpdfDocumentT document, int pageIndex, int dpi, string? path)
    {
        int pageCount = fpdfview.FPDF_GetPageCount(document);
        if (pageIndex < 0 || pageIndex >= pageCount)
        {
            throw new InvalidOperationException("Page index is outside the PDF page range.");
        }

        FpdfPageT page = fpdfview.FPDF_LoadPage(document, pageIndex);
        EnsureHandle(page, "page", path);
        try
        {
            int width = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageWidthF(page) * dpi / 72d));
            int height = Math.Max(1, (int)Math.Ceiling(fpdfview.FPDF_GetPageHeightF(page) * dpi / 72d));
            FpdfBitmapT bitmap = fpdfview.FPDFBitmapCreate(width, height, 1);
            EnsureHandle(bitmap, "bitmap", path);
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

    /// <summary>
    /// Reads one page's complete native text layer inside a single native unit: load page,
    /// open the PDFium text page, walk every character index in native order, then close the
    /// text page and page. No rendering and no second PDF parser. Characters that fail an
    /// individual read keep their index so the source anchor stream stays intact; a page
    /// with no text object returns an empty glyph list rather than an exception.
    /// </summary>
    internal static PdfiumTextPage ReadTextPage(FpdfDocumentT document, int pageIndex, string? path)
    {
        int pageCount = fpdfview.FPDF_GetPageCount(document);
        if (pageIndex < 0 || pageIndex >= pageCount)
        {
            throw new InvalidOperationException("Page index is outside the PDF page range.");
        }

        FpdfPageT page = fpdfview.FPDF_LoadPage(document, pageIndex);
        EnsureHandle(page, "page", path);
        FpdfTextpageT? textPage = null;
        try
        {
            double pageWidth = fpdfview.FPDF_GetPageWidthF(page);
            double pageHeight = fpdfview.FPDF_GetPageHeightF(page);
            float left = 0, bottom = 0, right = (float)pageWidth, top = (float)pageHeight;
            if (fpdf_transformpage.FPDFPageGetMediaBox(page, ref left, ref bottom, ref right, ref top) == 0)
            {
                left = 0;
                bottom = 0;
                right = (float)pageWidth;
                top = (float)pageHeight;
            }

            PdfPageBox mediaBox = new(left, bottom, right, top);
            float cropLeft = 0, cropBottom = 0, cropRight = 0, cropTop = 0;
            PdfPageBox effectiveBox = mediaBox;
            if (fpdf_transformpage.FPDFPageGetCropBox(page, ref cropLeft, ref cropBottom,
                    ref cropRight, ref cropTop) != 0)
            {
                PdfPageBox clipped = mediaBox.Intersect(
                    new PdfPageBox(cropLeft, cropBottom, cropRight, cropTop));
                if (!clipped.IsDegenerate)
                {
                    effectiveBox = clipped;
                }
            }

            int rotation = fpdf_edit.FPDFPageGetRotation(page) switch
            {
                1 => 90,
                2 => 180,
                3 => 270,
                _ => 0
            };

            textPage = fpdf_text.FPDFTextLoadPage(page);
            if (textPage is null || textPage.__Instance == IntPtr.Zero)
            {
                return new PdfiumTextPage(mediaBox, effectiveBox, rotation, 0,
                    Array.Empty<PdfiumTextGlyph>(), pageWidth, pageHeight);
            }

            int charCount = fpdf_text.FPDFTextCountChars(textPage);
            List<PdfiumTextGlyph> glyphs = new(Math.Max(0, charCount));
            for (int index = 0; index < charCount; index++)
            {
                glyphs.Add(ReadGlyph(textPage, index, charCount));
            }

            return new PdfiumTextPage(mediaBox, effectiveBox, rotation, charCount, glyphs,
                pageWidth, pageHeight);
        }
        finally
        {
            if (textPage is { } openTextPage && openTextPage.__Instance != IntPtr.Zero)
            {
                fpdf_text.FPDFTextClosePage(openTextPage);
            }

            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Reads one character in-place. The character index is its permanent source anchor —
    /// readers never reorder or drop indices even when a sub-read fails.
    /// </summary>
    private static PdfiumTextGlyph ReadGlyph(FpdfTextpageT textPage, int index, int charCount)
    {
        PdfiumTextGlyphFlags flags = PdfiumTextGlyphFlags.None;
        if (fpdf_text.FPDFTextIsGenerated(textPage, index) != 0)
        {
            flags |= PdfiumTextGlyphFlags.Generated;
        }

        if (fpdf_text.FPDFTextIsHyphen(textPage, index) != 0)
        {
            flags |= PdfiumTextGlyphFlags.Hyphen;
        }

        if (fpdf_text.FPDFTextHasUnicodeMapError(textPage, index) != 0)
        {
            flags |= PdfiumTextGlyphFlags.UnicodeMapError;
        }

        (string text, bool continuation) = ReadGlyphText(textPage, index, charCount);
        if (continuation)
        {
            // The low-surrogate half of a supplementary scalar: the text lives on the
            // previous index, so this index is trustworthy but contributes no text.
            flags |= PdfiumTextGlyphFlags.SurrogateContinuation;
        }
        else if (text.Length == 0)
        {
            flags |= PdfiumTextGlyphFlags.NoUnicodeMapping;
        }

        double left = 0, right = 0, bottom = 0, top = 0;
        _ = fpdf_text.FPDFTextGetCharBox(textPage, index, ref left, ref right, ref bottom, ref top);
        double originX = 0, originY = 0;
        _ = fpdf_text.FPDFTextGetCharOrigin(textPage, index, ref originX, ref originY);
        double angle = fpdf_text.FPDFTextGetCharAngle(textPage, index);
        double fontSize = fpdf_text.FPDFTextGetFontSize(textPage, index);
        (string? fontName, int fontFlags) = ReadGlyphFont(textPage, index);
        return new PdfiumTextGlyph(index, text, left, right, bottom, top, originX, originY,
            angle, flags, fontName, fontFlags, fontSize);
    }

    /// <summary>
    /// Reads the character as UTF-16 text so supplementary-plane scalars stay intact —
    /// a surrogate pair occupies two PDFium indices: the first carries the whole scalar, the
    /// second is reported as a continuation so the index domain stays exact while the
    /// character stream never emits a lone surrogate.
    /// </summary>
    private static (string Text, bool Continuation) ReadGlyphText(FpdfTextpageT textPage,
        int index, int charCount)
    {
        ushort[] buffer = new ushort[8];
        int written = fpdf_text.FPDFTextGetText(textPage, index, 1, ref buffer[0]);
        if (written <= 1)
        {
            uint code = fpdf_text.FPDFTextGetUnicode(textPage, index);
            string fallback = code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                ? char.ConvertFromUtf32((int)code)
                : string.Empty;
            return (fallback, false);
        }

        char first = (char)buffer[0];
        if (char.IsLowSurrogate(first))
        {
            // Second half of a surrogate pair — the previous index already produced it.
            return (string.Empty, true);
        }

        if (char.IsHighSurrogate(first))
        {
            if (index + 1 < charCount)
            {
                ushort[] pair = new ushort[4];
                int pairWritten = fpdf_text.FPDFTextGetText(textPage, index, 2, ref pair[0]);
                if (pairWritten > 2 && char.IsLowSurrogate((char)pair[1]))
                {
                    return (new string([(char)pair[0], (char)pair[1]]), false);
                }
            }

            return (string.Empty, false);
        }

        int length = Math.Min(written - 1, buffer.Length); // exclude terminator
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)buffer[i];
        }

        return (new string(chars), false);
    }

    private static (string? Name, int Flags) ReadGlyphFont(FpdfTextpageT textPage, int index)
    {
        int flags = 0;
        ulong length = fpdf_text.FPDFTextGetFontInfo(textPage, index, IntPtr.Zero, 0, ref flags);
        if (length == 0 || length > 4096)
        {
            return (null, flags);
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            _ = fpdf_text.FPDFTextGetFontInfo(textPage, index, buffer, length, ref flags);
            string? name = Marshal.PtrToStringUTF8(buffer, checked((int)length));
            return (name?.TrimEnd('\0'), flags);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads every page's info inside one native unit; the document is opened and closed here.</summary>
    internal static IReadOnlyList<PdfiumPageInfoEntry> ReadPageInfos(string path, int pageCount)
    {
        FpdfDocumentT document = LoadDocument(path);
        try
        {
            List<PdfiumPageInfoEntry> results = new(pageCount);
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                results.Add(ReadPageInfo(document, pageIndex));
            }

            return results;
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    /// <summary>
    /// Extracts a page range into memory as one native unit (import + save-as-copy). Writing
    /// the bytes to disk happens outside the scheduled unit, in the caller.
    /// </summary>
    internal static byte[] ExtractPagesToBytes(string sourcePath, int startPageIndex, int pageCount)
    {
        FpdfDocumentT source = LoadDocument(sourcePath);
        try
        {
            // Creating the destination is inside the source's try as well: if creation or
            // its handle check fails, the finally below still closes the source document
            // instead of leaking it.
            FpdfDocumentT destination = fpdf_edit.FPDF_CreateNewDocument();
            try
            {
                EnsureHandle(destination, "destination document", sourcePath);
                int sourcePageCount = fpdfview.FPDF_GetPageCount(source);
                if (startPageIndex < 0 || pageCount <= 0 || startPageIndex + pageCount > sourcePageCount)
                {
                    throw new InvalidOperationException("The requested PDF page range is invalid.");
                }

                string range = $"{startPageIndex + 1}-{startPageIndex + pageCount}";
                if (fpdf_ppo.FPDF_ImportPages(destination, source, range, 0) == 0)
                {
                    throw LastError("PDF pages could not be imported", sourcePath);
                }

                using MemoryStream stream = new();
                FPDF_FILEWRITE_ writer = new() { Version = 1 };
                writer.WriteBlock = WriteRegisteredBlock;
                SaveStreams[writer.__Instance] = stream;
                try
                {
                    if (fpdf_save.FPDF_SaveAsCopy(destination, writer, 0) == 0)
                    {
                        throw LastError("PDF pages could not be saved", sourcePath);
                    }
                }
                finally
                {
                    _ = SaveStreams.TryRemove(writer.__Instance, out _);
                }

                return stream.ToArray();
            }
            finally
            {
                if (destination.__Instance != IntPtr.Zero)
                {
                    fpdfview.FPDF_CloseDocument(destination);
                }
            }
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(source);
        }
    }

    private static PdfiumPageInfoEntry ReadPageInfo(FpdfDocumentT document, int pageIndex)
    {
        FpdfPageT page = fpdfview.FPDF_LoadPage(document, pageIndex);
        if (page.__Instance == IntPtr.Zero)
        {
            ulong error = fpdfview.FPDF_GetLastError();
            return new PdfiumPageInfoEntry(false, null,
                $"PDFium could not load page {pageIndex} (PDFium error {error}).");
        }

        try
        {
            double width = fpdfview.FPDF_GetPageWidthF(page);
            double height = fpdfview.FPDF_GetPageHeightF(page);
            int rotation = fpdf_edit.FPDFPageGetRotation(page) switch
            {
                1 => 90,
                2 => 180,
                3 => 270,
                _ => 0
            };
            return new PdfiumPageInfoEntry(true, new PdfiumPageInfoData(width, height, rotation), null);
        }
        catch (Exception exception) // Classified into the page failure result; the page records it (ADR 0035).
        {
            return new PdfiumPageInfoEntry(false, null, exception.Message);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static void EnsureHandle(object handle, string description, string? path)
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
            throw LastError($"PDFium did not create the {description}", path);
        }
    }

    private static PdfiumDocumentException LastError(string operation, string? path)
    {
        // The last-error value is thread-global native state: it is only meaningful when read
        // immediately after the failing call, inside the same scheduled unit.
        ulong error = fpdfview.FPDF_GetLastError();
        return new PdfiumDocumentException($"{operation} (PDFium error {error}).", error, path);
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
