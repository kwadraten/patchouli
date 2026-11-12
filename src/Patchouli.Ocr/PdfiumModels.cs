namespace Patchouli.Ocr;

/// <summary>PDFium native error codes, as returned by the library's last-error channel.</summary>
public static class PdfiumErrorCodes
{
    public const ulong Unknown = 1;
    public const ulong File = 2;
    public const ulong Format = 3;
    public const ulong Password = 4;
    public const ulong Security = 5;
    public const ulong Page = 6;
}

public sealed class PdfiumDocumentException : Exception
{
    public PdfiumDocumentException(string message, ulong? code = null, string? path = null) : base(message)
    {
        Code = code;
        Path = path;
    }

    public ulong? Code { get; }
    public string? Path { get; }
}

public sealed record PdfiumPageBitmap(byte[] BgraBytes, int Width, int Height, int Stride);

/// <summary>A user-space page box: un-rotated PDF coordinates with a bottom-left origin.</summary>
public readonly record struct PdfPageBox(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public bool IsDegenerate => Width <= 0 || Height <= 0;

    public PdfPageBox Intersect(PdfPageBox other)
    {
        return new PdfPageBox(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom),
            Math.Min(Right, other.Right), Math.Min(Top, other.Top));
    }
}

[Flags]
public enum PdfiumTextGlyphFlags
{
    None = 0,
    Generated = 1,
    UnicodeMapError = 2,
    Hyphen = 4,
    NoUnicodeMapping = 8,
    SurrogateContinuation = 16
}

public sealed record PdfiumTextGlyph(
    int Index,
    string Text,
    double Left,
    double Right,
    double Bottom,
    double Top,
    double OriginX,
    double OriginY,
    double AngleRadians,
    PdfiumTextGlyphFlags Flags,
    string? FontName,
    int FontFlags,
    double FontSize);

public sealed record PdfiumTextPage(
    PdfPageBox MediaBox,
    PdfPageBox EffectiveBox,
    int RotationDegrees,
    int CharCount,
    IReadOnlyList<PdfiumTextGlyph> Glyphs,
    double PageWidthPoints = 0,
    double PageHeightPoints = 0);
