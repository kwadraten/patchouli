using Patchouli.Core.Ids;

namespace Patchouli.Core.Layout;

public sealed record Page(
    PageId PageId,
    DocumentInstanceId DocumentInstanceId,
    int PageIndex,
    string? PageLabel,
    double? Width,
    double? Height,
    int Rotation,
    string CoordinateBasis,
    double? BasisWidth,
    double? BasisHeight,
    string RendererBasisVersion,
    string? SourceFileHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
