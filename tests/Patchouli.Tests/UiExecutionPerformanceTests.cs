using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Update;
using Patchouli.Ocr;
using Patchouli.Performance;
using Patchouli.Testing;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>Explicit real-data probes, separate from normal correctness/endurance acceptance.</summary>
[Collection("RetainedTabUI")]
[Trait("Category", "performance")]
public sealed class UiExecutionPerformanceTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Reader_queue_progress_and_import_browsing_use_the_shared_UI_budget()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        CancellationToken token = timeout.Token;
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await new UpdateCheckSettingsService(settings.Path, new SystemClock())
                .SaveSettingsAsync(new UpdateCheckSettings(false, null), timeout.Token);
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                ocrComposition: new HostOcrCompositionOptions(new PacedMockEngine()))
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("ui-execution-performance")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            string root = Path.GetDirectoryName(settings.Path)!;
            List<DocumentInstanceId> documents = await Task.Run(async () =>
            {
                List<DocumentInstanceId> ids = [];
                for (int index = 0; index < 12; index++)
                {
                    ids.Add(await ImportAsync(services, root, index, timeout.Token));
                }

                for (int index = 0; index < 150; index++)
                {
                    (await services.Items.CreateItemAsync("book", $"Metadata fixture {index}",
                            cancellationToken: timeout.Token))
                        .IsSuccess.Should().BeTrue();
                }

                return ids;
            }, timeout.Token);

            MainWindow window = new(main);
            window.Show();
            try
            {
                string reportRoot = Path.Combine(RepositoryTestEnvironment.TemporaryRoot, "performance",
                    "ui-execution-" + Guid.NewGuid().ToString("N"));
                PerformanceReport baseline = PerformanceReport.FromJson(await File.ReadAllTextAsync(
                    TestPaths.FromRepositoryRoot(".agents", "perf", "baseline.full.json"), timeout.Token))!;

                IOcrQueueScheduler queue = (await services.GetOcrQueueAsync(timeout.Token)).Value;
                OcrPreset preset = (await Task.Run(() => services.OcrPresets.CreatePresetAsync(
                    "Performance mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", true,
                    timeout.Token))).Value;
                OcrPresetVersion version = (await Task.Run(() =>
                    services.OcrPresets.GetCurrentVersionAsync(preset.PresetId, timeout.Token))).Value;
                StrongBox<int> progressEvents = new();
                EventHandler<OcrQueueChangedEventArgs> progress = (_, args) =>
                {
                    if (args.ChangeKind == OcrQueueChangeKind.Progress)
                    {
                        Interlocked.Increment(ref progressEvents.Value);
                    }
                };
                queue.Changed += progress;
                try
                {
                    await main.OpenOcrQueueCommand.ExecuteAsync();
                    main.OcrQueue.StartObserving();
                    // AC3 measures live work, not the separate cold-window/template budget.
                    await Task.Run(async () =>
                    {
                        IReadOnlyList<Page> pages = (await services.Pages.ListPagesAsync(documents[0], timeout.Token))
                            .Value;
                        (await queue.EnqueueAsync(new OcrQueueTaskRequest(documents[0], preset.PresetId,
                                    pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Pages,
                                    OcrEngineIds.Mock, OcrAdapterKind.Mock, null, OcrQueuePriority.BatchCollection,
                                    PresetVersionId: version.PresetVersionId, Source: OcrQueueSource.UiPages),
                                timeout.Token))
                            .IsSuccess.Should().BeTrue();
                        await queue.WaitForIdleAsync(timeout.Token);
                        queue.ClearFinishedTasks();
                    }, timeout.Token);
                    await main.OcrQueue.RefreshAsync();
                    window.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    Interlocked.Exchange(ref progressEvents.Value, 0);
                    await MeasureAsync("queue-progress", async () =>
                    {
                        Task work = Task.Run(async () =>
                        {
                            foreach (DocumentInstanceId id in documents)
                            {
                                IReadOnlyList<Page> pages = (await services.Pages.ListPagesAsync(id, token))
                                    .Value;
                                (await queue.EnqueueAsync(new OcrQueueTaskRequest(id, preset.PresetId,
                                            pages.Select(page => page.PageId).ToArray(), OcrQueueTaskKind.Pages,
                                            OcrEngineIds.Mock, OcrAdapterKind.Mock, null,
                                            OcrQueuePriority.BatchCollection,
                                            PresetVersionId: version.PresetVersionId, Source: OcrQueueSource.UiPages),
                                        token))
                                    .IsSuccess.Should().BeTrue();
                            }

                            await queue.WaitForIdleAsync(token);
                        }, timeout.Token);
                        while (!work.IsCompleted)
                        {
                            await main.OcrQueue.RefreshAsync();
                            await Task.Delay(100, timeout.Token);
                        }

                        await work;
                        await Task.Delay(250, timeout.Token);
                    }, main, services, baseline, reportRoot, timeout.Token);
                    Volatile.Read(ref progressEvents.Value).Should()
                        .BeGreaterThan(0, "real queue execution must emit progress during the measurement");
                    IReadOnlyList<OcrQueueTask> tasks =
                        (await Task.Run(() => queue.ListTasksAsync(new OcrQueueTaskFilter(), timeout.Token))).Value;
                    tasks.Should().HaveCount(documents.Count).And
                        .OnlyContain(task => task.State == OcrQueueTaskState.Succeeded);
                }
                finally
                {
                    queue.Changed -= progress;
                }

                await Task.Run(async () =>
                {
                    IReadOnlyList<Page> pages = (await services.Pages.ListPagesAsync(documents[0], timeout.Token))
                        .Value;
                    for (int history = 0; history < 3; history++)
                    {
                        foreach (Page page in pages)
                        {
                            DocumentBoxSeed[] boxes = Enumerable.Range(0, 200).Select(index => new DocumentBoxSeed(
                                    null, null, index, DocumentBoxType.Text, null,
                                    new NormalizedBBox(.04 + index % 8 * .115, .04 + index / 8 * .036, .1, .03),
                                    new TextBoxPayload(
                                        $"Paragraph {index}, history {history}. A representative reading fixture.")))
                                .ToArray();
                            DocumentTreeRevision working = (await services.DocumentTrees.BeginWorkingRevisionAsync(
                                    documents[0], page.PageId, boxes, DocumentTreeRevisionSource.Import,
                                    cancellationToken: timeout.Token))
                                .Value;
                            (await services.DocumentTrees.CommitWorkingRevisionAsync(working.TreeRevisionId,
                                cancellationToken: timeout.Token)).IsSuccess.Should().BeTrue();
                        }
                    }
                }, timeout.Token);

                LibraryItemViewModel item = (await main.Shell.ResolveDocumentItemAsync(documents[0].ToString()))!;
                await main.ShowReadingAsync(item);
                PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)main.ActiveTab!.Content!;
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await MeasureAsync("reader-navigation", async () =>
                {
                    for (int index = 0; index < 12; index++)
                    {
                        await workspace.GoToPageAsync(index % 3 + 1);
                    }

                    await Task.Delay(250, timeout.Token);
                }, main, services, baseline, reportRoot, timeout.Token);

                await main.ShowLibraryCommand.ExecuteAsync();
                await MeasureAsync("import-browsing", async () =>
                {
                    Task import = Task.Run(async () =>
                    {
                        for (int index = 12; index < 36; index++)
                        {
                            await ImportAsync(services, root, index, token);
                        }
                    }, timeout.Token);
                    while (!import.IsCompleted)
                    {
                        await main.Shell.RefreshItemsAsync();
                        await Task.Delay(50, timeout.Token);
                    }

                    await import;
                    await main.Shell.RefreshItemsAsync();
                    await Task.Delay(250, timeout.Token);
                }, main, services, baseline, reportRoot, timeout.Token);
            }
            finally
            {
                await main.ShutdownAsync();
                window.Close();
            }

            return true;
        }, timeout.Token);
    }

    private static async Task<DocumentInstanceId> ImportAsync(HostServices services, string root, int index,
        CancellationToken token)
    {
        string path = Path.Combine(root, $"source-{index}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, path);
        await File.AppendAllTextAsync(path, $"\n% isolated fixture {index}\n", token);
        PdfImportResult result =
            await services.PdfImport.ImportPdfAsync(new PdfImportRequest(path, $"Document {index}", null, 3), token);
        result.Success.Should().BeTrue(result.ErrorMessage);
        return DocumentInstanceId.Parse(result.CreatedDocumentInstanceId!);
    }

    private static async Task MeasureAsync(string scenario, Func<Task> operation, MainWindowViewModel main,
        HostServices services, PerformanceReport baseline, string reportRoot, CancellationToken token)
    {
        long before = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite);
        UiHeartbeatMeasurement heartbeat = await UiHeartbeatProbe.MeasureAsync(operation, token);
        long violations = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite) - before;
        (int items, int pages, int boxes, int units) = await Task.Run(async () =>
        {
            await using SqliteConnection connection = services.ConnectionFactory.CreateReadConnection();
            await connection.OpenAsync(token);
            return (await connection.ExecuteScalarAsync<int>("select count(*) from items;"),
                await connection.ExecuteScalarAsync<int>("select count(*) from pages;"),
                await connection.ExecuteScalarAsync<int>("select count(*) from document_boxes;"),
                await connection.ExecuteScalarAsync<int>("select count(*) from search_units;"));
        }, token);
        PerformanceReport current = new()
        {
            Profile = "ui-execution-" + scenario,
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Environment = new ReportEnvironment(RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), SQLitePCL.raw.sqlite3_libversion().utf8_to_string(),
                "issue46/v1"),
            // Mixed metadata-only/document/history data has no uniform per-item shape.
            Fixture = new ReportFixture(scenario, items, 0, 0, items, pages, boxes, units, 46),
            Cache = new ReportCache(false, null, null, null, false),
            Ui = new ReportUi(true, null, null, null, null, heartbeat.MaximumGapMs,
                UiPerfProbe.HeartbeatTargetMs, heartbeat.Samples, violations, main.Shell.Items.Count)
        };
        RegressionReport regression = RegressionEvaluator.Evaluate(current, baseline, "baseline.full.json",
            new RegressionTolerance(EnforceUiBudgets: true));
        string json = System.Text.Json.JsonSerializer.Serialize(new
            { Scenario = scenario, Report = current, Regression = regression });
        ReportPrivacy.AssertSafe(json);
        Directory.CreateDirectory(reportRoot);
        await File.WriteAllTextAsync(Path.Combine(reportRoot, scenario + ".json"), json, token);
        violations.Should().Be(0);
        heartbeat.Samples.Should().BeGreaterThanOrEqualTo(2);
        regression.Passed.Should().BeTrue(string.Join("; ", regression.Checks.Where(check => check.Result == "fail")));
    }

    private sealed class PacedMockEngine : IOcrEngine
    {
        private readonly MockOcrEngine _inner = new();
        public string EngineId => OcrEngineIds.Mock;

        public async Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            await Task.Delay(80, cancellationToken);
            return await _inner.RunPageAsync(page, presetVersion, cancellationToken);
        }
    }
}
