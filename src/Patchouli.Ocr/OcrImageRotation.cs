using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using SkiaSharp;

namespace Patchouli.Ocr;

/// <summary>
/// Clockwise quarter-turn support for viewport-rotated OCR. All public OCR entry points take
/// <c>rotationDegrees</c> as the onscreen clockwise rotation (0/90/180/270). BBoxes and regions
/// stay in the original unrotated document basis at API boundaries; the pipeline renders the
/// rotated pixels for the provider and maps returned geometry back into document coordinates.
/// </summary>
public static class OcrImageRotation
{
    /// <summary>
    /// Normalizes any degree value to one of 0/90/180/270 (clockwise), or fails when the value
    /// is not a quarter turn.
    /// </summary>
    public static Result<int> Normalize(int rotationDegrees)
    {
        int normalized = (rotationDegrees % 360 + 360) % 360;
        return normalized % 90 == 0
            ? Result<int>.Success(normalized)
            : Result<int>.Failure(AppErrorCodes.ValidationFailed,
                "OCR page rotation must be a clockwise quarter turn (0, 90, 180, or 270 degrees).");
    }

    public static bool IsQuarterTurn(int rotationDegrees)
    {
        return Normalize(rotationDegrees) is { IsSuccess: true };
    }

    /// <summary>
    /// Maps a normalized bbox from the unrotated document basis into the rotated image basis —
    /// the frame of the pixels a provider sees when the page is rotated clockwise onscreen.
    /// </summary>
    public static NormalizedBBox RotateIntoImageBasis(NormalizedBBox box, int rotationDegrees)
    {
        return Rotate(box, NormalizeOrThrow(rotationDegrees));
    }

    /// <summary>
    /// Maps a normalized bbox from the rotated image basis back into the original unrotated
    /// document basis — the inverse of <see cref="RotateIntoImageBasis"/>.
    /// </summary>
    public static NormalizedBBox UnrotateFromImageBasis(NormalizedBBox box, int rotationDegrees)
    {
        int normalized = NormalizeOrThrow(rotationDegrees);
        return Rotate(box, (360 - normalized) % 360);
    }

    /// <summary>Clockwise quarter-turn of a normalized rect inside its own unit square.</summary>
    private static NormalizedBBox Rotate(NormalizedBBox box, int clockwiseDegrees)
    {
        return clockwiseDegrees switch
        {
            0 => box,
            90 => new NormalizedBBox(1 - (box.Y + box.Height), box.X, box.Height, box.Width),
            180 => new NormalizedBBox(1 - (box.X + box.Width), 1 - (box.Y + box.Height), box.Width,
                box.Height),
            270 => new NormalizedBBox(box.Y, 1 - (box.X + box.Width), box.Height, box.Width),
            _ => box
        };
    }

    /// <summary>
    /// Rotates a decoded bitmap clockwise by a quarter turn and returns PNG bytes.
    /// Degree 0 returns the source encoded unchanged; 90/270 swap width and height.
    /// </summary>
    public static byte[] RotateToPngBytes(SKBitmap source, int rotationDegrees)
    {
        ArgumentNullException.ThrowIfNull(source);
        int degrees = NormalizeOrThrow(rotationDegrees);
        using SKBitmap rotated = RotateToBitmap(source, degrees);
        using SKImage image = SKImage.FromBitmap(rotated);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>
    /// Rotates a PNG file clockwise by a quarter turn into <paramref name="outputPath"/>.
    /// Degree 0 copies the encoded content without a pixel round-trip.
    /// </summary>
    public static void RotatePngFile(string sourcePngPath, string outputPath, int rotationDegrees)
    {
        int degrees = NormalizeOrThrow(rotationDegrees);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (degrees == 0)
        {
            File.Copy(sourcePngPath, outputPath, true);
            return;
        }

        using SKBitmap? source = SKBitmap.Decode(sourcePngPath);
        if (source is null)
        {
            throw new InvalidOperationException($"Could not decode PNG '{sourcePngPath}'.");
        }

        File.WriteAllBytes(outputPath, RotateToPngBytes(source, degrees));
    }

    private static SKBitmap RotateToBitmap(SKBitmap source, int clockwiseDegrees)
    {
        bool swap = clockwiseDegrees is 90 or 270;
        int targetWidth = swap ? source.Height : source.Width;
        int targetHeight = swap ? source.Width : source.Height;
        SKBitmap rotated = new(targetWidth, targetHeight, source.ColorType, source.AlphaType);
        using (SKCanvas canvas = new(rotated))
        {
            switch (clockwiseDegrees)
            {
                case 90:
                    canvas.Translate(targetWidth, 0);
                    canvas.RotateDegrees(90);
                    break;
                case 180:
                    canvas.Translate(targetWidth, targetHeight);
                    canvas.RotateDegrees(180);
                    break;
                case 270:
                    canvas.Translate(0, targetHeight);
                    canvas.RotateDegrees(270);
                    break;
            }

            canvas.DrawBitmap(source, 0, 0);
            canvas.Flush();
        }

        return rotated;
    }

    private static int NormalizeOrThrow(int rotationDegrees)
    {
        Result<int> normalized = Normalize(rotationDegrees);
        return normalized.IsSuccess
            ? normalized.Value
            : throw new ArgumentOutOfRangeException(nameof(rotationDegrees), rotationDegrees,
                normalized.ErrorMessage);
    }
}
