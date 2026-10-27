using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>Real FSX generations pinned by the default queue, not a fake lease or adapter.</summary>
[Trait("Category", "compiled-extension")]
public sealed class OcrDogfoodGenerationTests
{
    [Fact]
    public async Task Queued_multi_page_job_keeps_old_compiled_generation_and_new_task_uses_new_generation()
    {
        const string engineId = "dogfood-generation";
        const string packageId = "ocr.dogfood-generation";
        string root = Path.Combine(Path.GetTempPath(), $"ocr-dogfood-generation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        HostServices? services = null;
        try
        {
            string database = Path.Combine(root, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            services = await HostServices.CreateAsync(database, defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = database,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs"),
                    FileSearchRoot = Path.Combine(root, "search")
                }
            }, Path.Combine(root, "settings.json"), appPaths: new TestAppPaths(root));
            (await services.Library.CreateLibraryAsync("Compiled queue generations")).IsSuccess.Should().BeTrue();
            Result<ItemMetadata> item = await services.Items.CreateItemAsync("book", "Generation fixture");
            Result<DocumentInstance> document = await services.Documents.AttachDocumentInstanceAsync(item.Value.ItemId,
                null,
                DocumentInstanceType.PrimaryScan, makePrimary: true);
            DocumentInstanceId documentId = document.Value.DocumentInstanceId;
            List<PageId> pageIds = [];
            for (int index = 0; index < 2; index++)
            {
                Result<Page> page = await services.Pages.CreatePageAsync(documentId, index, $"{index + 1}",
                    100, 100, 0, CoordinateBasis.NormalizedPage, 100, 100, "generated", null);
                page.IsSuccess.Should().BeTrue(page.ErrorMessage);
                pageIds.Add(page.Value.PageId);
                await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                    documentId, page.Value.PageId, "baseline");
            }

            services.OcrEngineRuntime.RegisterEngine(engineId, packageId,
                new OcrEngineDefinition(engineId, "Generation probe", OcrAdapterKind.LocalLibrary,
                    "probe-model", [OcrInputKinds.PageImage]));
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Generation pin", null,
                engineId, "probe-model", null, "{}", false)).Value;
            OcrPresetVersion version = (await services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;
            IOcrQueueScheduler queue = (await services.GetOcrQueueAsync()).Value;
            (await queue.PauseAsync(OcrPauseScope.Global)).IsSuccess.Should().BeTrue();

            ExtensionPackageSnapshot oldPackage = Package(packageId, "old");
            ExtensionCheckResult oldCheck = await services.TranslationExtensions.ActivateAsync(oldPackage);
            oldCheck.Succeeded.Should().BeTrue(string.Join("; ", oldCheck.Diagnostics.Select(d => d.Message)));
            Result<OcrQueueTask> oldTask = await queue.EnqueueAsync(new OcrQueueTaskRequest(documentId,
                preset.PresetId, pageIds, OcrQueueTaskKind.Pages, engineId, OcrAdapterKind.LocalLibrary,
                null, OcrQueuePriority.UserStartedDocument, PresetVersionId: version.PresetVersionId,
                Source: OcrQueueSource.Test));
            oldTask.IsSuccess.Should().BeTrue(oldTask.ErrorMessage);

            ExtensionPackageSnapshot newPackage = Package(packageId, "new");
            ExtensionCheckResult newCheck = await services.TranslationExtensions.ActivateAsync(newPackage);
            newCheck.Succeeded.Should().BeTrue(string.Join("; ", newCheck.Diagnostics.Select(d => d.Message)));
            Result<OcrQueueTask> newTask = await queue.EnqueueAsync(new OcrQueueTaskRequest(documentId,
                preset.PresetId, pageIds, OcrQueueTaskKind.Pages, engineId, OcrAdapterKind.LocalLibrary,
                null, OcrQueuePriority.UserStartedDocument, PresetVersionId: version.PresetVersionId,
                Source: OcrQueueSource.Test));
            newTask.IsSuccess.Should().BeTrue(newTask.ErrorMessage);
            (await queue.GetTaskAsync(oldTask.Value.TaskId)).Value.State.Should().NotBe(OcrQueueTaskState.Running);

            await queue.StartAsync();
            (await queue.ResumeAsync(OcrPauseScope.Global)).IsSuccess.Should().BeTrue();
            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
            await queue.WaitForIdleAsync(deadline.Token);
            OcrQueueTask oldFinished = (await queue.GetTaskAsync(oldTask.Value.TaskId)).Value;
            OcrQueueTask newFinished = (await queue.GetTaskAsync(newTask.Value.TaskId)).Value;
            oldFinished.State.Should().Be(OcrQueueTaskState.Succeeded, oldFinished.LastErrorMessage);
            newFinished.State.Should().Be(OcrQueueTaskState.Succeeded, newFinished.LastErrorMessage);
            oldFinished.RunId.Should().NotBeNull();
            newFinished.RunId.Should().NotBeNull().And.NotBe(oldFinished.RunId);
            foreach ((OcrQueueTask task, string marker, string fingerprint) in new[]
                     {
                         (oldFinished, "old:1", oldPackage.Fingerprint), (newFinished, "new:1", newPackage.Fingerprint)
                     })
            {
                OcrRun run = (await services.Ocr.GetRunAsync(task.RunId!.Value)).Value;
                run.ParametersSnapshotJson.Should().Contain(fingerprint);
                IReadOnlyList<OcrPageResult> results = (await services.Ocr.ListPageResultsAsync(run.OcrRunId)).Value;
                results.Should().HaveCount(2).And.OnlyContain(result => result.State == OcrPageResultState.Succeeded);
                foreach (OcrPageResult pageResult in results)
                {
                    Result<IReadOnlyList<DocumentBox>> boxes =
                        await services.DocumentTrees.ListBoxesAsync(pageResult.WorkingTreeRevisionId!.Value);
                    boxes.Value.Select(box => box.Payload).OfType<TextBoxPayload>()
                        .Should().ContainSingle(payload => payload.Markdown == marker,
                            "both pages came from one invocation of the same compiled generation");
                }
            }
        }
        finally
        {
            if (services is not null)
            {
                await services.ShutdownAsync();
            }

            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task Public_retry_adopts_a_completed_compiled_run_without_reacquiring_its_missing_engine()
    {
        const string engineId = "dogfood-generation";
        const string packageId = "ocr.dogfood-retry";
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            $"ocr-dogfood-retry-{Guid.NewGuid():N}")).FullName;
        HostServices? services = null;
        try
        {
            string database = Path.Combine(root, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            services = await HostServices.CreateAsync(database, defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = database, DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"), LogDirectory = Path.Combine(root, "logs"),
                    FileSearchRoot = Path.Combine(root, "search")
                }
            }, Path.Combine(root, "settings.json"), appPaths: new TestAppPaths(root));
            (await services.Library.CreateLibraryAsync("Completed run retry")).IsSuccess.Should().BeTrue();
            ItemMetadata item = (await services.Items.CreateItemAsync("book", "Retained compiled result")).Value;
            DocumentInstance document = (await services.Documents.AttachDocumentInstanceAsync(item.ItemId,
                null, DocumentInstanceType.PrimaryScan, makePrimary: true)).Value;
            Page page = (await services.Pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", 100, 100,
                0, CoordinateBasis.NormalizedPage, 100, 100, "generated", null)).Value;
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                document.DocumentInstanceId, page.PageId, "baseline");
            services.OcrEngineRuntime.RegisterEngine(engineId, packageId,
                new OcrEngineDefinition(engineId, "Retry probe", OcrAdapterKind.LocalLibrary,
                    "probe-model", [OcrInputKinds.PageImage]));
            ExtensionPackageSnapshot package = Package(packageId, "retained");
            ExtensionCheckResult check = await services.TranslationExtensions.ActivateAsync(package);
            check.Succeeded.Should()
                .BeTrue(string.Join("; ", check.Diagnostics.Select(diagnostic => diagnostic.Message)));
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Retained generation", null,
                engineId, "probe-model", null, "{}", false)).Value;
            OcrPresetVersion version = (await services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;
            IOcrQueueScheduler queue = (await services.GetOcrQueueAsync()).Value;
            PageEditSession draft = (await services.DocumentTrees.BeginPageEditAsync(document.DocumentInstanceId,
                page.PageId)).Value;
            Result<OcrQueueTask> accepted = await queue.EnqueueAsync(new OcrQueueTaskRequest(
                document.DocumentInstanceId, preset.PresetId, [page.PageId], OcrQueueTaskKind.Pages,
                engineId, OcrAdapterKind.LocalLibrary, null, OcrQueuePriority.UserStartedDocument,
                PresetVersionId: version.PresetVersionId, Source: OcrQueueSource.Test));
            accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            OcrQueueTask failed = (await queue.GetTaskAsync(accepted.Value.TaskId)).Value;
            failed.State.Should().BeOneOf(OcrQueueTaskState.Failed, OcrQueueTaskState.Blocked);
            OcrRun recognized = (await services.Ocr.GetRunAsync(failed.RunId!.Value)).Value;
            recognized.State.Should().Be(OcrRunState.Completed);
            failed.CompletedPageCount.Should().Be(1);

            services.OcrEngineRuntime.RegisterEngine(engineId, "ocr.no-longer-installed");
            (await services.DocumentTrees.DiscardPageEditAsync(draft.SessionId)).IsSuccess.Should().BeTrue();
            (await services.OcrPresets.CreatePresetVersionAsync(preset.PresetId, engineId, "new-model", null,
                "{}", false)).IsSuccess.Should().BeTrue();

            Result<McpOcrCommandResult> retry = await services.OcrCommands.RetryAsync(failed.TaskId);

            retry.IsSuccess.Should().BeTrue(retry.ErrorMessage);
            retry.Value.PresetVersionId.Should().Be(version.PresetVersionId.ToString());
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            OcrQueueTask adopted = (await queue.GetTaskAsync(OcrQueueTaskId.Parse(retry.Value.TaskId))).Value;
            adopted.State.Should().Be(OcrQueueTaskState.Succeeded, adopted.LastErrorMessage);
            adopted.RunId.Should().Be(recognized.OcrRunId);
            (await services.Ocr.GetRunAsync(recognized.OcrRunId)).Value.ParametersSnapshotJson
                .Should().Be(recognized.ParametersSnapshotJson).And.Contain(package.Fingerprint);
            DocumentTreeRevision current = (await services.DocumentTrees.GetCurrentRevisionAsync(
                document.DocumentInstanceId, page.PageId)).Value;
            (await services.DocumentTrees.ListBoxesAsync(current.TreeRevisionId)).Value.Select(box => box.Payload)
                .OfType<TextBoxPayload>().Should().ContainSingle(payload => payload.Markdown == "retained:1");
        }
        finally
        {
            if (services is not null)
            {
                await services.ShutdownAsync();
            }

            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    private static ExtensionPackageSnapshot Package(string id, string marker)
    {
        string source = $$"""
                          open System
                          open System.Threading
                          open System.Threading.Tasks
                          open Patchouli.Core.Documents
                          open Patchouli.Extensions.Contracts
                          open Patchouli.Extensions.Sdk

                          let mutable private invocationCount = 0
                          let private run (_settings: unit) (job: IOcrJob) (_resources: IExtensionResources)
                                          (token: CancellationToken) : Task =
                              task {
                                  invocationCount <- invocationCount + 1
                                  let text = "{{marker}}:" + string invocationCount
                                  let mutable more = true
                                  while more do
                                      let! page = job.NextPageAsync(token)
                                      match Option.ofObj page with
                                      | None -> more <- false
                                      | Some page ->
                                          let boxes = page.Inputs |> Seq.map (fun input ->
                                              OcrExtensionBoxCandidate(input.Id, input.Region,
                                                  LocalOcrCandidate("text", TextBoxPayload(text), Nullable<int>()),
                                                  Nullable<double>())) |> Seq.toArray
                                          let outcomes = page.Inputs |> Seq.map (fun input ->
                                              OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized)) |> Seq.toArray
                                          let! receipt = job.SubmitCandidatesAsync(
                                              OcrExtensionCandidate(page.Page, boxes, outcomes), token)
                                          if receipt.State <> OcrSubmissionState.Accepted then
                                              failwith "Host rejected generated candidate."
                              } :> Task

                          /// <summary>Deterministic task-level generation probe.</summary>
                          /// <patchouli-extension id="{{id}}" kind="ocr" version="1.0.0" host-api="{{ExtensionHostApi.Current}}">
                          ///   <name>Generation probe</name>
                          /// </patchouli-extension>
                          let extension = Extension.createOcr<unit> run
                          """;
        return new ExtensionPackageSnapshot(id, new Dictionary<string, string> { ["main.fsx"] = source });
    }
}
