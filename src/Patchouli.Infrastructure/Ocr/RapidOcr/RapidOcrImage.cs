using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>
/// Row-major 8-bit BGR image. Upstream RapidOCR works in OpenCV BGR order, so the
/// native port keeps the same channel layout and interpolation math instead of
/// reusing Skia's BGRA bitmaps directly.
/// </summary>
internal sealed class RapidOcrImage
{
    public const int ChannelCount = 3;

    public RapidOcrImage(int width, int height)
        : this(width, height, new byte[checked(width * height * ChannelCount)])
    {
    }

    private RapidOcrImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>
    /// Decodes an image file the way upstream <c>LoadImage</c> does: EXIF orientation is
    /// applied and images with an alpha channel are composited over a black or white
    /// background chosen from the mean luminance of the opaque pixels.
    /// </summary>
    public static RapidOcrImage? TryDecode(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using SKCodec? codec = SKCodec.Create(stream);
            if (codec is null)
            {
                return null;
            }

            using SKBitmap? decoded = SKBitmap.Decode(codec);
            if (decoded is null)
            {
                return null;
            }

            return FromBitmap(decoded, codec.EncodedOrigin);
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static RapidOcrImage FromBitmap(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        RapidOcrImage bgr = FromBitmapPixels(bitmap);
        return ApplyOrientation(bgr, origin);
    }

    private static RapidOcrImage FromBitmapPixels(SKBitmap bitmap)
    {
        using SKBitmap straight = CopyToStraightBgra(bitmap);
        bool premultiplied = straight.AlphaType == SKAlphaType.Premul;
        int rowBytes = straight.RowBytes;
        ReadOnlySpan<byte> source = straight.GetPixelSpan();

        int pixelCount = checked(bitmap.Width * bitmap.Height);
        byte[] bgr = new byte[checked(pixelCount * ChannelCount)];
        bool hasTransparency = false;
        double luminanceSum = 0;
        long opaqueCount = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = rowStart + x * 4;
                byte alpha = source[offset + 3];
                byte b = source[offset];
                byte g = source[offset + 1];
                byte r = source[offset + 2];
                if (premultiplied && alpha is > 0 and < 255)
                {
                    b = Unpremultiply(b, alpha);
                    g = Unpremultiply(g, alpha);
                    r = Unpremultiply(r, alpha);
                }
                else if (alpha == 0 && premultiplied)
                {
                    b = 0;
                    g = 0;
                    r = 0;
                }

                if (alpha != 255)
                {
                    hasTransparency = true;
                }

                if (alpha > 0)
                {
                    luminanceSum += 0.299 * r + 0.587 * g + 0.114 * b;
                    opaqueCount++;
                }

                int target = (y * bitmap.Width + x) * ChannelCount;
                bgr[target] = b;
                bgr[target + 1] = g;
                bgr[target + 2] = r;
            }
        }

        RapidOcrImage image = new(bitmap.Width, bitmap.Height, bgr);
        if (!hasTransparency)
        {
            return image;
        }

        // Upstream composites the straight-alpha RGB over a black or white background
        // picked from the mean luminance of the non-transparent pixels.
        byte background = opaqueCount == 0 || luminanceSum / opaqueCount >= 128 ? (byte)0 : (byte)255;
        if (opaqueCount == 0)
        {
            background = 255;
        }

        for (int y = 0; y < bitmap.Height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = rowStart + x * 4;
                byte alpha = source[offset + 3];
                if (alpha == 255)
                {
                    continue;
                }

                int target = (y * bitmap.Width + x) * ChannelCount;
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    double foreground = bgr[target + channel] * (alpha / 255.0);
                    double blended = foreground + background * (1.0 - alpha / 255.0);
                    bgr[target + channel] = (byte)Math.Clamp((int)blended, 0, 255);
                }
            }
        }

        return image;
    }

    private static SKBitmap CopyToStraightBgra(SKBitmap bitmap)
    {
        SKImageInfo info = new(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        SKBitmap destination = new(info);
        if (bitmap.CopyTo(destination, SKColorType.Bgra8888))
        {
            return destination;
        }

        destination.Dispose();
        return bitmap.Copy(SKColorType.Bgra8888) ??
               throw new InvalidOperationException("Failed to normalize the decoded image to BGRA8888.");
    }

    private static byte Unpremultiply(byte value, byte alpha)
    {
        int result = (value * 255 + alpha / 2) / alpha;
        return (byte)Math.Clamp(result, 0, 255);
    }

    internal static RapidOcrImage ApplyOrientation(RapidOcrImage source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source;
        }

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swap ? source.Height : source.Width;
        int height = swap ? source.Width : source.Height;
        RapidOcrImage destination = new(width, height);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int sourceX, int sourceY) = MapOrientation(origin, x, y, source.Width, source.Height);
                int sourceOffset = (sourceY * source.Width + sourceX) * ChannelCount;
                int targetOffset = (y * width + x) * ChannelCount;
                destination.Pixels[targetOffset] = source.Pixels[sourceOffset];
                destination.Pixels[targetOffset + 1] = source.Pixels[sourceOffset + 1];
                destination.Pixels[targetOffset + 2] = source.Pixels[sourceOffset + 2];
            }
        }

        return destination;
    }

    private static (int X, int Y) MapOrientation(SKEncodedOrigin origin, int x, int y, int sourceWidth,
        int sourceHeight)
    {
        return origin switch
        {
            SKEncodedOrigin.TopRight => (sourceWidth - 1 - x, y),
            SKEncodedOrigin.BottomRight => (sourceWidth - 1 - x, sourceHeight - 1 - y),
            SKEncodedOrigin.BottomLeft => (x, sourceHeight - 1 - y),
            SKEncodedOrigin.LeftTop => (y, x),
            SKEncodedOrigin.RightTop => (y, sourceHeight - 1 - x),
            SKEncodedOrigin.RightBottom => (sourceWidth - 1 - y, sourceHeight - 1 - x),
            SKEncodedOrigin.LeftBottom => (sourceWidth - 1 - y, x),
            _ => (x, y)
        };
    }

    /// <summary>OpenCV <c>INTER_LINEAR</c> resize using the same pixel-center mapping.</summary>
    public RapidOcrImage ResizeLinear(int width, int height)
    {
        if (width == Width && height == Height)
        {
            return Clone();
        }

        RapidOcrImage destination = new(width, height);
        float scaleX = (float)Width / width;
        float scaleY = (float)Height / height;
        for (int y = 0; y < height; y++)
        {
            float sourceY = (y + 0.5f) * scaleY - 0.5f;
            int y0 = (int)MathF.Floor(sourceY);
            float fy = sourceY - y0;
            int y1 = y0 + 1;
            y0 = Math.Clamp(y0, 0, Height - 1);
            y1 = Math.Clamp(y1, 0, Height - 1);
            for (int x = 0; x < width; x++)
            {
                float sourceX = (x + 0.5f) * scaleX - 0.5f;
                int x0 = (int)MathF.Floor(sourceX);
                float fx = sourceX - x0;
                int x1 = x0 + 1;
                x0 = Math.Clamp(x0, 0, Width - 1);
                x1 = Math.Clamp(x1, 0, Width - 1);

                int topLeft = (y0 * Width + x0) * ChannelCount;
                int topRight = (y0 * Width + x1) * ChannelCount;
                int bottomLeft = (y1 * Width + x0) * ChannelCount;
                int bottomRight = (y1 * Width + x1) * ChannelCount;
                int target = (y * width + x) * ChannelCount;
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    float top = Pixels[topLeft + channel] * (1 - fx) + Pixels[topRight + channel] * fx;
                    float bottom = Pixels[bottomLeft + channel] * (1 - fx) +
                                   Pixels[bottomRight + channel] * fx;
                    float value = top * (1 - fy) + bottom * fy;
                    destination.Pixels[target + channel] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
                }
            }
        }

        return destination;
    }

    public RapidOcrImage Clone()
    {
        return new RapidOcrImage(Width, Height, (byte[])Pixels.Clone());
    }

    public RapidOcrImage Crop(int x0, int y0, int x1, int y1)
    {
        int left = Math.Clamp(x0, 0, Width);
        int top = Math.Clamp(y0, 0, Height);
        int right = Math.Clamp(x1, left, Width);
        int bottom = Math.Clamp(y1, top, Height);
        RapidOcrImage crop = new(right - left, bottom - top);
        for (int y = 0; y < crop.Height; y++)
        {
            int sourceOffset = ((top + y) * Width + left) * ChannelCount;
            int targetOffset = y * crop.Width * ChannelCount;
            Array.Copy(Pixels, sourceOffset, crop.Pixels, targetOffset, crop.Width * ChannelCount);
        }

        return crop;
    }

    /// <summary>Adds black rows on top and bottom, matching <c>copyMakeBorder</c>.</summary>
    public RapidOcrImage PadVertically(int top, int bottom)
    {
        RapidOcrImage padded = new(Width, Height + top + bottom);
        int rowBytes = Width * ChannelCount;
        for (int y = 0; y < Height; y++)
        {
            Array.Copy(Pixels, y * rowBytes, padded.Pixels, (y + top) * rowBytes, rowBytes);
        }

        return padded;
    }

    /// <summary>Rotates 180 degrees, matching <c>cv2.rotate(..., ROTATE_180)</c>.</summary>
    public RapidOcrImage Rotate180()
    {
        RapidOcrImage rotated = new(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int sourceOffset = (y * Width + x) * ChannelCount;
                int targetOffset = ((Height - 1 - y) * Width + (Width - 1 - x)) * ChannelCount;
                rotated.Pixels[targetOffset] = Pixels[sourceOffset];
                rotated.Pixels[targetOffset + 1] = Pixels[sourceOffset + 1];
                rotated.Pixels[targetOffset + 2] = Pixels[sourceOffset + 2];
            }
        }

        return rotated;
    }

    /// <summary>
    /// Rotates 90 degrees counter-clockwise, matching <c>np.rot90</c>. Upstream applies
    /// this to perspective crops whose height/width ratio is at least 1.5.
    /// </summary>
    public RapidOcrImage Rotate90CounterClockwise()
    {
        RapidOcrImage rotated = new(Height, Width);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int sourceOffset = (y * Width + x) * ChannelCount;
                int targetOffset = ((Width - 1 - x) * Height + y) * ChannelCount;
                rotated.Pixels[targetOffset] = Pixels[sourceOffset];
                rotated.Pixels[targetOffset + 1] = Pixels[sourceOffset + 1];
                rotated.Pixels[targetOffset + 2] = Pixels[sourceOffset + 2];
            }
        }

        return rotated;
    }
}
