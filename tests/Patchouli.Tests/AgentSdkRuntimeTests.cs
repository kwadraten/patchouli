using System.Text.Json;
using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Agent.Sdk;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.Llm;
using Patchouli.Workflows;

namespace Patchouli.Tests;

public sealed class AgentSdkRuntimeTests
{
    private const string Put = """{"uri":"patchouli://translations/doc/page-1.md","content":"translated"}""";

    [Fact]
    public async Task Worker_settles_unawaited_sdk_tasks_and_rejects_tasks_from_an_ended_evaluation()
    {
        Gateway gateway = new();
        AgentEffectContext context = new("task-lifetime", "", [], []);
        await using AgentFsiRepl worker = new();
        AgentSdkRuntime runtime = new(gateway);
        using AgentSdkRuntime.Scope first = runtime.Activate(context, "effect/1", CancellationToken.None);
        AgentToolOutcome initial = await worker.ExecuteAsync(context.SessionId, JsonSerializer.Serialize(new
        {
            code = """
                   let pending = Sdk.fetch [ReadableResource.SourcePage("doc", 1)]
                   let release = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
                   let later = task {
                       do! release.Task
                       return! Sdk.fetch [ReadableResource.SourcePage("doc", 2)]
                   }
                   """
        }), first.InvokeAsync, CancellationToken.None);
        initial.Succeeded.Should().BeTrue(initial.Payload);
        first.Receipts.Should().ContainSingle();
        using AgentSdkRuntime.Scope next = runtime.Activate(context, "effect/2", CancellationToken.None);
        AgentToolOutcome later = await worker.ExecuteAsync(context.SessionId, JsonSerializer.Serialize(new
        {
            code = "release.SetResult(())\nprintfn \"%s\" (later.GetAwaiter().GetResult().ErrorCode)"
        }), next.InvokeAsync, CancellationToken.None);
        later.Succeeded.Should().BeTrue(later.Payload);
        later.Payload.Should().Contain("SDK_SCOPE_EXPIRED");
        gateway.Calls.Should().ContainSingle();
        next.Receipts.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_worker_records_caught_failures_and_enforces_the_primitive_budget()
    {
        Gateway gateway = new() { Fail = true };
        List<AgentSdkReceipt> observed = [];
        AgentEffectContext context = new("sdk-worker", "", [], [])
        {
            SdkPolicy = new AgentSdkPolicy(new HashSet<string> { "fetch" }, 1),
            RecordSdk = receipt =>
            {
                observed.Add(receipt);
                return Task.CompletedTask;
            }
        };
        using AgentSdkRuntime.Scope scope =
            new AgentSdkRuntime(gateway).Activate(context, "effect/1", CancellationToken.None);
        await using AgentFsiRepl worker = new();
        AgentToolOutcome compile = await worker.ExecuteAsync(context.SessionId, JsonSerializer.Serialize(new
        {
            code = "let result = Sdk.fetch [ReadableResource.SourcePage(\"doc\", 1)]\nlet invalid: int = \"bad\""
        }), scope.InvokeAsync, CancellationToken.None);
        compile.Succeeded.Should().BeFalse();
        gateway.Calls.Should().BeEmpty("the entire block must type-check before any primitive executes");
        AgentToolOutcome caught = await worker.ExecuteAsync(context.SessionId, JsonSerializer.Serialize(new
        {
            code = """
                   try
                       Sdk.fetch [ReadableResource.SourcePage("doc", 1)] |> Async.AwaitTask |> Async.RunSynchronously |> Sdk.requireSuccess |> ignore
                   with :? SdkCallException -> printfn "caught"
                   let exhausted = Sdk.fetch [ReadableResource.SourcePage("doc", 2)] |> Async.AwaitTask |> Async.RunSynchronously
                   printfn "%s" exhausted.ErrorCode
                   """
        }), scope.InvokeAsync, CancellationToken.None);
        caught.Succeeded.Should().BeTrue(caught.Payload);
        caught.Payload.Should().Contain("caught").And.Contain("AGENT_TOOL_BUDGET_EXHAUSTED");
        gateway.Calls.Should().ContainSingle();
        scope.Receipts.Should().Contain(r => r.ErrorCode == "PERMISSION_DENIED");
        observed.Should().Contain(r => r.Status == "Started").And.Contain(r => r.Status == "Failed");
    }

    [Fact]
    public async Task Bound_export_has_the_same_native_and_real_fsi_semantics_and_cannot_override_target()
    {
        using ScriptHostSession script = ScriptHostSession.Create();
        script.EvaluateScript("""
                              open Patchouli.Agent.Sdk
                              let exported = LibraryTools.commitTranslation |> Tool.bind (WritableResource.TranslationPage("bound", 7)) |> Tool.export
                              """, "bound.fsx").Succeeded.Should().BeTrue();
        ExportedTool exported = (ExportedTool)script.EvaluateExpression("exported", "bound.fsx").Value!;
        exported.InputSchema.Should().Contain("content").And.NotContain("uri");
        Gateway gateway = new();
        AgentEffectContext context = new("bound-worker", "", [], [])
        {
            SdkPolicy = new AgentSdkPolicy(new HashSet<string> { "commitTranslation" }, 3, [exported],
                exported.Identity)
        };
        AgentSdkRuntime runtime = new(gateway);
        using AgentSdkRuntime.Scope native = runtime.Activate(context, "effect/1", CancellationToken.None);
        (await native.InvokeAsync("commitTranslation", """{"content":"translated"}""")).Succeeded.Should().BeTrue();
        (await native.InvokeAsync("commitTranslation", """{"content":"translated","uri":"other"}""")).ErrorCode.Should()
            .Be("INVALID_ARGUMENT");
        (await native.InvokeAsync("put", Put)).ErrorCode.Should().Be("AGENT_TOOL_DENIED");
        using AgentSdkRuntime.Scope fsi = runtime.Activate(context, "effect/2", CancellationToken.None);
        await using AgentFsiRepl worker = new();
        AgentToolOutcome result = await worker.ExecuteAsync(context.SessionId, JsonSerializer.Serialize(new
        {
            code =
                "AgentTools.commitTranslation { content = \"translated\" } |> Async.AwaitTask |> Async.RunSynchronously |> printfn \"%s\""
        }), fsi.InvokeAsync, CancellationToken.None, AgentSdkBindings.Source([exported]));
        result.Succeeded.Should().BeTrue(result.Payload);
        gateway.Calls.Should().HaveCount(2);
        gateway.Calls[0].Should().Be(gateway.Calls[1]);
        gateway.Calls[0].Arguments.Should().Contain("bound/page-7.md");
        fsi.Receipts.Single().ParentId.Should().Contain("commitTranslation");
    }

    [Fact]
    public async Task Completed_write_is_reused_and_unknown_write_blocks_other_attempts_to_the_same_target()
    {
        string directory = TemporaryDirectory();
        try
        {
            Gateway gateway = new();
            AgentEffectContext context = new("durable", "", [], []) { SessionDirectory = directory };
            AgentSdkRuntime runtime = new(gateway);
            using (AgentSdkRuntime.Scope first = runtime.Activate(context, "effect/1", CancellationToken.None))
            {
                (await first.InvokeAsync("put", Put)).Succeeded.Should().BeTrue();
            }

            using (AgentSdkRuntime.Scope replay = runtime.Activate(context, "effect/1", CancellationToken.None))
            {
                (await replay.InvokeAsync("put", Put)).Succeeded.Should().BeTrue();
            }

            gateway.Calls.Should().ContainSingle();
            gateway.Interrupt = true;
            using (AgentSdkRuntime.Scope interrupted = runtime.Activate(context, "effect/2", CancellationToken.None))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted.InvokeAsync("put", Put));
            }

            gateway.Interrupt = false;
            using (AgentSdkRuntime.Scope next = runtime.Activate(context, "effect/3", CancellationToken.None))
            {
                (await next.InvokeAsync("put", Put)).ErrorCode.Should().Be("SDK_OPERATION_UNKNOWN");
            }

            gateway.Calls.Should().HaveCount(2, "a new effect ID must not bypass an unresolved write");
            WorkflowEffectHostAdapter adapter = new(new AgentEffectInterpreter(new NoModels(), gateway,
                new PlaceholderAgentHostPrimitives()), _ => "", sessionDirectory: _ => directory);
            WorkflowEffectOutcome observed = await adapter.ExecuteEffectAsync(context.SessionId,
                new WorkflowEffectScope(AgentCoreModule.initial),
                Effect.NewMcpToolCall(EffectId.NewEffectId(2), "put", Put), CancellationToken.None);
            observed.Should().BeOfType<WorkflowEffectOutcome.ObservedEvent>().Which.operations.Single().ErrorCode
                .Should().Be("SDK_OPERATION_UNKNOWN",
                    "an unsettled receipt cannot become a merely blocked page outcome");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Immutable_codec_round_trips_maps_sets_options_tuples_and_du_payloads_and_rejects_nulls()
    {
        using ScriptHostSession script = ScriptHostSession.Create();
        script.EvaluateScript("""
                              open Patchouli.Agent.Sdk
                              type Domain = Items | Translations
                              type Target = Root | Page of string * int
                              type State = { Domain: Domain; Target: Target; Values: Map<string, int>; Labels: Set<string>; Last: (int * string) option }
                              let codec = ValueCodec.create<State>()
                              let state = { Domain = Translations; Target = Page("doc", 7); Values = Map.ofList ["a", 1]; Labels = Set.ofList ["one"; "two"]; Last = Some(2, "last") }
                              let encoded = codec.Encode state
                              let roundtrip = codec.Decode encoded = state
                              let rejectsNull = try ValueCodec.create<string>().Decode "null" |> ignore; false with _ -> true
                              """, "codec.fsx").Succeeded.Should().BeTrue();
        script.EvaluateExpression("roundtrip && rejectsNull", "codec.fsx").Value.Should().Be(true);
    }

    [Fact]
    public async Task Interrupted_fsi_keeps_the_commit_receipt_and_does_not_replay_the_code_block()
    {
        string directory = TemporaryDirectory();
        try
        {
            Gateway gateway = new();
            await using AgentFsiRepl worker = new();
            using CancellationTokenSource cancel = new();
            TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            AgentEffectContext context = new("interrupted-worker", "", [], [])
            {
                SessionDirectory = directory,
                RecordSdk = receipt =>
                {
                    if (receipt.Status == "Succeeded")
                    {
                        committed.TrySetResult();
                    }

                    return Task.CompletedTask;
                }
            };
            AgentEffectInterpreter interpreter =
                new(new NoModels(), gateway, new PlaceholderAgentHostPrimitives(), worker);
            Effect call = Effect.NewMcpToolCall(EffectId.NewEffectId(1), "fsi", JsonSerializer.Serialize(new
            {
                code = """
                       Sdk.put (WritableResource.TranslationPage("doc", 1)) "translated" |> Async.AwaitTask |> Async.RunSynchronously |> Sdk.requireSuccess |> ignore
                       System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite)
                       """
            }));
            Task<AgentEffectOutcome> execution = interpreter.ExecuteAsync(context, call, cancel.Token);
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            AgentEffectOutcome resumed = await interpreter.ExecuteAsync(context, call, CancellationToken.None);
            resumed.FailureCode.Should().Be("FSI_EXECUTION_INTERRUPTED");
            resumed.Receipts.Should().ContainSingle().Which.Status.Should().Be("Succeeded");
            gateway.Calls.Should().ContainSingle();
            resumed.ResultEvent.Should().BeOfType<Event.ToolFailure>().Which.payload.Should().Contain("sdkOperations");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "patchouli-sdk-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class NoModels : IAgentLlmClientProvider
    {
        public Task<Result<ILlmChatClient>> TryGetAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No model request belongs in an SDK test.");
        }
    }

    private sealed class Gateway : IAgentMcpGateway
    {
        public bool Fail { get; init; }
        public bool Interrupt { get; set; }
        public List<(string Name, string Arguments)> Calls { get; } = [];

        public Task<AgentToolOutcome> CallToolAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            Calls.Add((name, arguments));
            if (Interrupt)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(new AgentToolOutcome(!Fail, Fail ? "denied" : "{\"entries\":[]}",
                Fail ? "PERMISSION_DENIED" : null));
        }

        public Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Only the shared SDK tool boundary is allowed.");
        }
    }
}
