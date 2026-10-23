using System.Net;
using System.Net.Http;
using System.Threading;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.FSharp.Core;
using Patchouli.Core.Ids;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Extensions.Sdk;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

public sealed class ExtensionSdkExecutionTests
{
    /// <summary>
    /// The shared pipeline, retry, parallel and batch surface over one page. The script pulls its page, runs
    /// the SDK work and commits exactly one outcome, which is the shape every real translation extension has.
    /// </summary>
    private const string Script = """
                                  open System
                                  open System.Collections.Generic
                                  open System.Net.Http
                                  open System.Threading
                                  open System.Threading.Tasks
                                  open Patchouli.Extensions.Contracts
                                  open Patchouli.Extensions.Sdk


                                  let extension : ITranslationExtension =
                                      { new ITranslationExtension with

                                          member _.ConfigFields = [||]
                                          member _.RunAsync(job: ITranslationJob, resources, cancellationToken) = task {
                                              let mutable running = true
                                              while running do
                                                  let! page = job.NextPageAsync(cancellationToken)
                                                  match Option.ofObj page with
                                                  | None -> running <- false
                                                  | Some page ->
                                                      let mode = job.Info.Config.Values["mode"]
                                                      let commission (outcomes: ExtensionBoxOutcome array) = task {
                                                          let! _ = job.CommitPageAsync(TranslationPageResult(page.Page, outcomes), cancellationToken)
                                                          ()
                                                      }
                                                      if mode = "cancel" then
                                                          let pipeline = Step.async (fun (value: int) (token: CancellationToken) -> task {
                                                              do! Task.Delay(Timeout.Infinite, token)
                                                              return value
                                                          })
                                                          let! value = Pipeline.run (Job.progress job) "sdk-pipeline" pipeline 1 cancellationToken
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string value) |]
                                                      elif mode = "fail" then
                                                          let pipeline: Pipeline<int, int> = Step.sync (fun _ -> raise (InvalidOperationException("step failed")))
                                                          let! value = Pipeline.run (Job.progress job) "sdk-pipeline" pipeline 1 cancellationToken
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string value) |]
                                                      elif mode = "parallel" then
                                                          let send _ (token: CancellationToken) = task {
                                                              use! response = resources.Http.GetAsync("https://unit.test", token)
                                                              if response.StatusCode <> System.Net.HttpStatusCode.OK then invalidOp "The test HTTP request failed."
                                                              return 1
                                                          }
                                                          let nested _ (token: CancellationToken) = Parallel.map 8 send [ 1; 2; 3; 4 ] token
                                                          let! _ = Parallel.map 8 nested [ 1; 2; 3; 4 ] cancellationToken
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Skipped) |]
                                                      elif mode = "budget" then
                                                          let concurrency = (match ExecutionScope.current() with | Some scope -> scope.MaxConcurrency | None -> 0)
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string concurrency) |]
                                                      elif mode = "inherit" then
                                                          do! ExecutionScope.run 2 (Job.progress job) cancellationToken (fun () -> task {
                                                              let! _ = Pipeline.run None "inherited" (Step.sync (fun (value: int) -> value + 1)) 1 cancellationToken
                                                              ()
                                                          })
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, "done") |]
                                                      elif mode = "no-scope" then
                                                          let! _ = Pipeline.run None "no-scope" (Step.sync (fun (value: int) -> value + 1)) 1 cancellationToken
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, "done") |]
                                                      else
                                                          let mutable executions = 0
                                                          let first = Step.sync (fun (value: int) -> executions <- executions + 1; value + executions)
                                                          let second = Step.sync (fun (value: int) -> value * 2)
                                                          let pipeline = first |>> second
                                                          let! value = Pipeline.run (Job.progress job) "sdk-pipeline" pipeline 0 cancellationToken
                                                          do! commission [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string value) |]
                                          }
                                      }
                                  """;

    private const string AdvancedScript = """
                                          open System
                                          open System.Net.Http
                                          open System.Threading
                                          open System.Threading.Tasks
                                          open Patchouli.Extensions.Contracts
                                          open Patchouli.Extensions.Sdk

                                          let runAdvancedCore (cancellationToken: CancellationToken) : Task<string> = task {
                                              let policy = Retry.withPolicy 2 TimeSpan.Zero TimeSpan.Zero
                                              let mutable attempts = 0
                                              let retryWork (_: CancellationToken) : Task<int> = task {
                                                  attempts <- attempts + 1
                                                  if attempts < 3 then
                                                      return! Task.FromException<int>(HttpRequestException("retry"))
                                                  else
                                                      return attempts
                                              }
                                              let! retried = Retry.run policy retryWork cancellationToken
                                              let parallelWork (value: int) (token: CancellationToken) : Task<int> = task {
                                                  do! Task.Delay((4 - value) * 20, token)
                                                  return value * 2
                                              }
                                              let! parallelResults = Parallel.map 2 parallelWork [ 1; 2; 3 ] cancellationToken
                                              let budget: BatchBudget = { MaxItems = 2; MaxCharacters = 3; MaxTokens = Some 3 }
                                              let batches =
                                                  Batch.packWithBudget budget
                                                      (fun (value: string) -> value.Length, Some value.Length)
                                                      [ "a"; "bb"; "c"; "dd" ]
                                              let batchWork (batch: string array) (_: CancellationToken) : Task<Result<string, exn> array> = task {
                                                  return batch |> Array.map (fun value ->
                                                      if value = "bb" then Error (InvalidOperationException("partial"))
                                                      else Ok (value.ToUpperInvariant()))
                                              }
                                              let! mapped = Batch.map 2 batchWork batches cancellationToken
                                              let successful, failed = Batch.collectPartial mapped
                                              let collectThrows =
                                                  try
                                                      Batch.collect mapped |> ignore
                                                      false
                                                  with :? AggregateException -> true
                                              return sprintf "retry=%d;parallel=%A;batches=%d;success=%d;failed=%d;collect=%b"
                                                          retried parallelResults batches.Length successful.Length failed.Length collectThrows
                                          }


                                          let extension : ITranslationExtension =
                                              { new ITranslationExtension with

                                                  member _.ConfigFields = [||]
                                                  member _.RunAsync(job: ITranslationJob, _, cancellationToken) = task {
                                                      let pipeline: Pipeline<unit, string> =
                                                           Step.async (fun () (token: CancellationToken) -> runAdvancedCore token)
                                                      let! summary = Pipeline.run (Job.progress job) "advanced-page" pipeline () cancellationToken
                                                      let! page = job.NextPageAsync(cancellationToken)
                                                      match Option.ofObj page with
                                                      | Some page ->
                                                          let! _ = job.CommitPageAsync(TranslationPageResult(page.Page,
                                                              [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, summary) |]), cancellationToken)
                                                          ()
                                                      | None -> ()
                                                      ()
                                                  }
                                              }
                                          """;

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Pipeline_operator_builds_cold_steps_and_reports_completion()
    {
        RecordingProgress progress = new();
        RunContext context = Context("cold", progress, 4);

        TranslationPageResult result = await RunAsync(context, TestExtensionResources.Unavailable());

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("2");
        progress.Items.Where(item => item.Phase == "sdk-pipeline").Select(item => item.Status).Should()
            .ContainInOrder(ExtensionProgressStatus.Started, ExtensionProgressStatus.Completed);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Pipeline_run_with_null_observer_inherits_the_ambient_scope_observer()
    {
        RecordingProgress progress = new();
        RunContext context = Context("inherit", progress, 4);

        TranslationPageResult result = await RunAsync(context, TestExtensionResources.Unavailable());

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("done");
        progress.Items.Where(item => item.Phase == "inherited").Select(item => item.Status).Should()
            .ContainInOrder(ExtensionProgressStatus.Started, ExtensionProgressStatus.Completed);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Pipeline_run_with_null_observer_and_no_active_scope_completes_without_progress()
    {
        RunContext context = Context("no-scope", null, 4);

        TranslationPageResult result = await RunAsync(context, TestExtensionResources.Unavailable());

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("done");
    }

    [Fact]
    public void Retryable_error_code_classification_accepts_options()
    {
        Job.isRetryableErrorCode(FSharpOption<string>.Some("invalid_argument")).Should().BeFalse();
        Job.isRetryableErrorCode(FSharpOption<string>.Some("database_error")).Should().BeFalse();
        Job.isRetryableErrorCode(FSharpOption<string>.Some("rate_limited")).Should().BeTrue();
        Job.isRetryableErrorCode(FSharpOption<string>.Some("")).Should().BeTrue();
        Job.isRetryableErrorCode(FSharpOption<string>.None).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Throwing_progress_observer_does_not_change_pipeline_results()
    {
        RunContext context = Context("cold", new ThrowingProgress(), 4);

        TranslationPageResult result = await RunAsync(context, TestExtensionResources.Unavailable());

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("2");
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Pipeline_errors_and_cancellation_publish_terminal_progress()
    {
        RecordingProgress failedProgress = new();
        Func<Task> fail = () => RunAsync(Context("fail", failedProgress, 4), TestExtensionResources.Unavailable());
        await fail.Should().ThrowAsync<InvalidOperationException>().WithMessage("step failed");
        failedProgress.Items.Should().Contain(item => item.Status == ExtensionProgressStatus.Failed);

        using CancellationTokenSource cancellation = new();
        CancellationToken cancellationToken = cancellation.Token;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingProgress cancelledProgress = new(value =>
        {
            if (value.Status == ExtensionProgressStatus.Started)
            {
                started.TrySetResult();
            }
        });
        Task cancel = RunAsync(Context("cancel", cancelledProgress, 4), TestExtensionResources.Unavailable(),
            cancellationToken);
        await Task.WhenAny(started.Task, cancel);
        if (!started.Task.IsCompleted)
        {
            await cancel;
        }

        cancellation.Cancel();
        Func<Task> cancelCall = () => cancel;
        await cancelCall.Should().ThrowAsync<OperationCanceledException>();
        cancelledProgress.Items.Should().Contain(item => item.Status == ExtensionProgressStatus.Cancelled);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Pinned_run_uses_its_frozen_config_and_host_fingerprint_over_caller_values()
    {
        string runtimeDirectory = TempDirectory("patchouli-sdk-pinned");
        try
        {
            TestExtensionResources capabilities = TestExtensionResources.Unavailable();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            const string script = """
                                  open System.Threading.Tasks
                                  open Patchouli.Extensions.Contracts


                                  let extension : ITranslationExtension =
                                      { new ITranslationExtension with

                                          member _.ConfigFields = [||]
                                          member _.RunAsync(job: ITranslationJob, _, _) = task {
                                              let mode = job.Info.Config.Values["mode"]
                                              let fingerprint = job.Info.Config.Values["__host.extension-fingerprint"]
                                              let callerValue = job.Info.Config.Values.ContainsKey("caller-only")
                                              let text = sprintf "%s|%s|%b" mode fingerprint callerValue
                                              let! page = job.NextPageAsync()
                                              match Option.ofObj page with
                                              | Some page ->
                                                  let! _ = job.CommitPageAsync(TranslationPageResult(page.Page,
                                                      [| ExtensionBoxOutcome(page.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, text) |]))
                                                  ()
                                              | None -> ()
                                              ()
                                          }
                                      }
                                  """;
            ExtensionPackageSnapshot package = new("test.pinned", new Dictionary<string, string>
            {
                ["main.fsx"] = WithEntryMetadata(script, "test.pinned", "Pinned config test",
                    "Pinned config test fixture.")
            });
            ExtensionCheckResult activation = await manager.ActivateAsync(package.Id, package, CancellationToken.None);
            activation.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activation.Diagnostics.Select(issue => issue.Message)));
            await manager.SaveConfigAsync(package.Id, new ExtensionConfigSnapshot(new Dictionary<string, string>
            {
                ["mode"] = "frozen"
            }, 1), CancellationToken.None);
            TranslationExtensionRunner runner = new(manager, capabilities);
            await using TranslationExtensionRun run = (await runner.AcquireAsync(package.Id))!;
            await manager.SaveConfigAsync(package.Id, new ExtensionConfigSnapshot(new Dictionary<string, string>
            {
                ["mode"] = "updated-after-pin"
            }, 2), CancellationToken.None);
            // The caller's own config is replaced by the pinned one; a spoofed host fingerprint never wins.
            RunContext callerContext = Context("caller", null, 4) with
            {
                Config = new ExtensionConfigSnapshot(new Dictionary<string, string>
                {
                    ["mode"] = "caller-overridden",
                    ["__host.extension-fingerprint"] = "caller-spoof",
                    ["caller-only"] = "present"
                }, 99)
            };
            TestTranslationJob job = callerContext.CreateJob();

            await run.RunAsync(job);

            job.Committed.Should().ContainSingle();
            job.Committed[0].Outcomes.Single().TranslatedText.Should().Be($"frozen|{package.Fingerprint}|false");

            await run.DisposeAsync();
            // This assertion intentionally calls the run after disposal to verify its public lifetime guard.
            // ReSharper disable once AccessToDisposedClosure
            Func<Task> translateAfterDispose = () => run.RunAsync(job);
            await translateAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
            await run.DisposeAsync();
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Nested_pipeline_retry_parallel_and_batch_work_report_parent_ids_and_preserve_order()
    {
        RecordingProgress progress = new();
        RunContext context = Context("advanced", progress, 4);

        TranslationPageResult result =
            await RunAsync(context, TestExtensionResources.Unavailable(), script: AdvancedScript);

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should()
            .Be("retry=3;parallel=[|2; 4; 6|];batches=2;success=3;failed=1;collect=true");
        ExtensionProgress[] started = progress.Items
            .Where(item => item.Status == ExtensionProgressStatus.Started).ToArray();
        started.Select(item => item.ExecutionId).Should().OnlyHaveUniqueItems();
        started.Should().Contain(item => item.Phase == "attempt" && item.Attempt == 1);
        started.Should().Contain(item => item.Phase == "attempt" && item.Attempt == 2);
        started.Should().Contain(item => item.Phase == "attempt" && item.Attempt == 3);
        started.Should().Contain(item => item.Phase == "parallel" && item.ParentExecutionId != null);
        started.Should().Contain(item => item.Phase == "item" && item.ParentExecutionId != null);
        started.Should().Contain(item => item.Phase == "attempt" && item.ParentExecutionId != null);
        started.Should().OnlyContain(item => item.Elapsed != null);
        progress.Items.Where(item => item.ExecutionId != null).GroupBy(item => item.ExecutionId)
            .Should().OnlyContain(group => group.Count(item => item.Status == ExtensionProgressStatus.Completed) <= 1,
                "each execution node completes exactly once, even when retry attempts fail");
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Compiled_generations_can_be_replaced_and_reacquired()
    {
        string runtimeDirectory = TempDirectory("patchouli-fsi-reload");
        try
        {
            TestExtensionResources capabilities = TestExtensionResources.Unavailable();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            TranslationExtensionRunner runner = new(manager, capabilities);

            for (int generation = 0; generation < 2; generation++)
            {
                ExtensionPackageSnapshot package = new("test.sdk", new Dictionary<string, string>
                {
                    ["main.fsx"] = WithEntryMetadata(Script + $"\n// generation {generation}",
                        "test.sdk", "SDK test", "SDK execution test fixture.")
                });
                ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package);
                activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                    activated.Diagnostics.Select(issue =>
                        $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));

                RunContext context = Context("cold", null, 4);
                await manager.SaveConfigAsync(package.Id, context.Config, CancellationToken.None);
                await using TranslationExtensionRun run = (await runner.AcquireAsync(package.Id))!;
                TestTranslationJob job = context.CreateJob();
                await run.RunAsync(job);
                job.Committed.Should().ContainSingle().Which.Outcomes.Single().TranslatedText.Should().Be("2");
            }
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Nested_parallel_work_uses_the_standard_httpclient()
    {
        ConcurrencyTrackingHandler handler = new();
        using TestExtensionResources capabilities = new(handler);
        // Raw RunAsync uses the explicit job budget; the host no longer interprets private config keys.
        RunContext context = Context("parallel", null, 2);

        // The run must already be in flight before the handshake can observe overlap; the
        // deadline keeps a regression to concurrency 1 a bounded failure instead of a hang.
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        Task<TranslationPageResult> run = RunAsync(context, capabilities, deadline.Token);
        try
        {
            Task secondEntered = handler.SecondRequestEntered;
            Task settled;
            try
            {
                // A run that settles before the handshake is awaited first so extension
                // failures surface instead of the deadline.
                settled = await Task.WhenAny(secondEntered, run).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException exception) when (!secondEntered.IsCompleted)
            {
                throw new InvalidOperationException(
                    "The second extension HTTP request never entered while the first was still blocked.", exception);
            }

            if (settled == run && !secondEntered.IsCompleted)
            {
                await run;
            }

            secondEntered.IsCompleted.Should().BeTrue(
                "a second direct HttpClient request must enter while the first is still blocked");
            handler.ReleaseRequests();
            await run.WaitAsync(deadline.Token);
        }
        finally
        {
            // Releasing is idempotent and unblocks held requests on the failure path; cancelling
            // and draining keeps the owned compiled-extension run and its directory cleanup inside the test.
            handler.ReleaseRequests();
            deadline.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception drained)
            {
                _ = drained;
            }
        }

        handler.RequestCount.Should().Be(16);
        handler.MaximumConcurrentRequests.Should().BeGreaterThan(1);
    }

    private static async Task<TranslationPageResult> RunAsync(RunContext context,
        IExtensionResources resources, CancellationToken cancellationToken = default, string script = Script)
    {
        string runtimeDirectory = TempDirectory("patchouli-sdk-extension");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, resources,
                new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = new("test.sdk", new Dictionary<string, string>
            {
                ["main.fsx"] = WithEntryMetadata(script, "test.sdk", "SDK test", "SDK execution test fixture.")
            });
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package, cancellationToken);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            await manager.SaveConfigAsync(package.Id, context.Config, cancellationToken);
            TranslationExtensionRunner runner = new(manager, resources);
            await using TranslationExtensionRun run = (await runner.AcquireAsync(package.Id,
                cancellationToken: cancellationToken))!;
            TestTranslationJob job = context.CreateJob();
            await run.RunAsync(job, cancellationToken);
            return job.Committed.Should().ContainSingle("the script commits its single page once").Subject;
        }
        finally
        {
            DeleteDirectory(runtimeDirectory);
        }
    }

    private static RunContext Context(string mode, IProgress<ExtensionProgress>? progress, int concurrency)
    {
        return new RunContext(
            new ExtensionConfigSnapshot(mode == "parallel"
                ? new Dictionary<string, string>
                    { ["mode"] = mode, ["concurrency"] = "2", ["request-interval"] = "0.03" }
                : new Dictionary<string, string> { ["mode"] = mode }, 1),
            new TranslationExtensionOptions(8, 12000, null, concurrency, TimeSpan.Zero, TimeSpan.FromSeconds(10),
                0, 0, false),
            progress);
    }

    private static string WithEntryMetadata(string source, string id, string name, string summary)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int[] exports = lines.Select((line, index) => (line, index))
            .Where(item => item.line == "let extension : ITranslationExtension =")
            .Select(item => item.index).ToArray();
        if (exports.Length != 1)
        {
            throw new InvalidOperationException(
                "SDK execution fixture must contain exactly one column-zero let extension export.");
        }

        string[] documentation =
        [
            $"/// <summary>{summary}</summary>",
            $"/// <patchouli-extension id=\"{id}\" kind=\"translation\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\">",
            $"/// <name>{name}</name>",
            "/// </patchouli-extension>"
        ];
        return string.Join('\n', lines.Take(exports[0]).Concat(documentation).Concat(lines.Skip(exports[0])));
    }

    private static string TempDirectory(string prefix)
    {
        return Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", prefix), Guid.NewGuid().ToString("N"));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    /// <summary>One single-page SDK run: the frozen config, the run options and the progress observer.</summary>
    private sealed record RunContext(
        ExtensionConfigSnapshot Config,
        TranslationExtensionOptions Options,
        IProgress<ExtensionProgress>? Progress)
    {
        public TestTranslationJob CreateJob()
        {
            TranslationPageInput page = new(new ExtensionPageIdentity(
                    DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                    PageId.Parse("00000000-0000-0000-0000-000000000002"),
                    DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 1),
                [new ExtensionBox(DocumentBoxId.New(), null, "text", null, "text", "{}")]);
            TestTranslationJob job = new(TestTranslationJob.CreateInfo(Options, Config, "auto", "en"), [page])
            {
                Progress = Progress
            };
            return job;
        }
    }

    private sealed class RecordingProgress(Action<ExtensionProgress>? onReport = null) : IProgress<ExtensionProgress>
    {
        public ConcurrentQueue<ExtensionProgress> Items { get; } = new();

        public void Report(ExtensionProgress value)
        {
            Items.Enqueue(value);
            onReport?.Invoke(value);
        }
    }

    private sealed class ThrowingProgress : IProgress<ExtensionProgress>
    {
        public void Report(ExtensionProgress value)
        {
            throw new InvalidOperationException("Observer failures are isolated from extension work.");
        }
    }

    private sealed class ConcurrencyTrackingHandler : HttpMessageHandler
    {
        private int _active;
        private int _maximum;
        private int _count;

        private readonly TaskCompletionSource _secondEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrentRequests => Volatile.Read(ref _maximum);
        public int RequestCount => Volatile.Read(ref _count);

        /// <summary>Completes once two requests are inside the handler at the same time.</summary>
        public Task SecondRequestEntered => _secondEntered.Task;

        /// <summary>Lets every request held in SendAsync finish; safe to call more than once.</summary>
        public void ReleaseRequests()
        {
            _release.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            int active = Interlocked.Increment(ref _active);
            int maximum;
            do
            {
                maximum = Volatile.Read(ref _maximum);
            } while (active > maximum && Interlocked.CompareExchange(ref _maximum, active, maximum) != maximum);

            if (active >= 2)
            {
                _secondEntered.TrySetResult();
            }

            try
            {
                // Held requests wait on the test-controlled release only; there is no
                // handler-side slot limit: the active count records direct HttpClient fan-out.
                await _release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
