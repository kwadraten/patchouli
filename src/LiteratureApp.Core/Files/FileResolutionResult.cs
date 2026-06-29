using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Files;

public sealed record FileResolutionResult(
    FileAssetId FileAssetId,
    string Status,
    string? ResolvedPath,
    IReadOnlyList<FileResolutionCandidate> Candidates,
    string Confidence,
    string RequiredAction,
    string? Warning);
