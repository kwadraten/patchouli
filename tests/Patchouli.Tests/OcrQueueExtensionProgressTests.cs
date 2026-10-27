using Avalonia.Headless;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Ids;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
[Trait("Category", "ui")]
[Trait("Category", "compiled-extension")]
public sealed class OcrQueueExtensionProgressTests
{
    [Fact]
    public async Task Real_compiled_job_updates_the_task_queue_while_recognition_is_still_running()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using MainWindowViewModel main = new(new Clipboard(), settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("ocr-live-extension-progress")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IOcrQueueScheduler scheduler = (await services.GetOcrQueueAsync()).Value;
            await scheduler.PauseAsync(OcrPauseScope.Global);
            DocumentInstance document = (await services.Documents.AttachDocumentInstanceAsync(
                (await services.Items.CreateItemAsync("book", "Live OCR progress")).Value.ItemId,
                null, DocumentInstanceType.PrimaryScan)).Value;
            List<PageId> pages = [];
            for (int index = 0; index < 2; index++)
            {
                Page page = (await services.Pages.CreatePageAsync(document.DocumentInstanceId, index,
                    $"{index + 1}", 100, 100, 0, CoordinateBasis.NormalizedPage, 100, 100, "test", null)).Value;
                pages.Add(page.PageId);
                await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                    document.DocumentInstanceId, page.PageId, "baseline");
            }

            const string engineId = "progress-probe";
            const string packageId = "ocr.progress-probe";
            services.OcrEngineRuntime.RegisterEngine(engineId, packageId,
                new OcrEngineDefinition(engineId, "Progress probe", OcrAdapterKind.LocalLibrary,
                    "probe", [OcrInputKinds.PageImage]));
            string source = $$"""
                              open System
                              open System.Threading
                              open System.Threading.Tasks
                              open Patchouli.Extensions.Contracts
                              open Patchouli.Extensions.Sdk

                              let run (_settings: unit) (job: IOcrJob) (_resources: IExtensionResources) (token: CancellationToken) : Task =
                                  task {
                                      let! first = job.NextPageAsync(token)
                                      match Option.ofObj first with
                                      | None -> failwith "Expected a page."
                                      | Some page ->
                                          let inputs = page.Inputs |> Seq.map (fun input ->
                                              OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized)) |> Seq.toArray
                                          let! _ = job.SubmitCandidatesAsync(OcrExtensionCandidate(page.Page, [||], inputs), token)
                                          let! _ = job.NextPageAsync(token)
                                          do! Task.Delay(Timeout.Infinite, token)
                                  } :> Task

                              /// <summary>Waits mid-recognition without reporting any extension-authored progress.</summary>
                              /// <patchouli-extension id="{{packageId}}" kind="ocr" version="1.0.0" host-api="{{ExtensionHostApi.Current}}">
                              ///   <name>Progress probe</name>
                              /// </patchouli-extension>
                              let extension = Extension.createOcr<unit> run
                              """;
            ExtensionCheckResult check = await services.TranslationExtensions.ActivateAsync(
                new ExtensionPackageSnapshot(packageId, new Dictionary<string, string> { ["main.fsx"] = source }));
            check.Succeeded.Should()
                .BeTrue(string.Join("; ", check.Diagnostics.Select(diagnostic => diagnostic.Message)));
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Progress probe", null,
                engineId, "probe", null, "{}", false)).Value;
            OcrPresetVersion version = (await services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;
            OcrQueueTask task = (await scheduler.EnqueueAsync(new OcrQueueTaskRequest(document.DocumentInstanceId,
                preset.PresetId, pages, OcrQueueTaskKind.Pages, engineId, OcrAdapterKind.LocalLibrary,
                null, OcrQueuePriority.UserStartedDocument, PresetVersionId: version.PresetVersionId,
                Source: OcrQueueSource.Test))).Value;
            try
            {
                main.OcrQueue.StartObserving();
                await scheduler.StartAsync();
                await scheduler.ResumeAsync(OcrPauseScope.Global);
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
                while (scheduler.GetTaskProgress(task.TaskId)?.Detail != "pages:1/2;page:2;inputs:0/1" ||
                       main.OcrQueue.ActiveTaskRows.All(row => !row.StageText.Contains("当前第 2 页")))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await main.OcrQueue.RefreshAsync();
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20, deadline.Token);
                }

                OcrQueueTask running = (await scheduler.GetTaskAsync(task.TaskId)).Value;
                running.State.Should().Be(OcrQueueTaskState.Running);
                running.RunId.Should().NotBeNull();
                (await services.Ocr.ListPageResultsAsync(running.RunId!.Value)).Value
                    .Should().OnlyContain(result => result.State == OcrPageResultState.Processing,
                        "recognition is still in flight and no candidates have been imported yet");
                OcrQueueTaskViewModel row = main.OcrQueue.ActiveTaskRows.Should().ContainSingle().Subject;
                row.StageText.Should().Be("逐页识别 · 1/2 页 · 当前第 2 页 · 输入 0/1");
                row.ProgressValue.Should().Be(42.5);
                row.ProgressPercentText.Should().Be($"{42.5:F0}%");
            }
            finally
            {
                await scheduler.CancelTaskAsync(task.TaskId);
                await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            return 0;
        }, CancellationToken.None);
    }

    private sealed class Clipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }
}
