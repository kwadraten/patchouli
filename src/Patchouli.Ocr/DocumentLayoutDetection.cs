using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

/// <summary>A local detector's normalized region, returned in reading order.</summary>
public sealed record DocumentLayoutRegion(string Label, NormalizedBBox BBox, double Confidence, int? Order = null);

public interface IDocumentLayoutDetector
{
    Result ValidateModel(string modelId, string modelPath);

    Task<Result<IReadOnlyList<DocumentLayoutRegion>>> DetectAsync(byte[] imageBytes, string modelId,
        string modelPath, CancellationToken cancellationToken = default);
}
