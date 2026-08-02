using System.Text;
using Patchouli.Core.Files;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Infrastructure.Files;

/// <summary>
/// Host-wide, bounded, lazy full-hash validation of user-owned source files. Before committing
/// to a whole-file hash it verifies the low-cost binding (normalized path, length, mtime, quick
/// hash) and fingerprint basis; unchanged files reuse the cached full hash, and concurrent
/// callers share one in-flight validation. Failures and cancellations are never cached.
/// </summary>
public sealed class SourceFingerprintValidationService : ISourceFingerprintValidationService
{
    internal const long DefaultByteLimit = 8 * 1024 * 1024;

    private readonly IFileFingerprintService _fingerprintService;
    private readonly Func<string, CancellationToken, Task<string>> _fullHashComputer;
    private readonly SharedReadCache<FingerprintKey, SourceFingerprintValidation> _cache;

    public SourceFingerprintValidationService(
        IFileFingerprintService? fingerprintService = null,
        Func<string, CancellationToken, Task<string>>? fullHashComputer = null,
        long byteLimit = DefaultByteLimit)
    {
        _fingerprintService = fingerprintService ?? new FileFingerprintService();
        _fullHashComputer = fullHashComputer ?? (static (path, cancellationToken) =>
            Blake3Hash.ComputeFileAsync(path, cancellationToken));
        _cache = new SharedReadCache<FingerprintKey, SourceFingerprintValidation>(byteLimit, EstimateSize);
    }

    /// <summary>Observable cache counters; safe for performance logging because they carry no content.</summary>
    internal ReadCacheMetrics CacheMetrics => _cache.Metrics;

    public async Task<Result<SourceFingerprintValidation>> ValidateAsync(
        string path,
        long sizeBytes,
        DateTimeOffset mtimeUtc,
        string fingerprintBasis,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<SourceFingerprintValidation>.Failure(AppErrorCodes.ValidationFailed,
                "File path is required.");
        }

        if (string.IsNullOrWhiteSpace(fingerprintBasis))
        {
            return Result<SourceFingerprintValidation>.Failure(AppErrorCodes.ValidationFailed,
                "Fingerprint basis is required.");
        }

        try
        {
            string normalizedPath = Path.GetFullPath(path);
            Result<string> quickHash = await _fingerprintService.ComputeQuickHashAsync(normalizedPath,
                cancellationToken);
            if (quickHash.IsFailure)
            {
                return Result<SourceFingerprintValidation>.Failure(quickHash.ErrorCode!, quickHash.ErrorMessage!);
            }

            FingerprintKey key = new(normalizedPath, sizeBytes, mtimeUtc.UtcTicks, quickHash.Value, fingerprintBasis);
            if (_cache.TryGet(key, out SourceFingerprintValidation? cached))
            {
                return Result<SourceFingerprintValidation>.Success(cached with { FromCache = true });
            }

            return await _cache.GetOrAddAsync(key,
                sharedToken => ComputeFullAsync(normalizedPath, sizeBytes, mtimeUtc, quickHash.Value, fingerprintBasis,
                    sharedToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.source-fingerprint-validation"))
        {
            return Result<SourceFingerprintValidation>.Failure(AppErrorCodes.DatabaseError,
                $"Fingerprint validation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the cached fingerprint for the given binding. Used after a resolved binding change or
    /// fingerprint basis upgrade so the next coordinate-sensitive access re-validates the source.
    /// </summary>
    public void Invalidate(string path, long sizeBytes, DateTimeOffset mtimeUtc, string quickHash,
        string fingerprintBasis)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _cache.Invalidate(new FingerprintKey(Path.GetFullPath(path), sizeBytes, mtimeUtc.UtcTicks, quickHash,
            fingerprintBasis));
    }

    private async Task<Result<SourceFingerprintValidation>> ComputeFullAsync(
        string normalizedPath,
        long sizeBytes,
        DateTimeOffset mtimeUtc,
        string quickHash,
        string fingerprintBasis,
        CancellationToken cancellationToken)
    {
        try
        {
            string fullBlake3 = await _fullHashComputer(normalizedPath, cancellationToken);
            return Result<SourceFingerprintValidation>.Success(new SourceFingerprintValidation(
                normalizedPath, sizeBytes, mtimeUtc, quickHash, fullBlake3, false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.source-fingerprint-validation"))
        {
            return Result<SourceFingerprintValidation>.Failure(AppErrorCodes.DatabaseError,
                $"Full hash failed: {ex.Message}");
        }
    }

    private static long EstimateSize(SourceFingerprintValidation value)
    {
        return 256L + Encoding.UTF8.GetByteCount(value.FullBlake3) + Encoding.UTF8.GetByteCount(value.QuickHash) +
               Encoding.UTF8.GetByteCount(value.Path);
    }

    private readonly record struct FingerprintKey(
        string Path,
        long SizeBytes,
        long MtimeTicks,
        string QuickHash,
        string Basis);
}
