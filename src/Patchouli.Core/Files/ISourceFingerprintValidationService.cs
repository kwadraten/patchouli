using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

/// <summary>
/// The fingerprint basis identifies the algorithm and version that produced a full source
/// file hash. A basis change (for example a hash algorithm upgrade) must invalidate every
/// previously cached fingerprint result without re-reading file metadata.
/// </summary>
public static class SourceFingerprintBasis
{
    public const string Blake3V1 = "blake3:v1";
}

/// <summary>
/// Result of a lazily computed, shared full-hash validation of a user-owned source file.
/// The runtime path is never exposed through MCP; it exists only to key the host cache.
/// </summary>
public sealed record SourceFingerprintValidation(
    string Path,
    long SizeBytes,
    DateTimeOffset MtimeUtc,
    string QuickHash,
    string FullBlake3,
    bool FromCache);

/// <summary>
/// Merges full-hash source file validation across callers. A validated fingerprint is
/// cached keyed by validated file identity (normalized path), length, modification time,
/// quick hash, and fingerprint basis, so an unchanged file is never re-hashed and
/// concurrent coordinate-sensitive reads share one in-flight validation.
/// </summary>
public interface ISourceFingerprintValidationService
{
    Task<Result<SourceFingerprintValidation>> ValidateAsync(
        string path,
        long sizeBytes,
        DateTimeOffset mtimeUtc,
        string fingerprintBasis,
        CancellationToken cancellationToken = default);
}
