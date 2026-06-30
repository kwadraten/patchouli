namespace Patchouli.Core.Files;

public record FileResolutionCandidate(
    string Path,
    long SizeBytes,
    DateTimeOffset? MtimeUtc,
    string? QuickHash,
    string? FullBlake3,
    string Confidence,
    string Reason);
