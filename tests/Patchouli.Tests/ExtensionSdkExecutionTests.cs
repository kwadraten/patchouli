using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Threading;
using System.Collections.Concurrent;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;

namespace Patchouli.Tests;

public sealed class ExtensionSdkExecutionTests
{
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
                                          member _.Descriptor = ExtensionDescriptor("test.sdk", "SDK test", "1.0", "")
                                          member _.ConfigFields = [||]
                                          member _.TranslateAsync(context, capabilities, cancellationToken) = task {
                                              let mode = context.Config.Values["mode"]
                                              if mode = "cancel" then
                                                  let pipeline = Step.async (fun (value: int) token -> task {
                                                      do! Task.Delay(Timeout.Infinite, token)
                                                      return value
                                                  })
                                                  let! value = Pipeline.run context.Progress "sdk-pipeline" pipeline 1 cancellationToken
                                                  return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string value) |])
                                              elif mode = "fail" then
                                                  let pipeline: Pipeline<int, int> = Step.sync (fun _ -> raise (InvalidOperationException("step failed")))
                                                  let! value = Pipeline.run context.Progress "sdk-pipeline" pipeline 1 cancellationToken
                                                  return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string value) |])
                                              elif mode = "parallel" then
                                                  let send _ token = task {
                                                      let! response = capabilities.Http.SendAsync(HttpRequest("GET", Uri("https://unit.test"), Dictionary<string, string>()), token)
                                                      if response.StatusCode <> 200 then invalidOp "The test HTTP request failed."
                                                      return 1
                                                  }
                                                  let nested _ token = Parallel.map 8 send [ 1; 2; 3; 4 ] token
                                                  let! _ = Parallel.map 8 nested [ 1; 2; 3; 4 ] cancellationToken
                                                  return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id, ExtensionBoxOutcomeKind.Skipped) |])
                                              elif mode = "budget" then
                                                  let concurrency = ExecutionScope.current().MaxConcurrency
                                                  return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string concurrency) |])
                                              else
                                                  let mutable executions = 0
                                                  let first = Step.sync (fun (value: int) -> executions <- executions + 1; value + executions)
                                                  let second = Step.sync (fun (value: int) -> value * 2)
                                                  let pipeline = first |>> second
                                                  let! value = Pipeline.run context.Progress "sdk-pipeline" pipeline 0 cancellationToken
                                                  return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, string value) |])
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
                                                  member _.Descriptor = ExtensionDescriptor("test.sdk", "SDK advanced test", "1.0", "")
                                                  member _.ConfigFields = [||]
                                                  member _.TranslateAsync(context, _, cancellationToken) =
                                                      let pipeline: Pipeline<unit, string> =
                                                          Step.async (fun () token -> runAdvancedCore token)
                                                      task {
                                                          let! summary = Pipeline.run context.Progress "advanced-page" pipeline () cancellationToken
                                                          return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id,
                                                              ExtensionBoxOutcomeKind.Translated, summary) |])
                                                      }
                                              }
                                          """;

    [Fact]
    public async Task Pipeline_operator_builds_cold_steps_and_reports_completion()
    {
        RecordingProgress progress = new();
        TranslationExtensionContext context = Context("cold", progress, 4);

        TranslationExtensionResult result = await RunAsync(context, new FakeCapabilities());

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("2");
        progress.Items.Select(item => item.Status).Should().ContainInOrder(
            ExtensionProgressStatus.Started, ExtensionProgressStatus.Completed);
    }

    [Fact]
    public async Task Throwing_progress_observer_does_not_change_pipeline_results()
    {
        TranslationExtensionContext context = Context("cold", new ThrowingProgress(), 4);

        TranslationExtensionResult result = await RunAsync(context, new FakeCapabilities());

        result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("2");
    }

    [Fact]
    public async Task Pipeline_errors_and_cancellation_publish_terminal_progress()
    {
        RecordingProgress failedProgress = new();
        Func<Task> fail = () => RunAsync(Context("fail", failedProgress, 4), new FakeCapabilities());
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
        Task cancel = RunAsync(Context("cancel", cancelledProgress, 4), new FakeCapabilities(),
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
    public async Task Pinned_run_uses_its_frozen_config_and_host_fingerprint_over_caller_values()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-sdk-pinned-{Guid.NewGuid():N}");
        try
        {
            FakeCapabilities capabilities = new();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new FsiExtensionGenerationLoader(runtimeDirectory));
            const string script = """
                                  open System.Threading.Tasks
                                  open Patchouli.Extensions.Contracts

                                  let extension : ITranslationExtension =
                                      { new ITranslationExtension with
                                          member _.Descriptor = ExtensionDescriptor("test.pinned", "Pinned config test", "1.0", "")
                                          member _.ConfigFields = [||]
                                          member _.TranslateAsync(context, _, _) = task {
                                              let mode = context.Config.Values["mode"]
                                              let fingerprint = context.Config.Values["__host.extension-fingerprint"]
                                              let callerValue = context.Config.Values.ContainsKey("caller-only")
                                              let text = sprintf "%s|%s|%b" mode fingerprint callerValue
                                              return TranslationExtensionResult([| ExtensionBoxOutcome(context.Boxes[0].Id, ExtensionBoxOutcomeKind.Translated, text) |])
                                          }
                                      }
                                  """;
            ExtensionPackageSnapshot package = new("test.pinned", new Dictionary<string, string>
            {
                ["main.fsx"] = script
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
            TranslationExtensionContext callerContext = Context("caller", null, 4) with
            {
                Config = new ExtensionConfigSnapshot(new Dictionary<string, string>
                {
                    ["mode"] = "caller-overridden",
                    ["__host.extension-fingerprint"] = "caller-spoof",
                    ["caller-only"] = "present"
                }, 99)
            };

            TranslationExtensionResult result = await run.TranslateAsync(callerContext);

            result.Outcomes.Should().ContainSingle().Which.TranslatedText
                .Should().Be($"frozen|{package.Fingerprint}|false");

            await run.DisposeAsync();
            // This assertion intentionally calls the lease after disposal to verify its public lifetime guard.
            // ReSharper disable once AccessToDisposedClosure
            Func<Task> translateAfterDispose = () => run.TranslateAsync(callerContext);
            await translateAfterDispose.Should().ThrowAsync<ObjectDisposedException>();
            await run.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Nested_pipeline_retry_parallel_and_batch_work_report_parent_ids_and_preserve_order()
    {
        RecordingProgress progress = new();
        TranslationExtensionContext context = Context("advanced", progress, 4);

        TranslationExtensionResult result = await RunAsync(context, new FakeCapabilities(), script: AdvancedScript);

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
    public async Task Collectible_fsi_sessions_can_be_replaced_and_reacquired_repeatedly()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-fsi-reload-{Guid.NewGuid():N}");
        try
        {
            FakeCapabilities capabilities = new();
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new FsiExtensionGenerationLoader(runtimeDirectory));
            TranslationExtensionRunner runner = new(manager, capabilities);

            for (int generation = 0; generation < 4; generation++)
            {
                ExtensionPackageSnapshot package = new("test.sdk", new Dictionary<string, string>
                {
                    ["main.fsx"] = Script + $"\n// generation {generation}"
                });
                ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package);
                activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                    activated.Diagnostics.Select(issue =>
                        $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));

                await manager.SaveConfigAsync(package.Id, Context("cold", null, 4).Config,
                    CancellationToken.None);
                await using TranslationExtensionRun run = (await runner.AcquireAsync(package.Id))!;
                TranslationExtensionResult result = await run.TranslateAsync(Context("cold", null, 4));
                result.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("2");
            }
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Nested_parallel_work_shares_the_host_request_concurrency_limit()
    {
        ConcurrencyTrackingHandler handler = new();
        using HttpClient client = new(handler);
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            Translation = new TranslationAppSettings(Concurrency: 1, RequestTimeoutSeconds: 10)
        };
        HostExtensionCapabilities capabilities = new(() => settings, new EmptyCredentialStore(), client);
        TranslationExtensionContext context = Context("parallel", null, 1);

        await RunAsync(context, capabilities);

        handler.RequestCount.Should().Be(16);
        handler.MaximumConcurrentRequests.Should().BeGreaterThan(1);
        handler.MaximumConcurrentRequests.Should().Be(2,
            "the pinned extension config sets concurrency to two even though host defaults and caller options are one");
        handler.MinimumObservedIntervalMilliseconds.Should().BeGreaterThan(15,
            "the shared host policy paces requests even when nested SDK parallel maps call concurrently");
    }

    [Fact]
    public void Nested_host_run_context_reuses_the_frozen_run_capabilities()
    {
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            Translation = new TranslationAppSettings(Concurrency: 2, RequestTimeoutSeconds: 10)
        };
        using HttpClient client = new();
        HostExtensionCapabilities host = new(() => settings, new EmptyCredentialStore(), client);
        ExtensionConfigSnapshot initial = new(new Dictionary<string, string>(), 0);

        IExtensionCapabilities pinned = host.ForRun(initial);
        IExtensionCapabilities nested = pinned.ForRun(new ExtensionConfigSnapshot(
            new Dictionary<string, string> { ["translation.concurrency"] = "99" }, 99));

        nested.Should().BeSameAs(pinned);
    }

    [Fact]
    public async Task Frozen_config_concurrency_overrides_legacy_options_and_invalid_values_are_rejected()
    {
        LlmAppSettings settings = LlmAppSettings.Default() with
        {
            Translation = new TranslationAppSettings(Concurrency: 1, RequestTimeoutSeconds: 10)
        };
        using HttpClient client = new();
        HostExtensionCapabilities capabilities = new(() => settings, new EmptyCredentialStore(), client);
        TranslationExtensionContext validContext = Context("budget", null, 2) with
        {
            Config = new ExtensionConfigSnapshot(new Dictionary<string, string>
            {
                ["mode"] = "budget",
                ["concurrency"] = "8"
            }, 1)
        };

        TranslationExtensionResult validResult = await RunAsync(validContext, capabilities);

        validResult.Outcomes.Should().ContainSingle().Which.TranslatedText.Should().Be("8");

        foreach (string invalidConcurrency in new[] { "not-a-number", "0", "33" })
        {
            TranslationExtensionContext invalidContext = validContext with
            {
                Config = new ExtensionConfigSnapshot(new Dictionary<string, string>
                {
                    ["mode"] = "budget",
                    ["concurrency"] = invalidConcurrency
                }, 2)
            };
            Func<Task> invalidRun = async () => await RunAsync(invalidContext, capabilities);
            await invalidRun.Should().ThrowAsync<ArgumentException>(invalidConcurrency);
        }
    }

    private static async Task<TranslationExtensionResult> RunAsync(TranslationExtensionContext context,
        IExtensionCapabilities capabilities, CancellationToken cancellationToken = default, string script = Script)
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-sdk-extension-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory, capabilities,
                new FileExtensionStore(runtimeDirectory), new FsiExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package = new("test.sdk", new Dictionary<string, string>
            {
                ["main.fsx"] = script
            });
            ExtensionCheckResult activated = await manager.ActivateAsync(package.Id, package, cancellationToken);
            activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                activated.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
            await manager.SaveConfigAsync(package.Id, context.Config, cancellationToken);
            TranslationExtensionRunner runner = new(manager, capabilities);
            await using TranslationExtensionRun run = (await runner.AcquireAsync(package.Id,
                cancellationToken: cancellationToken))!;
            return await run.TranslateAsync(context, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    private static TranslationExtensionContext Context(string mode, IProgress<ExtensionProgress>? progress,
        int concurrency)
    {
        return new TranslationExtensionContext(new ExtensionPageIdentity(
                DocumentInstanceId.Parse("00000000-0000-0000-0000-000000000001"),
                PageId.Parse("00000000-0000-0000-0000-000000000002"),
                DocumentTreeRevisionId.Parse("00000000-0000-0000-0000-000000000003"), 1), "auto", "en",
            [new ExtensionBox(DocumentBoxId.New(), null, "text", null, "text", "{}")],
            new ExtensionConfigSnapshot(mode == "parallel"
                ? new Dictionary<string, string>
                    { ["mode"] = mode, ["concurrency"] = "2", ["request-interval"] = "0.03" }
                : new Dictionary<string, string> { ["mode"] = mode }, 1), progress, null,
            new TranslationExtensionOptions(8, 12000, null, concurrency, TimeSpan.Zero, TimeSpan.FromSeconds(10),
                0, 0, false));
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

    private sealed class FakeCapabilities : IExtensionCapabilities
    {
        public ILlmCapability Llm { get; } = new FakeLlmCapability();
        public IHttpCapability Http { get; } = new FakeHttpCapability();

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        private sealed class FakeLlmCapability : ILlmCapability
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LlmResponse(""));
            }
        }

        private sealed class FakeHttpCapability : IHttpCapability
        {
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new HttpResponse(200, new Dictionary<string, string>(), "{}"));
            }
        }
    }

    private sealed class ConcurrencyTrackingHandler : HttpMessageHandler
    {
        private int _active;
        private int _maximum;
        private int _count;
        private readonly ConcurrentQueue<long> _startedAt = new();

        public int MaximumConcurrentRequests => Volatile.Read(ref _maximum);
        public int RequestCount => Volatile.Read(ref _count);

        public double MinimumObservedIntervalMilliseconds
        {
            get
            {
                long[] timestamps = _startedAt.ToArray();
                return timestamps.Zip(timestamps.Skip(1), (first, second) =>
                        TimeSpan.FromSeconds((double)(second - first) / Stopwatch.Frequency).TotalMilliseconds)
                    .DefaultIfEmpty(0).Min();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _startedAt.Enqueue(Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _count);
            int active = Interlocked.Increment(ref _active);
            int maximum;
            do
            {
                maximum = Volatile.Read(ref _maximum);
            } while (active > maximum && Interlocked.CompareExchange(ref _maximum, active, maximum) != maximum);

            try
            {
                await Task.Delay(25, cancellationToken);
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

    private sealed class EmptyCredentialStore : ICredentialStore
    {
        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure("not_found", "No credential."));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
