namespace Patchouli.Ocr.Engines.OvisOcr3;

internal sealed record OvisOcr3TextPositions(long[] PositionIds, float[] Cos, float[] Sin, int Next);

internal static class OvisOcr3Positions
{
    private static readonly float[] Frequencies = Enumerable.Range(0, 32)
        .Select(index => 1f / MathF.Pow(10_000_000f, index * 2f / 64f)).ToArray();

    public static OvisOcr3TextPositions Prepare(long[] ids, OvisOcr3Image image)
    {
        int imageStart = Array.IndexOf(ids, 248056L);
        if (imageStart < 0 || imageStart + image.ImageTokenCount > ids.Length ||
            ids.AsSpan(imageStart, image.ImageTokenCount).ContainsAnyExcept(248056L))
        {
            throw new InvalidDataException("The OvisOCR3 image tokens do not match the image grid.");
        }

        int count = ids.Length;
        long[] positionIds = new long[count * 3];
        int current = 0;
        for (int index = 0; index < imageStart; index++)
        {
            Assign(positionIds, count, index, current, current, current);
            current++;
        }

        for (int index = 0; index < image.ImageTokenCount; index++)
        {
            int blockY = index / (image.GridWidth / 2);
            int blockX = index % (image.GridWidth / 2);
            Assign(positionIds, count, imageStart + index, current, current + blockY, current + blockX);
        }

        current += Math.Max(image.GridHeight, image.GridWidth) / 2;
        for (int index = imageStart + image.ImageTokenCount; index < count; index++)
        {
            Assign(positionIds, count, index, current, current, current);
            current++;
        }

        float[] cos = new float[count * 64];
        float[] sin = new float[count * 64];
        for (int index = 0; index < count; index++)
        {
            for (int frequency = 0; frequency < 32; frequency++)
            {
                int dimension = frequency < 33 && frequency % 3 == 1 ? 1 :
                    frequency < 30 && frequency % 3 == 2 ? 2 : 0;
                float angle = positionIds[dimension * count + index] * Frequencies[frequency];
                float cosine = MathF.Cos(angle);
                float sine = MathF.Sin(angle);
                cos[index * 64 + frequency] = cosine;
                cos[index * 64 + frequency + 32] = cosine;
                sin[index * 64 + frequency] = sine;
                sin[index * 64 + frequency + 32] = sine;
            }
        }

        return new OvisOcr3TextPositions(positionIds, cos, sin,
            (int)positionIds.Max() + 1);
    }

    public static (float[] Cos, float[] Sin) Decode(int start, int count)
    {
        float[] cos = new float[count * 64];
        float[] sin = new float[count * 64];
        for (int token = 0; token < count; token++)
        {
            for (int frequency = 0; frequency < 32; frequency++)
            {
                float angle = (start + token) * Frequencies[frequency];
                float cosine = MathF.Cos(angle);
                float sine = MathF.Sin(angle);
                cos[token * 64 + frequency] = cosine;
                cos[token * 64 + frequency + 32] = cosine;
                sin[token * 64 + frequency] = sine;
                sin[token * 64 + frequency + 32] = sine;
            }
        }

        return (cos, sin);
    }

    private static void Assign(long[] positions, int count, int index, int temporal, int height, int width)
    {
        positions[index] = temporal;
        positions[count + index] = height;
        positions[count * 2 + index] = width;
    }
}
