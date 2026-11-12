using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Avalonia.Headless;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Core.Csl;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Csl;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class CslDownloadTaskTests(AvaloniaSharedSessionFixture headless)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Installing_catalog_style_tracks_real_body_and_installation(
        bool unknownLength, bool invalidXml)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            byte[] bytes = Encoding.UTF8.GetBytes(invalidXml ? "<not-valid-csl>" : StyleXml);
            using DownloadProgressTestStream body = new(bytes);
            RecordingDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs, autoStartMcpServer: false,
                settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!),
                infrastructure: new HostInfrastructureOptions
                {
                    ProcessRunner = new NoProcesses(),
                    HttpHandlerFactory = _ => new NoNetworkHandler()
                });
            await main.OpenDatabaseCommand.ExecuteAsync().WaitAsync(Deadline);
            await main.Library.CreateCommand.ExecuteAsync().WaitAsync(Deadline);
            HostServices services = await main.ServicesAsync().WaitAsync(Deadline);
            CslStyleManagerViewModel manager = new(main, StyleClientFactory(body, unknownLength));
            ConcurrentQueue<bool> propertyThreads = new();
            ConcurrentQueue<bool> collectionThreads = new();
            manager.PropertyChanged += (_, _) => propertyThreads.Enqueue(Dispatcher.UIThread.CheckAccess());
            manager.InstalledStyles.CollectionChanged += (_, _) =>
                collectionThreads.Enqueue(Dispatcher.UIThread.CheckAccess());
            CslCatalogStyle catalog = new("progress-style", "Progress Style",
                "https://progress.invalid/styles/progress-style.csl", "test");
            ConcurrentQueue<DownloadTaskStatus> snapshots = new();

            void RecordSnapshot(object? sender, EventArgs args)
            {
                foreach (DownloadTaskStatus snapshot in services.DownloadTasks.List())
                {
                    snapshots.Enqueue(snapshot);
                }
            }

            services.DownloadTasks.Changed += RecordSnapshot;
            Task installation = manager.InstallStyleAsync(catalog);
            try
            {
                await body.FirstReported.Task.WaitAsync(Deadline);
                DownloadOperationDialogViewModel dialog = await dialogs.DownloadShown.Task.WaitAsync(Deadline);
                DownloadTaskStatus running = services.DownloadTasks.List().Should().ContainSingle().Which;
                running.State.Should().Be(DownloadTaskService.RunningState);
                running.CanRetry.Should().BeFalse("a CSL install is tied to its original library context");
                snapshots.Should().Contain(snapshot => snapshot.Files.Count == 1 &&
                                                       snapshot.Files[0].RelativePath == "progress-style.csl" &&
                                                       snapshot.Files[0].State == "queued" &&
                                                       snapshot.Files[0].DownloadedBytes == 0,
                    "the registry must have the relative file row before the body starts");
                DownloadFileProgress file = running.Files.Should().ContainSingle().Which;
                file.RelativePath.Should().Be("progress-style.csl");
                Path.IsPathRooted(file.RelativePath).Should().BeFalse();
                file.State.Should().Be("running");
                file.DownloadedBytes.Should().BeGreaterThan(0).And.BeLessThan(bytes.Length);
                file.TotalBytes.Should().Be(unknownLength ? null : (long?)bytes.Length);
                running.IsIndeterminate.Should().Be(unknownLength);
                installation.IsCompleted.Should().BeFalse();
                (await services.CslStore.ListInstalledStylesAsync().WaitAsync(Deadline))
                    .Value.Should().NotContain(style => style.StyleId == catalog.StyleId,
                        "a partially received body must never install the downloaded style");

                await dialog.ToggleDetailsCommand.ExecuteAsync().WaitAsync(Deadline);
                dialog.IsDetailsVisible.Should().BeTrue();
                dialog.Files.Should().ContainSingle().Which.RelativePath.Should().Be("progress-style.csl");
                await dialog.BackgroundCommand.ExecuteAsync().WaitAsync(Deadline);
                dialog.BackgroundRequested.Should().BeTrue();
                installation.IsCompleted.Should().BeFalse("backgrounding only closes presentation");
                DownloadTaskStatus background = services.DownloadTasks.List().Single();
                background.State.Should().Be(DownloadTaskService.RunningState);
                background.Files.Single().Should().Be(file);
                manager.InstalledStyles.Should().NotContain(style => style.StyleId == catalog.StyleId);

                body.Release();
                await installation.WaitAsync(Deadline);
                Dispatcher.UIThread.CheckAccess().Should().BeTrue();
                DownloadTaskStatus finished = services.DownloadTasks.List().Single();
                DownloadFileProgress completedFile = finished.Files.Should().ContainSingle().Which;
                completedFile.DownloadedBytes.Should().Be(bytes.Length);
                completedFile.TotalBytes.Should().Be(unknownLength ? null : (long?)bytes.Length);
                Result<IReadOnlyList<CslStyle>> installed =
                    await services.CslStore.ListInstalledStylesAsync().WaitAsync(Deadline);
                installed.IsSuccess.Should().BeTrue();
                if (invalidXml)
                {
                    finished.State.Should().Be(DownloadTaskService.FailedState);
                    finished.ErrorCode.Should().NotBeNullOrWhiteSpace();
                    finished.ErrorMessage.Should().NotBeNullOrWhiteSpace();
                    completedFile.State.Should().Be("failed");
                    completedFile.ErrorMessage.Should().NotBeNullOrWhiteSpace();
                    installed.Value.Should().NotContain(style => style.StyleId == catalog.StyleId);
                    manager.InstalledStyles.Should().NotContain(style => style.StyleId == catalog.StyleId);
                }
                else
                {
                    finished.State.Should().Be(DownloadTaskService.SucceededState);
                    completedFile.State.Should().Be("succeeded");
                    installed.Value.Should().ContainSingle(style => style.StyleId == catalog.StyleId);
                    manager.InstalledStyles.Should().ContainSingle(style => style.StyleId == catalog.StyleId);
                    (await services.CslStore.GetStyleContentAsync(catalog.StyleId).WaitAsync(Deadline))
                        .Value.Should().Contain("Progress Style");
                    collectionThreads.Should().NotBeEmpty();
                }

                propertyThreads.Should().NotBeEmpty().And.OnlyContain(onDispatcher => onDispatcher);
                collectionThreads.Should().OnlyContain(onDispatcher => onDispatcher);
                dialogs.DownloadCount.Should().Be(1);
            }
            finally
            {
                // A failed mid-stream assertion must not deadlock host shutdown.
                body.Release();
                try
                {
                    await installation.WaitAsync(Deadline);
                }
                finally
                {
                    services.DownloadTasks.Changed -= RecordSnapshot;
                }
            }

            return 0;
        }, CancellationToken.None);
    }

    private static Func<HttpClient> StyleClientFactory(DownloadProgressTestStream body, bool unknownLength)
    {
        return () => new HttpClient(new StyleHandler(body, unknownLength));
    }

    private const string StyleXml = """
                                    <?xml version="1.0" encoding="utf-8"?>
                                    <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="en-US">
                                      <info>
                                        <title>Progress Style</title>
                                        <id>https://example.test/styles/progress-style</id>
                                      </info>
                                      <citation>
                                        <layout prefix="(" suffix=")" delimiter="; ">
                                          <names variable="author"><name and="text" delimiter=", " initialize-with=". "/></names>
                                        </layout>
                                      </citation>
                                      <bibliography>
                                        <layout suffix=".">
                                          <group delimiter=" ">
                                            <names variable="author"><name and="text" delimiter=", " initialize-with=". "/></names>
                                            <date variable="issued"><date-part name="year"/></date>
                                            <text variable="title"/>
                                          </group>
                                        </layout>
                                      </bibliography>
                                    </style>
                                    """;

    private sealed class StyleHandler(DownloadProgressTestStream body, bool unknownLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsoluteUri.Should().Be("https://progress.invalid/styles/progress-style.csl");
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentLength = unknownLength ? null : body.ByteCount;
            return Task.FromResult(response);
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<AgentProcessResult> RunAsync(AgentProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AgentProcessResult.NotStarted("No external processes in download tests."));
        }
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public TaskCompletionSource<DownloadOperationDialogViewModel> DownloadShown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DownloadCount { get; private set; }

        public Task ShowDialogAsync(object viewModel)
        {
            if (viewModel is DownloadOperationDialogViewModel download)
            {
                DownloadCount++;
                DownloadShown.TrySetResult(download);
            }

            // Deliberately no window: the runner must await its actual worker, not this task.
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult(viewModel is ConfirmDialogViewModel &&
                                   typeof(TResult) == typeof(ConfirmDialogResult)
                ? (TResult?)(object)ConfirmDialogResult.Confirm
                : default);
        }
    }
}
