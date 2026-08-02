using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Hashing;

namespace Patchouli.Tests;

public sealed class SourceFingerprintValidationServiceTests
{
    [Fact]
    public async Task Unchanged_file_is_full_hashed_once_and_reused_without_rehashing()
    {
        await using TempFile file = await TempFile.CreateAsync("source content");
        int fullHashCount = 0;
        SourceFingerprintValidationService service =
            new(fullHashComputer: CountingHash(() => Interlocked.Increment(ref fullHashCount)));

        Result<SourceFingerprintValidation> first = await service.ValidateAsync(file.Path, file.Length,
            file.Mtime, SourceFingerprintBasis.Blake3V1);
        Result<SourceFingerprintValidation> second = await service.ValidateAsync(file.Path, file.Length,
            file.Mtime, SourceFingerprintBasis.Blake3V1);

        first.IsSuccess.Should().BeTrue();
        first.Value.FromCache.Should().BeFalse();
        second.Value.FromCache.Should().BeTrue();
        second.Value.FullBlake3.Should().Be(first.Value.FullBlake3);
        fullHashCount.Should().Be(1);
        service.CacheMetrics.Hits.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Concurrent_validations_share_one_inflight_full_hash()
    {
        await using TempFile file = await TempFile.CreateAsync("concurrent source");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int fullHashCount = 0;
        SourceFingerprintValidationService service = new(fullHashComputer: async (path, ct) =>
        {
            Interlocked.Increment(ref fullHashCount);
            await release.Task.WaitAsync(ct);
            return await Blake3Hash.ComputeFileAsync(path, ct);
        });

        Task<Result<SourceFingerprintValidation>>[] requests = Enumerable.Range(0, 8)
            .Select(_ => service.ValidateAsync(file.Path, file.Length, file.Mtime, SourceFingerprintBasis.Blake3V1))
            .ToArray();
        await Task.Delay(100);
        fullHashCount.Should().Be(1, "all callers share one in-flight full hash validation");
        release.SetResult();
        Result<SourceFingerprintValidation>[] results = await Task.WhenAll(requests);
        results.Should().OnlyContain(result => result.IsSuccess);
        fullHashCount.Should().Be(1);
    }

    [Fact]
    public async Task Changed_mtime_invalidates_the_cached_fingerprint()
    {
        await using TempFile file = await TempFile.CreateAsync("mtime invalidation");
        int fullHashCount = 0;
        SourceFingerprintValidationService service =
            new(fullHashComputer: CountingHash(() => Interlocked.Increment(ref fullHashCount)));

        await service.ValidateAsync(file.Path, file.Length, file.Mtime, SourceFingerprintBasis.Blake3V1);
        DateTimeOffset newMtime = file.Mtime.AddMinutes(5);
        Result<SourceFingerprintValidation> after = await service.ValidateAsync(file.Path, file.Length, newMtime,
            SourceFingerprintBasis.Blake3V1);

        after.Value.FromCache.Should().BeFalse();
        fullHashCount.Should().Be(2, "a different modification time must invalidate the cached result");
    }

    [Fact]
    public async Task Changed_size_invalidates_the_cached_fingerprint()
    {
        await using TempFile file = await TempFile.CreateAsync("small");
        int fullHashCount = 0;
        SourceFingerprintValidationService service =
            new(fullHashComputer: CountingHash(() => Interlocked.Increment(ref fullHashCount)));

        await service.ValidateAsync(file.Path, file.Length, file.Mtime, SourceFingerprintBasis.Blake3V1);
        await File.AppendAllTextAsync(file.Path, "grown");
        FileInfo grown = new(file.Path);
        Result<SourceFingerprintValidation> after = await service.ValidateAsync(file.Path, grown.Length,
            grown.LastWriteTimeUtc,
            SourceFingerprintBasis.Blake3V1);

        after.Value.FromCache.Should().BeFalse();
        fullHashCount.Should().Be(2, "a different file size must invalidate the cached result");
    }

    [Fact]
    public async Task Different_fingerprint_basis_invalidates_the_cached_fingerprint()
    {
        await using TempFile file = await TempFile.CreateAsync("basis invalidation");
        int fullHashCount = 0;
        SourceFingerprintValidationService service =
            new(fullHashComputer: CountingHash(() => Interlocked.Increment(ref fullHashCount)));

        await service.ValidateAsync(file.Path, file.Length, file.Mtime, SourceFingerprintBasis.Blake3V1);
        Result<SourceFingerprintValidation> after = await service.ValidateAsync(file.Path, file.Length, file.Mtime,
            "blake3:v2");

        after.Value.FromCache.Should().BeFalse();
        fullHashCount.Should().Be(2, "a fingerprint basis upgrade must invalidate every cached result");
    }

    [Fact]
    public async Task Failed_full_hash_is_not_cached_and_is_retried()
    {
        await using TempFile file = await TempFile.CreateAsync("retry source");
        int fullHashCount = 0;
        SourceFingerprintValidationService service = new(fullHashComputer: (path, ct) =>
        {
            int attempt = Interlocked.Increment(ref fullHashCount);
            return attempt == 1
                ? Task.FromException<string>(new IOException("transient I/O failure"))
                : Blake3Hash.ComputeFileAsync(path, ct);
        });

        Result<SourceFingerprintValidation> first = await service.ValidateAsync(file.Path, file.Length, file.Mtime,
            SourceFingerprintBasis.Blake3V1);
        Result<SourceFingerprintValidation> second = await service.ValidateAsync(file.Path, file.Length, file.Mtime,
            SourceFingerprintBasis.Blake3V1);

        first.IsFailure.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        fullHashCount.Should().Be(2);
    }

    [Fact]
    public async Task Canceling_one_waiter_does_not_cancel_shared_validation()
    {
        await using TempFile file = await TempFile.CreateAsync("cancel source");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SourceFingerprintValidationService service = new(fullHashComputer: async (path, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return await Blake3Hash.ComputeFileAsync(path, ct);
        });

        using CancellationTokenSource canceledWaiter = new();
        Task<Result<SourceFingerprintValidation>> canceled = service.ValidateAsync(file.Path, file.Length, file.Mtime,
            SourceFingerprintBasis.Blake3V1, canceledWaiter.Token);
        Task<Result<SourceFingerprintValidation>> surviving = service.ValidateAsync(file.Path, file.Length, file.Mtime,
            SourceFingerprintBasis.Blake3V1);
        await Task.Delay(100);
        canceledWaiter.Cancel();

        Func<Task> expectCancellation = async () => await canceled;
        await expectCancellation.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        (await surviving).IsSuccess.Should().BeTrue();
    }

    private static Func<string, CancellationToken, Task<string>> CountingHash(Action onHash)
    {
        return async (path, ct) =>
        {
            onHash();
            return await Blake3Hash.ComputeFileAsync(path, ct);
        };
    }

    private sealed class TempFile : IAsyncDisposable
    {
        private TempFile(string path)
        {
            Path = path;
            FileInfo info = new(path);
            Length = info.Length;
            Mtime = info.LastWriteTimeUtc;
        }

        public string Path { get; }
        public long Length { get; }
        public DateTimeOffset Mtime { get; }

        public static async Task<TempFile> CreateAsync(string content)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"patchouli-source-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, content);
            return new TempFile(path);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
