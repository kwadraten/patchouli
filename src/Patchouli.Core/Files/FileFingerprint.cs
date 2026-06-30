namespace Patchouli.Core.Files;

public sealed record FileFingerprint(
    string Path,
    string FileName,
    long SizeBytes,
    DateTimeOffset MtimeUtc,
    string QuickHash,
    string? FullBlake3);
