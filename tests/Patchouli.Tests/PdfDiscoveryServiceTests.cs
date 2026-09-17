using FluentAssertions;
using System.Collections.Concurrent;
using Patchouli.Core.Diagnostics;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Tests;

public sealed class PdfDiscoveryServiceTests
{
    [Fact]
    public async Task Scan_activity_uses_host_lifetime_and_closes_when_the_old_host_is_cancelled()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-host-{Guid.NewGuid():N}"))
            .FullName;
        using HostActivityTracker tracker = new(TimeSpan.Zero);
        using CancellationTokenSource hostLifetime = new();
        BlockingDirectoryAdapter adapter = new();
        PdfDiscoveryService service = new(new FileSearchRootAccess(adapter), tracker, hostLifetime.Token);

        try
        {
            Task<PdfScanResult> scan = service.ScanDirectoryAsync(Selected(dir));
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tracker.Current.Items.Should().ContainSingle(item => item.Kind == HostActivityKind.Scanning);

            hostLifetime.Cancel();
            PdfScanResult result = await scan;

            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Cancelled);
            tracker.Current.IsBusy.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_returns_only_pdfs()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "doc1.pdf");
            TestFixtures.CopyRealThreePagePdfTo(dir, "doc2.pdf");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a pdf");

            PdfDiscoveryService service = new();
            PdfScanResult result = await service.ScanDirectoryAsync(Selected(dir));

            result.Candidates.Should().HaveCount(2);
            result.TotalCount.Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_ignores_bin_and_obj_subdirectories()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            TestFixtures.CopyRealThreePagePdfTo(dir, "valid.pdf");
            TestFixtures.CopyRealThreePagePdfTo(Path.Combine(dir, "bin"), "output.pdf");
            TestFixtures.CopyRealThreePagePdfTo(Path.Combine(dir, "obj"), "temp.pdf");

            PdfDiscoveryService service = new(new FileSearchRootAccess(exclusionPatterns:
                [@"(^|/)bin(/|$)", @"(^|/)obj(/|$)"]));
            PdfScanResult result = await service.ScanDirectoryAsync(Selected(dir));

            result.Candidates.Should().HaveCount(1);
            result.ExcludedEntries.Should().HaveCount(2);
            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Complete);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_returns_empty_for_nonexistent_directory()
    {
        PdfDiscoveryService service = new();
        PdfScanResult result = await service.ScanDirectoryAsync(Selected("X:\\nonexistent\\path"));

        result.Candidates.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Failed);
        result.RootStatus.Should().Be(FileSearchRootStatuses.Offline);
    }

    [Fact]
    public async Task ScanDirectoryAsync_matches_pdf_extension_case_insensitively()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "upper.PDF");
            TestFixtures.CopyRealThreePagePdfTo(dir, "mixed.Pdf");

            PdfScanResult result = await new PdfDiscoveryService().ScanDirectoryAsync(Selected(dir));

            result.Candidates.Should().HaveCount(2);
            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Complete);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_preserves_candidates_when_one_directory_fails()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "available.pdf");
            string denied = Directory.CreateDirectory(Path.Combine(dir, "denied")).FullName;
            PdfDiscoveryService service = new(new FileSearchRootAccess(new DeniedDirectoryAdapter(denied)));

            PdfScanResult result = await service.ScanDirectoryAsync(Selected(dir));

            result.Candidates.Should().ContainSingle();
            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Partial);
            result.RootStatus.Should().Be(FileSearchRootStatuses.Partial);
            result.SkippedDirectories.Should().ContainSingle(issue => issue.Code == "access_denied");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_returns_cancelled_when_directory_resolution_is_cancelled()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            PdfDiscoveryService service = new(new FileSearchRootAccess(new CancellingDirectoryAdapter()));

            PdfScanResult result = await service.ScanDirectoryAsync(Selected(dir), cancellation.Token);

            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Cancelled);
            result.Candidates.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static SelectedFileSearchRoot Selected(string path)
    {
        return new SelectedFileSearchRoot(path, "test_picker",
            FileSearchRootAuthorizationKinds.None, null, null, DateTimeOffset.UtcNow);
    }

    private sealed class DeniedDirectoryAdapter(string deniedPath) : INativeFileAccessAdapter
    {
        private readonly PortableNativeFileAccessAdapter _inner = new();

        public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
            CancellationToken cancellationToken)
        {
            return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(deniedPath)), StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromResult(new NativeDirectoryResolution(null, "access_denied", "Test denial."))
                : _inner.ResolveDirectoryAsync(path, cancellationToken);
        }

        public ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
            CancellationToken cancellationToken)
        {
            return _inner.MaterializeFileAsync(path, cancellationToken);
        }
    }

    private sealed class CancellingDirectoryAdapter : INativeFileAccessAdapter
    {
        public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new NativeFileMaterialization(true));
        }
    }

    private sealed class BlockingDirectoryAdapter : INativeFileAccessAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The host cancellation should stop directory resolution.");
        }

        public ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new NativeFileMaterialization(true));
        }
    }
}
