using System.Buffers.Binary;

namespace Patchouli.Ocr;

public sealed record PngImageSize(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;
}

public sealed record OcrImageSizeGuardLimit(int MaxWidthPx, int MaxHeightPx, long MaxPixelCount)
{
    public static OcrImageSizeGuardLimit FormalMaterialDefault { get; } = new(8000, 11000, 60000000);
}

public static class PngImageSizeGuard
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<PngImageSize?> TryReadPngSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[24];
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read < header.Length || !header.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        return width <= 0 || height <= 0 ? null : new PngImageSize(width, height);
    }

    public static bool ExceedsLimit(PngImageSize size, OcrImageSizeGuardLimit? limit = null)
    {
        limit ??= OcrImageSizeGuardLimit.FormalMaterialDefault;
        return size.Width > limit.MaxWidthPx || size.Height > limit.MaxHeightPx || size.PixelCount > limit.MaxPixelCount;
    }

    public static string BuildErrorMessage(PngImageSize size, OcrImageSizeGuardLimit? limit = null)
    {
        limit ??= OcrImageSizeGuardLimit.FormalMaterialDefault;
        return $"Rendered image is too large for local OCR: width={size.Width}, height={size.Height}, pixels={size.PixelCount}, limits={limit.MaxWidthPx}x{limit.MaxHeightPx}/{limit.MaxPixelCount} pixels.";
    }
}
