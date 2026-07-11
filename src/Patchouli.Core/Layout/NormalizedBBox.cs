using Patchouli.Core.Results;

namespace Patchouli.Core.Layout;

public readonly record struct NormalizedBBox(double X, double Y, double Width, double Height)
{
    public Result Validate()
    {
        if (X < 0 || X > 1 || Y < 0 || Y > 1 || Width <= 0 || Width > 1 || Height <= 0 || Height > 1)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Normalized bbox values must be within 0..1, with positive width and height.");
        }

        if (X + Width > 1 || Y + Height > 1)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Normalized bbox must fit within the page.");
        }

        return Result.Success();
    }

    public bool Overlaps(NormalizedBBox other)
    {
        bool xOverlap = X < other.X + other.Width && X + Width > other.X;
        bool yOverlap = Y < other.Y + other.Height && Y + Height > other.Y;
        return xOverlap && yOverlap;
    }
}
