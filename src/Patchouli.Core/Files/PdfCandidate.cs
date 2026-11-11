namespace Patchouli.Core.Files;

public sealed record PdfCandidate(
    string Path,
    string FileName,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    int? PageCount,
    string Status,
    string Readiness = FileLocalityReadiness.LocalReady,
    bool IsCloudPath = false);
