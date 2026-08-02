using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Hashing;

namespace Patchouli.Infrastructure.Rendering;

/// <summary>
/// Lazy, rebuildable source-basis validation for viewing sessions. Validation is cheap-first:
/// an unchanged resolved file is confirmed with exists/size/mtime/quick-hash only, and the
/// full file hash is computed at most once per resolved binding per basis and shared by all
/// concurrent callers. Failures and cancellations are not cached.
/// </summary>
public sealed class SourceValidationService : ISourceValidationService
{
    private readonly IFileFingerprintService _fingerprints;
    private readonly Func<string, CancellationToken, Task<string>> _fullHashFactory;
    private readonly object _sync = new();
    private readonly Dictionary<RuntimeKey, RuntimeEntry> _runtime = new();

    public SourceValidationService(IFileFingerprintService? fingerprints = null,
        Func<string, CancellationToken, Task<string>>? fullHashFactory = null)
    {
        _fingerprints = fingerprints ?? new FileFingerprintService();
        _fullHashFactory = fullHashFactory ?? Blake3Hash.ComputeFileAsync;
    }

    public Task<SourceValidationResult> GetLastKnownAsync(SourceValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_runtime.TryGetValue(Key(request), out RuntimeEntry? entry) && entry.Status is not null)
            {
                return Task.FromResult(ToResult(entry));
            }
        }

        return Task.FromResult(LastKnownFromStored(request));
    }

    public async Task<SourceValidationResult?> TryGetCurrentAsync(SourceValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        RuntimeKey key = Key(request);
        RuntimeEntry? entry;
        lock (_sync)
        {
            _runtime.TryGetValue(key, out entry);
        }

        return entry is null ? null : await TryReuseAsync(entry, request);
    }

    public async Task<SourceValidationResult> EnsureValidatedAsync(SourceValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        RuntimeKey key = Key(request);

        RuntimeEntry? cached;
        lock (_sync)
        {
            _runtime.TryGetValue(key, out cached);
        }

        if (cached is not null && cached.InFlight is null)
        {
            SourceValidationResult? reused = await TryReuseAsync(cached, request);
            if (reused is not null)
            {
                return reused;
            }
        }

        Task<SourceValidationResult> inFlight;
        lock (_sync)
        {
            if (_runtime.TryGetValue(key, out RuntimeEntry? existing) && existing.InFlight is not null)
            {
                inFlight = existing.InFlight;
            }
            else
            {
                RuntimeEntry entry = cached ?? new RuntimeEntry();
                entry.ResolvedPath ??= request.ResolvedPath;
                _runtime[key] = entry;
                entry.ValidationCancellation?.Dispose();
                entry.ValidationCancellation = new CancellationTokenSource();
                entry.InFlight = ValidateCoreAsync(entry, request, entry.ValidationCancellation.Token);
                inFlight = entry.InFlight;
            }
        }

        return await inFlight.WaitAsync(cancellationToken);
    }

    public Task InvalidateAsync(FileAssetId fileAssetId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            RuntimeKey[] keys = _runtime.Keys.Where(key => key.FileAssetId == fileAssetId).ToArray();
            foreach (RuntimeKey key in keys)
            {
                if (_runtime.Remove(key, out RuntimeEntry? entry))
                {
                    entry.ValidationCancellation?.Cancel();
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task<SourceValidationResult?> TryReuseAsync(RuntimeEntry entry,
        SourceValidationRequest request)
    {
        if (entry.InFlight is not null || entry.Status is null)
        {
            return null;
        }

        // A FileAsset can be rebound without changing its id. Never carry a runtime decision
        // made for the previous persisted fingerprint into that new binding, even when the
        // filesystem path happens to be unchanged.
        if (entry.StoredFingerprint != StoredFingerprint.From(request))
        {
            return null;
        }

        if (entry.Status == SourceValidationStatus.Unavailable)
        {
            return File.Exists(entry.ResolvedPath ?? request.ResolvedPath)
                ? null
                : ToResult(entry);
        }

        if (entry.Status is not (SourceValidationStatus.Current or SourceValidationStatus.Changed))
        {
            return null;
        }

        try
        {
            string path = entry.ResolvedPath ?? request.ResolvedPath;
            FileInfo info = new(path);
            if (!info.Exists || entry.Stamp is not { } stamp || stamp != (info.Length, info.LastWriteTimeUtc.Ticks))
            {
                return null;
            }

            return ToResult(entry);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.source-validation"))
        {
            return null;
        }
    }

    private async Task<SourceValidationResult> ValidateCoreAsync(RuntimeEntry entry,
        SourceValidationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            string path = request.ResolvedPath;
            if (!File.Exists(path))
            {
                return Complete(entry, request, new SourceValidationResult(
                    SourceValidationStatus.Unavailable, path, null, "Source file is unavailable.", false));
            }

            FileInfo info = new(path);
            bool cheapConfirmsUnchanged =
                !request.RequireFullHash &&
                request.StoredSizeBytes == info.Length &&
                !string.IsNullOrWhiteSpace(request.StoredQuickHash) &&
                await QuickHashMatchesAsync(request, cancellationToken);

            if (cheapConfirmsUnchanged)
            {
                return Complete(entry, request, new SourceValidationResult(
                    SourceValidationStatus.Current, path, request.StoredFullHash, null, false));
            }

            string fullHash = await _fullHashFactory(path, cancellationToken);
            bool matchesStored = string.Equals(fullHash, request.StoredFullHash, StringComparison.Ordinal);
            if (matchesStored)
            {
                return Complete(entry, request, new SourceValidationResult(
                    SourceValidationStatus.Current, path, fullHash, null, true));
            }

            return Complete(entry, request, new SourceValidationResult(
                SourceValidationStatus.Changed, path, fullHash,
                "source_changed: existing bbox values may be bbox_basis_stale.", true));
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                entry.InFlight = null;
                entry.ValidationCancellation?.Dispose();
                entry.ValidationCancellation = null;
            }

            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.source-validation"))
        {
            return Complete(entry, request, new SourceValidationResult(
                SourceValidationStatus.Unavailable, request.ResolvedPath, null,
                $"Source validation failed: {exception.Message}", false));
        }
    }

    private async Task<bool> QuickHashMatchesAsync(SourceValidationRequest request,
        CancellationToken cancellationToken)
    {
        Result<string> quick = await _fingerprints.ComputeQuickHashAsync(request.ResolvedPath, cancellationToken);
        return quick.IsSuccess &&
               string.Equals(quick.Value, request.StoredQuickHash, StringComparison.Ordinal);
    }

    private SourceValidationResult Complete(RuntimeEntry entry, SourceValidationRequest request,
        SourceValidationResult result)
    {
        lock (_sync)
        {
            entry.Status = result.Status;
            entry.FullHash = result.FullHash;
            entry.Warning = result.Warning;
            entry.ResolvedPath = result.ResolvedPath;
            entry.InFlight = null;
            entry.ValidationCancellation?.Dispose();
            entry.ValidationCancellation = null;
            entry.Stamp = FileStamp(result.ResolvedPath);
            entry.StoredFingerprint = StoredFingerprint.From(request);
        }

        return result;
    }

    private static SourceValidationResult ToResult(RuntimeEntry entry)
    {
        return new SourceValidationResult(entry.Status ?? SourceValidationStatus.Unverified,
            entry.ResolvedPath ?? "", entry.FullHash, entry.Warning, false);
    }

    private static SourceValidationResult LastKnownFromStored(SourceValidationRequest request)
    {
        string status = request.StoredStatus switch
        {
            FileAssetStatus.Available => SourceValidationStatus.Current,
            FileAssetStatus.Changed => SourceValidationStatus.Changed,
            _ => SourceValidationStatus.Unavailable
        };
        return new SourceValidationResult(status, request.StoredPath, request.StoredFullHash, null, false);
    }

    private static (long Size, long MtimeTicks)? FileStamp(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static RuntimeKey Key(SourceValidationRequest request)
    {
        return new RuntimeKey(request.FileAssetId, request.FingerprintBasis);
    }

    private readonly record struct RuntimeKey(FileAssetId FileAssetId, string FingerprintBasis);

    private sealed class RuntimeEntry
    {
        public string? Status { get; set; }
        public string? FullHash { get; set; }
        public string? Warning { get; set; }
        public string? ResolvedPath { get; set; }
        public (long Size, long MtimeTicks)? Stamp { get; set; }
        public Task<SourceValidationResult>? InFlight { get; set; }
        public CancellationTokenSource? ValidationCancellation { get; set; }
        public StoredFingerprint? StoredFingerprint { get; set; }
    }

    private readonly record struct StoredFingerprint(
        long SizeBytes,
        DateTimeOffset? MtimeUtc,
        string? QuickHash,
        string? FullHash,
        string Status)
    {
        public static StoredFingerprint From(SourceValidationRequest request)
        {
            return new StoredFingerprint(request.StoredSizeBytes, request.StoredMtimeUtc,
                request.StoredQuickHash, request.StoredFullHash, request.StoredStatus);
        }
    }
}
