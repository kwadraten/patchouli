using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Core.Translation;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Extensions.Worker;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

public sealed class ExtensionWorkerProcessTests
{
    private const string ExtensionId = "test.worker";

    private const string Script = """
                                  open System
                                  open System.Threading.Tasks
                                  open Patchouli.Core.Documents
                                  open Patchouli.Extensions.Contracts
                                  open Patchouli.Extensions.Sdk

                                  let first = bind "first-model" (Capability.llm "First model" |> Capability.followDefault Domains.translation)
                                  let second = bind "second-model" (Capability.llm "Second model" |> Capability.followDefault Domains.translation)

                                  let extension : ITranslationExtension =
                                      { new ITranslationExtension with
                                          member _.Descriptor = ExtensionDescriptor("test.worker", "Worker test", "1.0", "")
                                          member _.ConfigFields = [||]
                                          member _.TranslateAsync(context, capabilities, token) = task {
                                              printfn "script stdout must not corrupt RPC"
                                              eprintfn "script stderr must not corrupt RPC"
                                              use _registration = token.Register(fun () ->
                                                  if context.TargetLanguage = "blocked" then
                                                      invalidOp "Cancellation callback failed.")
                                              let pipeline = Step.async (fun (_: int) cancellationToken -> task {
                                                  let primary = Capability.resolveLlm first context.Config capabilities
                                                  let secondary = Capability.resolveLlm second context.Config capabilities
                                                  let! a = primary.CompleteAsync(LlmRequest([| LlmMessage("user", context.TargetLanguage) |]), cancellationToken)
                                                  let! b = secondary.CompleteAsync(LlmRequest([| LlmMessage("user", context.TargetLanguage) |]), cancellationToken)
                                                  return a.Text + "/" + b.Text
                                              })
                                              let! text = Pipeline.run context.Progress "worker-test" pipeline 0 token
                                              return TranslationExtensionResult([| ExtensionBoxOutcome.FromPayload(context.Boxes.[0].Id, TextBoxPayload(text)) |])
                                          } }
                                  """;

    [Fact]
    public async Task Real_worker_preserves_typed_identity_progress_and_separate_model_slots()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using IExtensionLease lease = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        RecordingProgress progress = new();
        TranslationExtensionContext context = Context("fr", progress);
        RecordingCapabilities capabilities = new();

        TranslationExtensionResult result = await lease.Translation.TranslateAsync(context, capabilities);

        result.Outcomes.Should().ContainSingle();
        result.Outcomes[0].BoxId.Should().Be(context.Boxes[0].Id);
        result.Outcomes[0].TranslatedPayload.Should().Be(new TextBoxPayload("model-a:fr/model-b:fr"));
        capabilities.ResolvedModels.Should().BeEquivalentTo("model-a", "model-b");
        progress.Items.Should().Contain(item => item.Status == ExtensionProgressStatus.Completed);
        fixture.Loader.GetWorkers().Should().ContainSingle(worker => !worker.HasExited);
        fixture.Loader.GetWorkers().Single(worker => !worker.HasExited).ProcessId.Should()
            .NotBe(Environment.ProcessId);
    }

    [Fact]
    public async Task Real_worker_candidate_is_atomically_committed_and_survives_worker_restart()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using TranslationTestContext document = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        await document.CommitAsync(new DocumentBoxSeed(boxId, null, 0, DocumentBoxType.Text, null,
            new NormalizedBBox(0.1, 0.1, 0.5, 0.1), new TextBoxPayload("source paragraph")));
        await fixture.Manager.SaveConfigAsync(ExtensionId, Context("fr").Config);
        TranslationRunEngine engine = new(document.Trees, document.Translations,
            new TranslationExtensionRunner(fixture.Manager, new RecordingCapabilities()));

        TranslationRunResult result = await engine.RunAsync(new TranslationRunRequest(document.DocumentId,
            [new TranslationPageTarget(document.PageId, 1)],
            new TranslationSettings("fr", EngineId: ExtensionId)));

        result.Pages.Should().ContainSingle().Which.State.Should().Be(TranslationPageRunState.Committed);
        WorkerSnapshot worker = fixture.Loader.GetWorkers().Single(item => !item.HasExited);
        await fixture.Loader.RestartAsync(worker.WorkerInstanceId);
        await using SqliteConnection connection = document.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        string translated = await connection.QuerySingleAsync<string>(
            "select translated_md from translation_boxes where page_id = @PageId and box_id = @BoxId",
            new { PageId = document.PageId.ToString(), BoxId = boxId.ToString() });
        translated.Should().Be("model-a:fr/model-b:fr");
        HasExited(worker.ProcessId).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_one_invocation_preserves_the_other_run_in_the_same_worker()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using IExtensionLease lease = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        using CancellationTokenSource cancellation = new();
        RecordingCapabilities blocked = new(true);
        Task<TranslationExtensionResult> first = lease.Translation.TranslateAsync(Context("blocked"), blocked,
            cancellation.Token);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        RecordingCapabilities independent = new(true);
        Task<TranslationExtensionResult> secondRun = lease.Translation.TranslateAsync(Context("independent"),
            independent);
        await independent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        WorkerSnapshot original = fixture.Loader.GetWorkers().Single(worker => !worker.HasExited);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(30)));
        secondRun.IsCompleted.Should().BeFalse("a throwing cancellation callback must not close the shared transport");
        independent.Release.TrySetResult();
        TranslationExtensionResult second = await secondRun.WaitAsync(TimeSpan.FromSeconds(30));
        second.Outcomes[0].GetTranslatedText().Should().Be("model-a:independent/model-b:independent");
        await lease.Translation.TranslateAsync(Context("after-cancellation"), new RecordingCapabilities());
        fixture.Loader.GetWorkers().Single(worker => !worker.HasExited).WorkerInstanceId.Should()
            .Be(original.WorkerInstanceId);
        blocked.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Manager_shutdown_reaps_workers_even_when_a_caller_still_holds_a_lease()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using IExtensionLease heldLease = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        RecordingCapabilities blocked = new(true);
        Task<TranslationExtensionResult> invocation = heldLease.Translation.TranslateAsync(Context("closing"), blocked);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        WorkerSnapshot worker = fixture.Loader.GetWorkers().Single(item => !item.HasExited);

        await fixture.Manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        HasExited(worker.ProcessId).Should().BeTrue();
        await AssertTerminatesWithFailureAsync(invocation);
        await AssertTerminatesWithFailureAsync(heldLease.Translation.TranslateAsync(Context("after-close"),
            new RecordingCapabilities()));
        fixture.Loader.GetWorkers().Should().OnlyContain(item => item.HasExited);
    }

    [Fact]
    public async Task Forced_restart_fails_all_inflight_calls_and_next_call_uses_a_new_instance_without_replay()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using IExtensionLease lease = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        RecordingCapabilities firstCapabilities = new(true);
        RecordingCapabilities secondCapabilities = new(true);
        Task<TranslationExtensionResult> first = lease.Translation.TranslateAsync(Context("one"), firstCapabilities);
        Task<TranslationExtensionResult> second = lease.Translation.TranslateAsync(Context("two"), secondCapabilities);
        await Task.WhenAll(firstCapabilities.Entered.Task, secondCapabilities.Entered.Task)
            .WaitAsync(TimeSpan.FromSeconds(30));
        WorkerSnapshot before = fixture.Loader.GetWorkers().Single(worker => !worker.HasExited);

        await fixture.Loader.RestartAsync(before.WorkerInstanceId);

        await AssertTerminatesWithFailureAsync(first);
        await AssertTerminatesWithFailureAsync(second);
        HasExited(before.ProcessId).Should().BeTrue("restart must confirm actual process exit");
        firstCapabilities.Calls.Should().Be(1);
        secondCapabilities.Calls.Should().Be(1);
        TranslationExtensionResult recovered = await lease.Translation.TranslateAsync(Context("new-call"),
            new RecordingCapabilities()).WaitAsync(TimeSpan.FromSeconds(30));
        recovered.Outcomes[0].GetTranslatedText().Should().Be("model-a:new-call/model-b:new-call");
        WorkerSnapshot after = fixture.Loader.GetWorkers().Single(worker => !worker.HasExited);
        after.WorkerInstanceId.Should().NotBe(before.WorkerInstanceId);
        after.Generation.Should().Be(before.Generation);
    }

    [Fact]
    public async Task Leased_draining_generation_applies_backpressure_without_changing_active()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IExtensionLease first = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        try
        {
            ExtensionPackageSnapshot secondPackage = Package("second");
            ExtensionCheckResult secondActivation = await fixture.Manager.ActivateAsync(ExtensionId, secondPackage);
            secondActivation.Succeeded.Should().BeTrue(Diagnostics(secondActivation));
            await using IExtensionLease second = (await fixture.Manager.AcquireAsync(ExtensionId))!;

            ExtensionCheckResult rejected = await fixture.Manager.ActivateAsync(ExtensionId, Package("third"));

            rejected.Succeeded.Should().BeFalse();
            await using IExtensionLease active = (await fixture.Manager.AcquireAsync(ExtensionId))!;
            active.Fingerprint.Should().Be(secondPackage.Fingerprint);
            fixture.Loader.GetWorkers().Count(worker => !worker.HasExited).Should().BeLessThanOrEqualTo(2);
        }
        finally
        {
            await first.DisposeAsync();
        }

        ExtensionCheckResult afterDrain = await fixture.Manager.ActivateAsync(ExtensionId, Package("after-drain"));
        afterDrain.Succeeded.Should().BeTrue(Diagnostics(afterDrain));
    }

    [Fact]
    public async Task Oversize_capability_reply_fails_promptly_without_breaking_the_worker_transport()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using IExtensionLease lease = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        WorkerSnapshot original = fixture.Loader.GetWorkers().Single(worker => !worker.HasExited);
        RecordingCapabilities oversized = new();

        await AssertTerminatesWithFailureAsync(lease.Translation.TranslateAsync(Context("oversize"), oversized));

        oversized.Calls.Should().Be(1);
        TranslationExtensionResult next = await lease.Translation.TranslateAsync(Context("normal"),
            new RecordingCapabilities()).WaitAsync(TimeSpan.FromSeconds(30));
        next.Outcomes.Single().GetTranslatedText().Should().Be("model-a:normal/model-b:normal");
        fixture.Loader.GetWorkers().Single(worker => !worker.HasExited).WorkerInstanceId.Should()
            .Be(original.WorkerInstanceId);
    }

    private static async Task AssertTerminatesWithFailureAsync(Task operation)
    {
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => operation.WaitAsync(TimeSpan.FromSeconds(30)));
        error.Should().NotBeOfType<TimeoutException>("a hung RPC is a test failure, not an accepted operation error");
    }

    private static string Diagnostics(ExtensionCheckResult result)
    {
        return string.Join(Environment.NewLine,
            result.Diagnostics.Select(issue =>
                $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}: {issue.Message}"));
    }

    [Fact]
    public async Task One_hundred_replacements_confirm_worker_exit_without_restarting_the_host()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fsx-worker-replacements-" + Guid.NewGuid().ToString("N"));
        WorkerGenerationLoader loader = new(directory);
        ExtensionPackageSnapshot package = Package();
        ExtensionCheckResult check = await new ExtensionCompiler(Path.Combine(directory, ".check"))
            .CheckAsync(package, CancellationToken.None);
        check.Succeeded.Should().BeTrue();
        HashSet<string> instances = new(StringComparer.Ordinal);
        int hostProcessId = Environment.ProcessId;
        try
        {
            // Only a trailing comment changes; this stress test exercises worker replacement,
            // not repeated source checking of the same declarations.
            for (int index = 0; index < 100; index++)
            {
                IExtensionGeneration generation = await ((ICheckedExtensionGenerationLoader)loader)
                    .LoadCheckedAsync(Package("replacement-" + index), check, CancellationToken.None);
                WorkerSnapshot worker = loader.GetWorkers().Single(item => !item.HasExited);
                try
                {
                    instances.Add(worker.WorkerInstanceId).Should().BeTrue();
                    TranslationExtensionResult result = await generation.Translation.TranslateAsync(
                        Context("replacement-" + index), new RecordingCapabilities());
                    result.Outcomes[0].GetTranslatedText().Should()
                        .Be($"model-a:replacement-{index}/model-b:replacement-{index}");
                }
                finally
                {
                    await generation.DisposeAsync();
                }

                HasExited(worker.ProcessId).Should().BeTrue($"replacement {index} must release its actual process");
                loader.GetWorkers().Should().OnlyContain(item => item.HasExited);
                Environment.ProcessId.Should().Be(hostProcessId);
            }

            instances.Should().HaveCount(100);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Metadata_success_failure_and_retrieval_revision_identity_cross_real_workers()
    {
        const string metadataSource = """
                                      open System.Collections.Generic
                                      open System.Threading.Tasks
                                      open Patchouli.Core.Bibliography.MetadataLookup
                                      open Patchouli.Core.Results
                                      open Patchouli.Extensions.Contracts
                                      let mutable cleared = false
                                      let extension : IMetadataExtension =
                                          { new IMetadataExtension with
                                              member _.Descriptor = ExtensionDescriptor("test.worker", "Metadata", "1.0", "", ExtensionKind.Metadata)
                                              member _.ConfigFields = [||]
                                              member _.SourceDefinition = MetadataSourceDefinition("test-source", "Source", HashSet<string>([| "doi" |]), 1)
                                              member _.LookupAsync(identifier, _, _, _) =
                                                  if identifier.Value = "missing" then
                                                      Task.FromResult(Result<MetadataCandidate>.Failure("metadata_not_found", "Fixture not found"))
                                                  else
                                                      let title = if cleared then identifier.Value + ":cleared" else identifier.Value
                                                      Task.FromResult(Result<MetadataCandidate>.Success(MetadataCandidate("test-source", Title = title)))
                                            interface IExtensionDerivedState with
                                                member _.ClearDerivedStateAsync(_) =
                                                    cleared <- true
                                                    Task.CompletedTask }
                                      """;
        const string retrievalSource = """
                                       open System.Threading.Tasks
                                       open Patchouli.Extensions.Contracts
                                       let extension : IRetrievalExtension =
                                           { new IRetrievalExtension with
                                               member _.Descriptor = ExtensionDescriptor("test.worker", "Retrieval", "1.0", "", ExtensionKind.Retrieval)
                                               member _.ConfigFields = [||]
                                               member _.RankAsync(context, _, _) =
                                                   Task.FromResult(RetrievalExtensionResult([| RetrievalExtensionMatch(context.Candidates.[0].UnitId, 0.75, context.Candidates.[0].TreeRevisionId.ToString()) |])) }
                                       """;
        await using Fixture fixture = await Fixture.CreateAsync(DomainPackage("metadata", metadataSource));
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>(), 0);
        {
            await using IExtensionLease metadata = (await fixture.Manager.AcquireAsync(ExtensionId))!;
            metadata.Metadata!.SourceDefinition.SupportedSchemes.Should().Contain("doi");
            Result<MetadataCandidate> success = await metadata.Metadata.LookupAsync(
                new NormalizedIdentifier("doi", "fixture title"),
                config, new RecordingCapabilities());
            Result<MetadataCandidate> failure = await metadata.Metadata.LookupAsync(
                new NormalizedIdentifier("doi", "missing"),
                config, new RecordingCapabilities());
            success.IsSuccess.Should().BeTrue();
            success.Value.Title.Should().Be("fixture title");
            failure.IsFailure.Should().BeTrue();
            failure.ErrorCode.Should().Be("metadata_not_found");
            metadata.Definition.Should().BeAssignableTo<IExtensionDerivedState>();
            await ((IExtensionDerivedState)metadata.Definition).ClearDerivedStateAsync();
            Result<MetadataCandidate> cleared = await metadata.Metadata.LookupAsync(
                new NormalizedIdentifier("doi", "fixture title"),
                config, new RecordingCapabilities());
            cleared.Value.Title.Should().Be("fixture title:cleared");
        }

        (await fixture.Manager.ActivateAsync(ExtensionId, DomainPackage("retrieval", retrievalSource)))
            .Succeeded.Should().BeTrue();
        await using IExtensionLease retrieval = (await fixture.Manager.AcquireAsync(ExtensionId))!;
        SearchMatchedUnit unit = new(SearchUnitId.New(), PageId.New(), DocumentBoxId.New(), "source text", "text",
            0, DocumentTreeRevisionId.New(), true);
        RetrievalExtensionResult ranked = await retrieval.Retrieval!.RankAsync(
            new RetrievalExtensionContext("query", config, [unit], 1), new RecordingCapabilities());
        ranked.Matches.Should().ContainSingle().Which.Should()
            .Be(new RetrievalExtensionMatch(unit.UnitId, 0.75, unit.TreeRevisionId.ToString()));
    }

    [Fact]
    public async Task Worker_restart_reaps_the_extension_child_process_tree()
    {
        const string source = """
                              open System
                              open System.Diagnostics
                              open System.Threading.Tasks
                              open Patchouli.Extensions.Contracts
                              let extension : ITranslationExtension =
                                  { new ITranslationExtension with
                                      member _.Descriptor = ExtensionDescriptor("test.worker", "Child lifetime", "1.0", "")
                                      member _.ConfigFields = [||]
                                      member _.TranslateAsync(context, _, _) =
                                          let start =
                                              if OperatingSystem.IsWindows() then
                                                  ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 120")
                                              else ProcessStartInfo("/bin/sleep", "120")
                                          start.UseShellExecute <- false
                                          start.CreateNoWindow <- true
                                          use child =
                                              match Process.Start(start) with
                                              | null -> invalidOp "Child process did not start."
                                              | value -> value
                                          Task.FromResult(TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes.[0].Id, ExtensionBoxOutcomeKind.Translated, string child.Id) |])) }
                              """;
        string directory = Path.Combine(Path.GetTempPath(), "fsx-worker-tree-" + Guid.NewGuid().ToString("N"));
        await using WorkerGenerationLoader loader = new(directory);
        try
        {
            // This low-level containment fixture bypasses EXT0020's process-launch policy only
            // through the checked-loader test seam. Production authoring keeps that policy.
            // The worker still really compiles and executes this controlled F# source.
            await using IExtensionGeneration generation = await ((ICheckedExtensionGenerationLoader)loader)
                .LoadCheckedAsync(DomainPackage("translation", source), new ExtensionCheckResult(true, [], []),
                    CancellationToken.None);
            TranslationExtensionResult result = await generation.Translation.TranslateAsync(Context("child"),
                new RecordingCapabilities());
            int childId = int.Parse(result.Outcomes.Single().GetTranslatedText()!,
                System.Globalization.CultureInfo.InvariantCulture);
            using Process child = Process.GetProcessById(childId);
            try
            {
                WorkerSnapshot worker = loader.GetWorkers().Single(item => !item.HasExited);
                child.HasExited.Should().BeFalse();
                await loader.RestartAsync(worker.WorkerInstanceId);
                HasExited(worker.ProcessId).Should().BeTrue();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                child.HasExited.Should().BeTrue();
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                    await child.WaitForExitAsync();
                }
            }
        }
        finally
        {
            await loader.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static ExtensionPackageSnapshot DomainPackage(string kind, string source)
    {
        return new ExtensionPackageSnapshot(ExtensionId,
            new Dictionary<string, string>
            {
                ["extension.yml"] =
                    $"id: test.worker\nversion: 1.0\nhost_api: patchouli.extensions/1\nkind: {kind}\nentry: main.fsx",
                ["main.fsx"] = source
            });
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static ExtensionPackageSnapshot Package(string marker = "initial")
    {
        return new ExtensionPackageSnapshot(ExtensionId,
            new Dictionary<string, string>
            {
                ["extension.yml"] =
                    "id: test.worker\nversion: 1.0\nhost_api: patchouli.extensions/1\nkind: translation\nentry: main.fsx",
                ["main.fsx"] = Script + "\n// " + marker
            });
    }

    private static TranslationExtensionContext Context(string target, IProgress<ExtensionProgress>? progress = null)
    {
        Dictionary<string, string> values = new()
        {
            ["first-model"] = JsonSerializer.Serialize(new ExtensionModelSelection("test-provider", "model-a")),
            ["second-model"] = JsonSerializer.Serialize(new ExtensionModelSelection("test-provider", "model-b"))
        };
        return new TranslationExtensionContext(
            new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(), 1),
            "en", target,
            [
                new ExtensionBox(DocumentBoxId.New(), null, "text", null, "source", "{}")
                    { Payload = new TextBoxPayload("source") }
            ], new ExtensionConfigSnapshot(values, 1), progress);
    }

    private sealed class Fixture(string directory, WorkerGenerationLoader loader, IExtensionManager manager)
        : IAsyncDisposable
    {
        public WorkerGenerationLoader Loader { get; } = loader;
        public IExtensionManager Manager { get; } = manager;

        public static async Task<Fixture> CreateAsync(ExtensionPackageSnapshot? package = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), "fsx-worker-" + Guid.NewGuid().ToString("N"));
            WorkerGenerationLoader loader = new(directory);
            IExtensionManager manager = new ExtensionManager(directory, new RecordingCapabilities(),
                new FileExtensionStore(directory), loader);
            Fixture fixture = new(directory, loader, manager);
            try
            {
                ExtensionCheckResult result = await manager.ActivateAsync(ExtensionId, package ?? Package());
                result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                    result.Diagnostics.Select(issue => issue.Code + ": " + issue.Message)));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            Loader.GetWorkers().Should().OnlyContain(worker => worker.HasExited);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class RecordingProgress : IProgress<ExtensionProgress>
    {
        public ConcurrentQueue<ExtensionProgress> Items { get; } = new();

        public void Report(ExtensionProgress value)
        {
            Items.Enqueue(value);
        }
    }

    private sealed class RecordingCapabilities(bool block = false) : IExtensionCapabilities, ILlmCapability,
        IHttpCapability
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<string> ResolvedModels { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILlmCapability Llm => this;
        public IHttpCapability Http => this;

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ILlmCapability ResolveLlm(string? domain, ExtensionModelSelection? selection)
        {
            ResolvedModels.Enqueue(selection?.Model ?? "default");
            return new BoundLlm(this, selection?.Model ?? "default");
        }

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (block)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            if (request.Messages[0].Content == "oversize")
            {
                return new LlmResponse(new string('x', 64 * 1024 * 1024 + 1));
            }

            return new LlmResponse(request.Model + ":" + request.Messages[0].Content);
        }

        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This fixture must use only its declared model slots.");
        }

        private sealed class BoundLlm(RecordingCapabilities owner, string model) : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                return owner.CompleteAsync(request with { Model = model }, cancellationToken);
            }
        }
    }
}
