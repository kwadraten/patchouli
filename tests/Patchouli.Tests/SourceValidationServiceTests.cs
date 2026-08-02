using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Infrastructure.Rendering;

namespace Patchouli.Tests;

public sealed class SourceValidationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileFingerprintService _fingerprints = new();
    private int _fullHashCalls;
    private int _quickHashCalls;

    public SourceValidationServiceTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"srcval-{Guid.NewGuid():N}")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task GetLastKnown_returns_stored_status_without_touching_the_file()
    {
        string path = await CreateFileAsync("plain.pdf");
        CountingFingerprints fingerprints = new(_fingerprints, () => _quickHashCalls++);
        SourceValidationService service = new(fingerprints, FullHashFactory);
        SourceValidationRequest request = await BuildRequestAsync(path);

        File.Delete(path);
        SourceValidationResult result = await service.GetLastKnownAsync(request);

        result.Status.Should().Be(SourceValidationStatus.Current);
        result.FullHash.Should().Be(request.StoredFullHash);
        _quickHashCalls.Should().Be(0, "plain reads must not hash or touch the file");
        _fullHashCalls.Should().Be(0);
    }

    [Fact]
    public async Task Unchanged_file_validates_current_without_a_full_hash()
    {
        string path = await CreateFileAsync("unchanged.pdf");
        SourceValidationService service = new(_fingerprints, FullHashFactory);
        SourceValidationRequest request = await BuildRequestAsync(path);

        SourceValidationResult result = await service.EnsureValidatedAsync(request);

        result.Status.Should().Be(SourceValidationStatus.Current);
        result.ComputedFullHash.Should().BeFalse("the cheap quick-hash check is enough for an unchanged file");
        _fullHashCalls.Should().Be(0);
    }

    [Fact]
    public async Task Changed_file_triggers_one_full_hash_and_returns_changed()
    {
        string path = await CreateFileAsync("changed.pdf");
        SourceValidationService service = new(_fingerprints, FullHashFactory);
        SourceValidationRequest request = await BuildRequestAsync(path);
        await File.AppendAllTextAsync(path, "mutated");

        SourceValidationResult result = await service.EnsureValidatedAsync(request);

        result.Status.Should().Be(SourceValidationStatus.Changed);
        result.ComputedFullHash.Should().BeTrue();
        result.Warning.Should().Contain("bbox_basis_stale");
        _fullHashCalls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_in_flight_full_hash()
    {
        string path = await CreateFileAsync("concurrent.pdf");
        SourceValidationRequest request = await BuildRequestAsync(path);
        await File.AppendAllTextAsync(path, "mutated");

        TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;
        SourceValidationService service = new(_fingerprints,
            (p, ct) =>
            {
                Interlocked.Increment(ref factoryCalls);
                return gate.Task;
            });

        Task<SourceValidationResult> first = service.EnsureValidatedAsync(request);
        Task<SourceValidationResult> second = service.EnsureValidatedAsync(request);
        Task<SourceValidationResult> third = service.EnsureValidatedAsync(request);

        gate.SetResult("computed-hash-that-matches-nothing");
        SourceValidationResult[] results = await Task.WhenAll(first, second, third);

        factoryCalls.Should().Be(1, "all callers must coalesce onto a single in-flight validation");
        results.Should().OnlyContain(result => result.Status == SourceValidationStatus.Changed);
    }

    [Fact]
    public async Task Validated_current_is_reused_without_any_hash_on_reaccess()
    {
        string path = await CreateFileAsync("reused.pdf");
        SourceValidationRequest request = await BuildRequestAsync(path);
        CountingFingerprints fingerprints = new(_fingerprints, () => _quickHashCalls++);
        SourceValidationService service = new(fingerprints, FullHashFactory);

        SourceValidationResult first = await service.EnsureValidatedAsync(request);
        SourceValidationResult second = await service.EnsureValidatedAsync(request);
        SourceValidationResult? reuse = await service.TryGetCurrentAsync(request);

        first.Status.Should().Be(SourceValidationStatus.Current);
        second.Status.Should().Be(SourceValidationStatus.Current);
        reuse.Should().NotBeNull();
        reuse!.Status.Should().Be(SourceValidationStatus.Current);
        reuse.ResolvedPath.Should().Be(path);
        _fullHashCalls.Should().Be(0);
        _quickHashCalls.Should().Be(1, "the first validation runs one quick hash; re-access reuses the entry");
    }

    [Fact]
    public async Task Missing_file_validates_unavailable_and_is_not_cached_as_success()
    {
        string path = Path.Combine(_root, "missing.pdf");
        SourceValidationRequest request = new(FileAssetId.New(), path, path, 0, null, null, null,
            FileAssetStatus.Available, "test-basis-v1");

        SourceValidationService service = new(_fingerprints, FullHashFactory);
        SourceValidationResult result = await service.EnsureValidatedAsync(request);

        result.Status.Should().Be(SourceValidationStatus.Unavailable);
        _fullHashCalls.Should().Be(0);
    }

    [Fact]
    public async Task Basis_change_revalidates_under_the_new_basis()
    {
        string path = await CreateFileAsync("basis.pdf");
        SourceValidationRequest v1 = await BuildRequestAsync(path, "basis-v1");
        SourceValidationRequest v2 = await BuildRequestAsync(path, "basis-v2");
        SourceValidationService service = new(_fingerprints, FullHashFactory);

        await service.EnsureValidatedAsync(v1);
        SourceValidationResult result = await service.EnsureValidatedAsync(v2);

        result.Status.Should().Be(SourceValidationStatus.Current);
        _quickHashCalls.Should().Be(0);
    }

    [Fact]
    public async Task Invalidate_drops_runtime_state_and_revalidates_on_next_access()
    {
        string path = await CreateFileAsync("invalidate.pdf");
        SourceValidationService service = new(_fingerprints, FullHashFactory);
        SourceValidationRequest request = await BuildRequestAsync(path);

        await service.EnsureValidatedAsync(request);
        await service.InvalidateAsync(request.FileAssetId);

        SourceValidationResult result = await service.EnsureValidatedAsync(request);
        result.Status.Should().Be(SourceValidationStatus.Current);
    }

    [Fact]
    public async Task Invalidate_cancels_a_coalesced_full_hash_in_flight()
    {
        string path = await CreateFileAsync("invalidate-in-flight.pdf");
        SourceValidationRequest request = await BuildRequestAsync(path);
        await File.AppendAllTextAsync(path, "mutated");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SourceValidationService service = new(_fingerprints, async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "unreachable";
        });

        Task<SourceValidationResult> validation = service.EnsureValidatedAsync(request);
        await entered.Task;
        await service.InvalidateAsync(request.FileAssetId);

        Func<Task> awaitValidation = async () => await validation;
        await awaitValidation.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Persisted_fingerprint_change_does_not_reuse_a_previous_file_asset_validation()
    {
        string path = await CreateFileAsync("rebound.pdf");
        SourceValidationService service = new(_fingerprints, FullHashFactory);
        SourceValidationRequest original = await BuildRequestAsync(path);

        (await service.EnsureValidatedAsync(original)).Status.Should().Be(SourceValidationStatus.Current);
        SourceValidationRequest rebound = original with
        {
            StoredFullHash = "different-persisted-fingerprint",
            StoredQuickHash = "different-persisted-quick-hash"
        };

        SourceValidationResult result = await service.EnsureValidatedAsync(rebound);

        result.Status.Should().Be(SourceValidationStatus.Changed,
            "a rebind changes the persisted source fingerprint even when FileAssetId is stable");
    }

    [Fact]
    public async Task File_changed_back_to_original_revalidates_current_after_one_full_hash()
    {
        string path = await CreateFileAsync("revert.pdf");
        byte[] original = await File.ReadAllBytesAsync(path);
        SourceValidationService service = new(_fingerprints, FullHashFactory);
        SourceValidationRequest request = await BuildRequestAsync(path);

        await File.WriteAllBytesAsync(path, [.. original, 1, 2, 3]);
        SourceValidationResult changed = await service.EnsureValidatedAsync(request);
        changed.Status.Should().Be(SourceValidationStatus.Changed);
        int hashesAfterChange = _fullHashCalls;

        await File.WriteAllBytesAsync(path, original);
        SourceValidationResult current = await service.EnsureValidatedAsync(request);

        current.Status.Should().Be(SourceValidationStatus.Current);
        current.ComputedFullHash.Should().BeFalse("the cheap quick-hash confirms the reverted original content");
        _fullHashCalls.Should()
            .Be(hashesAfterChange, "restoring the original content makes the cheap check pass again");
    }

    private async Task<SourceValidationRequest> BuildRequestAsync(string path, string basis = "test-basis-v1")
    {
        Result<FileFingerprint> fingerprint = await _fingerprints.GetFileMetadataAsync(path);
        fingerprint.IsSuccess.Should().BeTrue(fingerprint.ErrorMessage);
        return new SourceValidationRequest(FileAssetId.New(), path, path, fingerprint.Value.SizeBytes,
            fingerprint.Value.MtimeUtc, fingerprint.Value.QuickHash, fingerprint.Value.FullBlake3,
            FileAssetStatus.Available, basis);
    }

    private async Task<string> CreateFileAsync(string name)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, "source content for validation");
        return path;
    }

    private Task<string> FullHashFactory(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _fullHashCalls);
        return Blake3Hash.ComputeFileAsync(path, cancellationToken);
    }

    private sealed class CountingFingerprints(IFileFingerprintService inner, Action onQuickHash)
        : IFileFingerprintService
    {
        public Task<Result<FileFingerprint>> GetFileMetadataAsync(string path,
            CancellationToken cancellationToken = default)
        {
            return inner.GetFileMetadataAsync(path, cancellationToken);
        }

        public Task<Result<string>> ComputeQuickHashAsync(string path,
            CancellationToken cancellationToken = default)
        {
            onQuickHash();
            return inner.ComputeQuickHashAsync(path, cancellationToken);
        }
    }
}
