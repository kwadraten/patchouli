using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record FileResolutionResult(
    FileAssetId FileAssetId,
    string Status,
    string? ResolvedPath,
    IReadOnlyList<FileResolutionCandidate> Candidates,
    string Confidence,
    string RequiredAction,
    string? Warning)
{
    public IReadOnlyList<ConflictDescriptor> Conflicts { get; init; } = Array.Empty<ConflictDescriptor>();
}
