using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.Vision;

/// <summary>Shared inference implementation; the FSX package owns configuration and job driving.</summary>
public sealed class LayoutRecognitionSession : IDisposable
{
    private readonly VisionLayoutDetector _detector;

    public LayoutRecognitionSession(string modelId, string modelPath)
    {
        _detector = new VisionLayoutDetector(modelId, modelPath);
    }

    public async Task<IReadOnlyList<LayoutRecognitionRegion>> DetectAsync(ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<VisionLayoutRegion>> result =
            await _detector.DetectAsync(image.ToArray(), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.ErrorMessage);
        }

        return Array.AsReadOnly(result.Value.Select(region => new LayoutRecognitionRegion(
            region.Label, region.BBox, region.Confidence, region.Order)).ToArray());
    }

    public void Dispose()
    {
        _detector.Dispose();
    }
}
