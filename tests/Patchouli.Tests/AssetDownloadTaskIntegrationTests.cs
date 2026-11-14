using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Core.Downloads;
using Patchouli.Core.Update;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.Infrastructure.Agents;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class AssetDownloadTaskIntegrationTests(AvaloniaSharedSessionFixture headless)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string AgentUrl = "https://assets.example.test/opencode.zip";
    private const string UpdateUrl = "https://assets.example.test/Patchouli-setup.exe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_install_backgrounds_only_the_dialog_and_never_applies_an_old_hosts_result(
        bool switchLibrary)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string root = Path.GetDirectoryName(settings.Path)!;
            string installRoot = Path.Combine(root, "agent-install");
            string executableName = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
            byte[] archive = AgentArchive(executableName);
            using GatedAssetStream body = new(archive);
            using HttpClient client = new(new GatedAssetHttpHandler(AgentUrl, body));
            RecordingDialogs dialogs = new(ConfirmDialogResult.Confirm);
            ScriptedProcessRunner processes = new()
            {
                Handler = _ => ScriptedProcessRunner.Output("1.19.3")
            };
            await using MainWindowViewModel main = new(dialogs: dialogs, settingsPath: settings.Path,
                appPaths: new TestAppPaths(root), infrastructure: OfflineInfrastructure(processes));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            using AgentIntegrationSettingsViewModel section = new(main,
                installer: new AgentInstaller(client, installRoot));
            AgentIntegrationRowViewModel row = section.Agents.Single(item => item.Entry.Id == "opencode");
            AgentInstallOffer offer = new("opencode", "OpenCode", "1.19.3", "windows-x86_64",
                AgentUrl, null, executableName, ["acp"], [], "https://github.com/anomalyco/opencode",
                "MIT", "https://opensource.org/license/mit");
            row.ApplyDetection(new AgentDetectionResult(row.Entry, AgentDetectionStatus.NotInstalled,
                null, [], offer));
            string? baselinePath = row.CustomPath;
            AgentIntegrationAppSettings baselineSettings = main.AppOptions.Agents;
            bool pathChangedOnUi = false;
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(row.CustomPath))
                {
                    Dispatcher.UIThread.CheckAccess().Should().BeTrue();
                    pathChangedOnUi = true;
                }
            };
            ConcurrentQueue<DownloadFileProgress> snapshots = new();

            void Record(object? sender, EventArgs args)
            {
                foreach (DownloadFileProgress file in services.DownloadTasks.List().SelectMany(task => task.Files))
                {
                    snapshots.Enqueue(file);
                }
            }

            services.DownloadTasks.Changed += Record;
            Task pending = section.InstallAsync(row, CancellationToken.None);
            try
            {
                await body.Blocked.Task.WaitAsync(Deadline);
                DownloadOperationDialogViewModel dialog = await dialogs.DownloadShown.Task.WaitAsync(Deadline);
                AssertLiveDownload(services.DownloadTasks, "opencode.zip", archive.Length);
                pending.IsCompleted.Should().BeFalse();
                row.CustomPath.Should().Be(baselinePath);
                section.IsDirty.Should().BeFalse();
                await dialog.BackgroundCommand.ExecuteAsync();
                await dialogs.DownloadClosed.Task.WaitAsync(Deadline);
                dialog.BackgroundRequested.Should().BeTrue();
                pending.IsCompleted.Should().BeFalse("background closes only the dedicated download dialog");
                AssertLiveDownload(services.DownloadTasks, "opencode.zip", archive.Length);

                if (switchLibrary)
                {
                    int generation = main.LibraryGeneration;
                    await main.BeginLibrarySwitchAsync().WaitAsync(Deadline);
                    main.LibraryGeneration.Should().BeGreaterThan(generation);
                    await pending.WaitAsync(Deadline);
                    services.DownloadTasks.List().Single().State.Should().Be(DownloadTaskService.CancelledState);
                    body.CancellationObserved.Task.IsCompleted.Should().BeTrue();
                    row.CustomPath.Should().Be(baselinePath);
                    section.IsDirty.Should().BeFalse("an old host must not edit the settings draft");
                    main.AppOptions.Agents.Should().BeEquivalentTo(baselineSettings);
                    pathChangedOnUi.Should().BeFalse();
                    Directory.Exists(Path.Combine(installRoot, "opencode")).Should().BeFalse();
                }
            }
            finally
            {
                body.Release.TrySetResult();
                // Drain before disposing the main/section/client, including when a live assertion fails.
                await pending.WaitAsync(Deadline);
                services.DownloadTasks.Changed -= Record;
            }

            if (!switchLibrary)
            {
                DownloadTaskStatus completed = services.DownloadTasks.List().Should().ContainSingle().Subject;
                completed.State.Should().Be(DownloadTaskService.SucceededState);
                completed.CanRetry.Should().BeFalse();
                DownloadFileProgress file = completed.Files.Should().ContainSingle().Subject;
                file.DownloadedBytes.Should().Be(archive.Length);
                file.TotalBytes.Should().Be(archive.Length);
                file.State.Should().Be("succeeded");
                snapshots.Should().Contain(value => value.State == "verifying" &&
                                                    value.DownloadedBytes == archive.Length &&
                                                    value.TotalBytes == archive.Length,
                    "extraction and activation retain the completed download byte counts");
                DownloadFileProgress[] afterDownload = snapshots.SkipWhile(value =>
                    value.DownloadedBytes != archive.Length).ToArray();
                afterDownload.Should().NotBeEmpty().And.OnlyContain(value =>
                    value.DownloadedBytes == archive.Length && value.TotalBytes == archive.Length);
                string installedPath = Path.Combine(installRoot, "opencode", executableName);
                row.CustomPath.Should().Be(installedPath);
                File.ReadAllText(installedPath).Should().Be("offline executable fixture");
                section.IsDirty.Should().BeTrue("the successful real install updates the editable draft");
                pathChangedOnUi.Should().BeTrue();
                processes.Requests.Should().Contain(request => request.FileName == installedPath,
                    "post-install detection uses only the fake process runner");
                main.AppOptions.Agents.Should().BeEquivalentTo(baselineSettings,
                    "installation edits the draft, not persisted settings");
            }

            dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().ContainSingle();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Default_agent_install_downloads_through_the_host_agent_download_client()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string root = Path.GetDirectoryName(settings.Path)!;
            string installRoot = Path.Combine(root, "agent-install");
            string executableName = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
            byte[] archive = AgentArchive(executableName);
            using GatedAssetStream body = new(archive);
            // The single AgentDownload client takes ownership of this handler.
            GatedAssetHttpHandler agentHandler = new(AgentUrl, body);
            ConcurrentQueue<HostHttpPurpose> purposes = new();
            RecordingDialogs dialogs = new(ConfirmDialogResult.Confirm);
            ScriptedProcessRunner processes = new() { Handler = _ => ScriptedProcessRunner.Output("1.19.3") };
            await using MainWindowViewModel main = new(dialogs: dialogs, settingsPath: settings.Path,
                appPaths: new TestAppPaths(root), infrastructure: new HostInfrastructureOptions
                {
                    ProcessRunner = processes,
                    HttpHandlerFactory = purpose =>
                    {
                        purposes.Enqueue(purpose);
                        return purpose == HostHttpPurpose.AgentDownload
                            ? agentHandler
                            : new GatedAssetHttpHandler();
                    }
                });
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            using AgentIntegrationSettingsViewModel section = new(main) { DefaultInstallRoot = installRoot };
            AgentIntegrationRowViewModel row = section.Agents.Single(item => item.Entry.Id == "opencode");
            AgentInstallOffer offer = new("opencode", "OpenCode", "1.19.3", "windows-x86_64",
                AgentUrl, null, executableName, ["acp"], [], "https://github.com/anomalyco/opencode",
                "MIT", "https://opensource.org/license/mit");
            row.ApplyDetection(new AgentDetectionResult(row.Entry, AgentDetectionStatus.NotInstalled,
                null, [], offer));
            purposes.Should().NotContain(HostHttpPurpose.AgentDownload,
                "the section must not create its download client before an install starts");

            Task pending = section.InstallAsync(row, CancellationToken.None);
            try
            {
                await body.Blocked.Task.WaitAsync(Deadline);
                purposes.Should().Contain(HostHttpPurpose.AgentDownload);
            }
            finally
            {
                body.Release.TrySetResult();
                await pending.WaitAsync(Deadline);
            }

            row.CustomPath.Should().Be(Path.Combine(installRoot, "opencode", executableName));
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_prompts_for_installation_only_after_real_completion_and_not_after_a_library_switch(
        bool switchLibrary)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            byte[] assetBytes = Encoding.UTF8.GetBytes(new string('x', 4096));
            using GatedAssetStream body = new(assetBytes);
            RecordingDialogs dialogs = new(ConfirmDialogResult.Cancel);
            ScriptedProcessRunner processes = new();
            HostInfrastructureOptions infrastructure = OfflineInfrastructure(processes) with
            {
                HttpHandlerFactory = UpdateHandlerFactory(body, assetBytes)
            };
            await using MainWindowViewModel main = new(dialogs: dialogs, settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!),
                infrastructure: infrastructure);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            ReleaseAssetInfo asset = new("Patchouli-setup.exe", UpdateUrl, assetBytes.Length,
                ChecksumSidecarStatus.Present);
            UpdateVersion.TryParse("99.1.0", out UpdateVersion? version).Should().BeTrue();
            ReleaseInfo release = new("v99.1.0", version!, "Offline update", "", false, false,
                DateTimeOffset.UtcNow, [asset]);
            Task pending = new UpdateFlowService(main).DownloadAndConfirmInstallAsync(release, asset);
            try
            {
                await body.Blocked.Task.WaitAsync(Deadline);
                DownloadOperationDialogViewModel dialog = await dialogs.DownloadShown.Task.WaitAsync(Deadline);
                AssertLiveDownload(services.DownloadTasks, asset.Name, assetBytes.Length);
                pending.IsCompleted.Should().BeFalse();
                dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().BeEmpty(
                    "an installer must never be offered while its body is still being downloaded");
                await dialog.BackgroundCommand.ExecuteAsync();
                await dialogs.DownloadClosed.Task.WaitAsync(Deadline);
                dialog.BackgroundRequested.Should().BeTrue();
                pending.IsCompleted.Should().BeFalse();
                AssertLiveDownload(services.DownloadTasks, asset.Name, assetBytes.Length);
                dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().BeEmpty();
                if (switchLibrary)
                {
                    await main.BeginLibrarySwitchAsync().WaitAsync(Deadline);
                    await pending.WaitAsync(Deadline);
                    services.DownloadTasks.List().Single().State.Should().Be(DownloadTaskService.CancelledState);
                    body.CancellationObserved.Task.IsCompleted.Should().BeTrue();
                    dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().BeEmpty();
                }
            }
            finally
            {
                body.Release.TrySetResult();
                await pending.WaitAsync(Deadline);
            }

            if (!switchLibrary)
            {
                DownloadTaskStatus completed = services.DownloadTasks.List().Should().ContainSingle().Subject;
                completed.State.Should().Be(DownloadTaskService.SucceededState);
                completed.CanRetry.Should().BeFalse();
                completed.Files.Single().DownloadedBytes.Should().Be(assetBytes.Length);
                completed.Files.Single().TotalBytes.Should().Be(assetBytes.Length);
                dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().ContainSingle(
                    "real verified completion offers explicit installation on the dispatcher");
                dialogs.InstallPromptOnUi.Should().BeTrue();
            }

            processes.Requests.Should().BeEmpty("declining installation must not launch an installer or exit");
            return 0;
        }, CancellationToken.None);
    }

    private static Func<HostHttpPurpose, HttpMessageHandler> UpdateHandlerFactory(
        GatedAssetStream body, byte[] assetBytes)
    {
        return purpose => purpose == HostHttpPurpose.UpdateDownload
            ? new GatedAssetHttpHandler(UpdateUrl, body, assetBytes)
            : new GatedAssetHttpHandler();
    }

    private static HostInfrastructureOptions OfflineInfrastructure(ScriptedProcessRunner processes)
    {
        return new HostInfrastructureOptions
        {
            ProcessRunner = processes,
            HttpHandlerFactory = _ => new GatedAssetHttpHandler()
        };
    }

    private static void AssertLiveDownload(DownloadTaskService downloads, string name, int total)
    {
        DownloadTaskStatus running = downloads.List().Should().ContainSingle().Subject;
        running.State.Should().Be(DownloadTaskService.RunningState);
        running.CanRetry.Should().BeFalse();
        DownloadFileProgress file = running.Files.Should().ContainSingle().Subject;
        file.RelativePath.Should().Be(name);
        file.DownloadedBytes.Should().BeGreaterThan(0).And.BeLessThan(total);
        file.TotalBytes.Should().Be(total);
    }

    private static byte[] AgentArchive(string executableName)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
        using (Stream entry = archive.CreateEntry(executableName).Open())
        {
            entry.Write(Encoding.UTF8.GetBytes("offline executable fixture"));
        }

        return output.ToArray();
    }

    private sealed class RecordingDialogs(ConfirmDialogResult confirmation) : IDialogService
    {
        public List<object> Shown { get; } = [];

        public TaskCompletionSource<DownloadOperationDialogViewModel> DownloadShown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DownloadClosed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool InstallPromptOnUi { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            Dispatcher.UIThread.CheckAccess().Should().BeTrue();
            Shown.Add(viewModel);
            if (viewModel is not DownloadOperationDialogViewModel download)
            {
                return Task.CompletedTask;
            }

            download.RequestClose += _ =>
            {
                Dispatcher.UIThread.CheckAccess().Should().BeTrue();
                DownloadClosed.TrySetResult();
            };
            DownloadShown.TrySetResult(download);
            return DownloadClosed.Task;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Dispatcher.UIThread.CheckAccess().Should().BeTrue();
            Shown.Add(viewModel);
            if (viewModel is ConfirmDialogViewModel)
            {
                InstallPromptOnUi = Dispatcher.UIThread.CheckAccess();
                return Task.FromResult(confirmation is TResult value ? value : default);
            }

            return Task.FromResult<TResult?>(default);
        }
    }
}

/// <summary>A nonseekable response body that cannot finish until explicitly released.</summary>
internal sealed class GatedAssetStream(byte[] bytes) : Stream
{
    private int _position;
    public long ByteCount => bytes.LongLength;
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource CancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int halfway = Math.Max(1, bytes.Length / 2);
        if (_position >= halfway && !Release.Task.IsCompleted)
        {
            // The caller has already written and reported the first chunk before asking for more.
            Blocked.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        int end = _position < halfway ? halfway : bytes.Length;
        int count = Math.Min(buffer.Length, end - _position);
        bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Never opens a socket; only the exact test asset and optional checksum sidecar exist.</summary>
internal sealed class GatedAssetHttpHandler(
    string? url = null,
    GatedAssetStream? body = null,
    byte[]? checksumPayload = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage response;
        if (url is not null && request.RequestUri?.AbsoluteUri == url && body is not null)
        {
            StreamContent content = new(body);
            // The stream deliberately does not expose Length or Position.
            content.Headers.ContentLength = body.ByteCount;
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        else if (url is not null && request.RequestUri?.AbsoluteUri == url + ".sha256" &&
                 checksumPayload is not null)
        {
            response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Convert.ToHexString(SHA256.HashData(checksumPayload))
                    .ToLowerInvariant())
            };
        }
        else
        {
            response = new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return Task.FromResult(response);
    }
}
