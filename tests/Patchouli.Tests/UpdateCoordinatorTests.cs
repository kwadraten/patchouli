using System.Net;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Core.Update;
using Patchouli.Infrastructure.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: update check orchestration. At most one check or download runs
/// at a time (concurrent callers get <c>AlreadyRunning</c>), automatic checks
/// are throttled to 24 hours (manual checks bypass the throttle), every
/// recorded attempt — success or failure — counts toward the throttle, and a
/// release that is not newer than the running version never prompts.
/// </summary>
public sealed class UpdateCoordinatorTests : IDisposable
{
    private const string CurrentVersion = "0.3.12";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-update-coord-{Guid.NewGuid():N}")).FullName;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 20, 0, 42, 0, TimeSpan.Zero));
    private readonly FakeUpdateSource _source = new();
    private readonly List<string> _deliveredFiles = [];

    public void Dispose()
    {
        foreach (string path in _deliveredFiles)
        {
            UpdateDownloader.DeleteDownload(path);
        }

        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    private UpdateCheckSettingsService SettingsService()
    {
        return new UpdateCheckSettingsService(Path.Combine(_root, "settings.json"), _clock);
    }

    private UpdateDownloader Downloader(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? handler = null)
    {
        Func<HttpRequestMessage, Task<HttpResponseMessage>> effective = handler
            ?? (request => Task.FromResult(request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : BytesResponse(Encoding.UTF8.GetBytes(new string('x', 512)))));
        return new UpdateDownloader(new HttpClient(new AsyncFakeHandler(effective)));
    }

    private UpdateCoordinator Coordinator(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? downloadHandler = null)
    {
        return new UpdateCoordinator(
            _source,
            SettingsService(),
            Downloader(downloadHandler),
            new UpdateLauncher(),
            _clock,
            CurrentVersion);
    }

    private static HttpResponseMessage BytesResponse(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentLength = payload.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static ReleaseInfo MakeRelease(string tag, params ReleaseAssetInfo[] assets)
    {
        return new ReleaseInfo(tag, UpdateVersion.Parse(tag), "title", "body",
            IsDraft: false, IsPrerelease: false, PublishedAt: null, assets);
    }

    private static ReleaseAssetInfo Asset(string name, long size = 512)
    {
        return new ReleaseAssetInfo(name, $"https://example.test/{name}", size);
    }

    private static ReleaseInfo NewerReleaseWithAllPlatformAssets()
    {
        return MakeRelease("v0.3.13",
            Asset("Patchouli.NET-0.3.13-win-x64-setup.exe"),
            Asset("Patchouli.NET-0.3.13-osx-arm64.dmg"),
            Asset("Patchouli.NET-0.3.13-linux-x64.AppImage"));
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string what)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(Timeout));
        completed.Should().Be(task, $"{what} did not finish within {Timeout.TotalSeconds}s");
        return await task;
    }

    private static async Task WithTimeout(Task task, string what)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(Timeout));
        completed.Should().Be(task, $"{what} did not finish within {Timeout.TotalSeconds}s");
        await task;
    }

    [Fact]
    public async Task Second_concurrent_check_returns_AlreadyRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Handler = async _ =>
        {
            entered.SetResult();
            await releaseGate.Task;
            return Result<ReleaseInfo?>.Success(null);
        };
        UpdateCoordinator coordinator = Coordinator();

        Task<UpdateCheckOutcome> first = coordinator.CheckForUpdatesAsync(manual: true);
        await WithTimeout(entered.Task, "first check to reach the source");

        UpdateCheckOutcome second = await coordinator.CheckForUpdatesAsync(manual: true);

        second.Status.Should().Be(UpdateCheckStatus.AlreadyRunning);
        second.Release.Should().BeNull();

        releaseGate.SetResult();
        (await WithTimeout(first, "first check")).Status.Should().Be(UpdateCheckStatus.UpToDate);
    }

    [Fact]
    public async Task Download_started_during_a_check_is_rejected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Handler = async _ =>
        {
            entered.SetResult();
            await releaseGate.Task;
            return Result<ReleaseInfo?>.Success(null);
        };
        UpdateCoordinator coordinator = Coordinator();

        Task<UpdateCheckOutcome> check = coordinator.CheckForUpdatesAsync(manual: true);
        await WithTimeout(entered.Task, "check to reach the source");

        Result<VerifiedDownload> download = await coordinator.DownloadAsync(Asset("a.AppImage"));

        download.IsFailure.Should().BeTrue();
        download.ErrorMessage.Should().Contain("正在进行");

        releaseGate.SetResult();
        await WithTimeout(check, "check");
    }

    [Fact]
    public async Task Check_started_during_a_download_returns_AlreadyRunning()
    {
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 512));
        UpdateCoordinator coordinator = Coordinator(async request =>
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            requestReceived.SetResult();
            await releaseDownload.Task;
            return BytesResponse(payload);
        });

        Task<Result<VerifiedDownload>> download =
            coordinator.DownloadAsync(Asset("Patchouli.NET-0.3.13-linux-x64.AppImage", payload.Length));
        await WithTimeout(requestReceived.Task, "download to start");

        UpdateCheckOutcome check = await coordinator.CheckForUpdatesAsync(manual: true);

        check.Status.Should().Be(UpdateCheckStatus.AlreadyRunning);

        releaseDownload.SetResult();
        Result<VerifiedDownload> result = await WithTimeout(download, "download");
        result.IsSuccess.Should().BeTrue();
        _deliveredFiles.Add(result.Value.FilePath);
    }

    [Fact]
    public async Task Auto_check_within_24h_is_skipped_without_calling_source()
    {
        UpdateCheckSettingsService settings = SettingsService();
        await settings.SaveSettingsAsync(new UpdateCheckSettings(true, _clock.UtcNow));
        _source.Handler = _ => Task.FromResult(
            Result<ReleaseInfo?>.Success(NewerReleaseWithAllPlatformAssets()));
        UpdateCoordinator coordinator = Coordinator();

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: false);

        outcome.Status.Should().Be(UpdateCheckStatus.SkippedRecently);
        _source.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Manual_check_bypasses_the_throttle()
    {
        UpdateCheckSettingsService settings = SettingsService();
        await settings.SaveSettingsAsync(new UpdateCheckSettings(true, _clock.UtcNow));
        _source.Handler = _ => Task.FromResult(
            Result<ReleaseInfo?>.Success(NewerReleaseWithAllPlatformAssets()));
        UpdateCoordinator coordinator = Coordinator();

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: true);

        outcome.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        outcome.Release.Should().NotBeNull();
        outcome.Asset.Should().NotBeNull();
        _source.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Failed_check_records_time_and_throttles_later_auto_checks()
    {
        _source.Handler = _ => Task.FromResult(
            Result<ReleaseInfo?>.Failure(AppErrorCodes.NetworkError, "boom"));
        UpdateCheckSettingsService settings = SettingsService();
        UpdateCoordinator coordinator = new UpdateCoordinator(
            _source, settings, Downloader(), new UpdateLauncher(), _clock, CurrentVersion);

        UpdateCheckOutcome first = await coordinator.CheckForUpdatesAsync(manual: false);

        first.Status.Should().Be(UpdateCheckStatus.CheckFailed);
        (await settings.GetSettingsAsync()).Value.LastCheckUtc.Should().Be(_clock.UtcNow);

        // No retry storm: the very next automatic check is throttled too.
        UpdateCheckOutcome second = await coordinator.CheckForUpdatesAsync(manual: false);

        second.Status.Should().Be(UpdateCheckStatus.SkippedRecently);
        _source.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Auto_check_disabled_returns_AutoCheckDisabled()
    {
        UpdateCheckSettingsService settings = SettingsService();
        await settings.SaveSettingsAsync(new UpdateCheckSettings(false, LastCheckUtc: null));
        UpdateCoordinator coordinator = Coordinator();

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: false);

        outcome.Status.Should().Be(UpdateCheckStatus.AutoCheckDisabled);
        _source.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("v0.3.10")] // older
    [InlineData("v0.3.12")] // equal
    public async Task Older_or_equal_release_is_UpToDate(string tag)
    {
        _source.Handler = _ => Task.FromResult(Result<ReleaseInfo?>.Success(MakeRelease(tag)));
        UpdateCoordinator coordinator = Coordinator();

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: true);

        outcome.Status.Should().Be(UpdateCheckStatus.UpToDate);
        outcome.Asset.Should().BeNull();
    }

    [Fact]
    public async Task Newer_release_without_platform_asset_is_NotAvailableForPlatform()
    {
        _source.Handler = _ => Task.FromResult(Result<ReleaseInfo?>.Success(
            MakeRelease("v0.3.13", Asset("Patchouli.NET-0.3.13.zip"))));
        UpdateCoordinator coordinator = Coordinator();

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: true);

        outcome.Status.Should().Be(UpdateCheckStatus.NotAvailableForPlatform);
        outcome.Release.Should().NotBeNull();
        outcome.Asset.Should().BeNull();
        outcome.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task No_stable_release_is_UpToDate()
    {
        _source.Handler = _ => Task.FromResult(Result<ReleaseInfo?>.Success(null));
        UpdateCoordinator coordinator = Coordinator();

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: true);

        outcome.Status.Should().Be(UpdateCheckStatus.UpToDate);
    }

    [Fact]
    public async Task Successful_check_records_the_check_time()
    {
        _source.Handler = _ => Task.FromResult(
            Result<ReleaseInfo?>.Success(NewerReleaseWithAllPlatformAssets()));
        UpdateCheckSettingsService settings = SettingsService();
        UpdateCoordinator coordinator = new UpdateCoordinator(
            _source, settings, Downloader(), new UpdateLauncher(), _clock, CurrentVersion);

        UpdateCheckOutcome outcome = await coordinator.CheckForUpdatesAsync(manual: false);

        outcome.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        (await settings.GetSettingsAsync()).Value.LastCheckUtc.Should().Be(_clock.UtcNow);
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        public int CallCount;
        public Func<CancellationToken, Task<Result<ReleaseInfo?>>> Handler { get; set; } =
            _ => Task.FromResult(Result<ReleaseInfo?>.Success(null));

        public Task<Result<ReleaseInfo?>> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Handler(cancellationToken);
        }
    }

    private sealed class AsyncFakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public AsyncFakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
