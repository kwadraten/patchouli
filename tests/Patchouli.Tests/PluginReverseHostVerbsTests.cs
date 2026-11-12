using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Plugins;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Testing;

namespace Patchouli.Tests;

/// <summary>
/// Reverse plugin interop (ADR 0058): plugins call host verbs over stdio.
/// Dispatcher unit tests plus a two-plugin pipe fixture proving the full loop:
/// read round trips, a mutating put behind an explicit grant, and the refusal
/// cases (no grant, undeclared verb, ungranted verb, denied domain, switched-off
/// tool, own-mount reentrancy) plus the depth budget.
/// </summary>
public sealed class PluginReverseHostVerbsTests
{
    private static readonly string TestRoot =
        Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-reverse-host-verbs");

    private const string PluginA = "test.reverse-a";
    private const string PluginB = "test.reverse-b";
    private static readonly TimeSpan ReverseTimeout = TimeSpan.FromSeconds(10);

    // ---- Dispatcher unit tests (no processes) --------------------------------

    [Fact]
    public async Task Reverse_dispatch_refuses_without_grant_and_never_invokes()
    {
        int invocations = 0;
        PluginReverseHostDispatcher dispatcher = new(PluginA,
            (_, _) => "The device registry does not grant the host verb 'find'.",
            (tool, args, ct) =>
            {
                invocations++;
                return Task.FromResult<object>(ToolResponse("ok"));
            },
            null, () => -1);

        ReverseHostReply reply = await dispatcher.DispatchAsync("patchouli.host.find",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        reply.Handled.Should().BeTrue();
        ToolError(reply).Should().Contain("permission_denied");
        invocations.Should().Be(0);
    }

    [Fact]
    public async Task Reverse_dispatch_refuses_unknown_verbs_and_ignores_other_methods()
    {
        PluginReverseHostDispatcher dispatcher = new(PluginA,
            (_, _) => null,
            (_, _, _) => Task.FromResult<object>(ToolResponse("ok")),
            null, () => -1);

        ReverseHostReply bogus = await dispatcher.DispatchAsync("patchouli.host.execute",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        bogus.Handled.Should().BeTrue();
        ToolError(bogus).Should().Contain("invalid_argument");

        ReverseHostReply other = await dispatcher.DispatchAsync("patchouli.plugin.find",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        other.Handled.Should().BeFalse();
    }

    [Fact]
    public async Task Reverse_dispatch_refuses_own_mount_calls()
    {
        int invocations = 0;
        PluginReverseHostDispatcher dispatcher = new(PluginA,
            (_, _) => null,
            (tool, args, ct) =>
            {
                invocations++;
                return Task.FromResult<object>(ToolResponse("ok"));
            },
            null, () => -1);

        JsonElement ownFetch = JsonSerializer.SerializeToElement(new
        {
            uris = new[] { "patchouli://plugin/" + PluginA + "/status.json" },
            format = "json"
        });
        ReverseHostReply reply = await dispatcher.DispatchAsync("patchouli.host.fetch", ownFetch,
            CancellationToken.None);

        reply.Handled.Should().BeTrue();
        ToolError(reply).Should().Contain("reentrancy_refused");
        invocations.Should().Be(0);
    }

    [Fact]
    public async Task Reverse_dispatch_enforces_tool_switches_and_fails_closed_without_settings()
    {
        McpServerSettings switchedOff =
            McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                ToolOverrides = [new McpToolOverride("patchouli.put", false)]
            };
        int invocations = 0;
        PluginReverseHostDispatcher dispatcher = new(PluginA,
            (_, _) => null,
            (tool, args, ct) =>
            {
                invocations++;
                return Task.FromResult<object>(ToolResponse("ok"));
            },
            _ => Task.FromResult(switchedOff), () => -1);

        ReverseHostReply put = await dispatcher.DispatchAsync("patchouli.host.put",
            JsonSerializer.SerializeToElement(new { uri = "patchouli://items/x.yml", content = "t: 1\n" }),
            CancellationToken.None);
        ToolError(put).Should().Contain("permission_denied");

        ReverseHostReply find = await dispatcher.DispatchAsync("patchouli.host.find",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        find.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse();
        invocations.Should().Be(1);

        PluginReverseHostDispatcher noSettings = new(PluginA,
            (_, _) => null,
            (tool, args, ct) =>
            {
                invocations++;
                return Task.FromResult<object>(ToolResponse("ok"));
            },
            null, () => -1);
        ReverseHostReply putNoSettings = await noSettings.DispatchAsync("patchouli.host.put",
            JsonSerializer.SerializeToElement(new { uri = "patchouli://items/x.yml", content = "t: 1\n" }),
            CancellationToken.None);
        ToolError(putNoSettings).Should().Contain("permission_denied");
        invocations.Should().Be(1);
    }

    [Fact]
    public async Task Reverse_dispatch_enforces_the_depth_budget_and_tags_the_invoker_context()
    {
        int observedDepth = -1;
        int invocations = 0;
        PluginReverseHostDispatcher deep = new(PluginA,
            (_, _) => null,
            (tool, args, ct) =>
            {
                invocations++;
                observedDepth = PluginReverseCallDepth.Current.Value ?? -1;
                return Task.FromResult<object>(ToolResponse("ok"));
            },
            null, () => PluginReverseHostVerbs.MaxDepth);

        ReverseHostReply refused = await deep.DispatchAsync("patchouli.host.find",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        ToolError(refused).Should().Contain("depth_exceeded");
        invocations.Should().Be(0);

        PluginReverseHostDispatcher fresh = new(PluginA,
            (_, _) => null,
            (tool, args, ct) =>
            {
                invocations++;
                observedDepth = PluginReverseCallDepth.Current.Value ?? -1;
                return Task.FromResult<object>(ToolResponse("ok"));
            },
            null, () => -1);
        ReverseHostReply ok = await fresh.DispatchAsync("patchouli.host.find",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        ok.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse();
        observedDepth.Should().Be(0);
        invocations.Should().Be(1);
    }

    [Fact]
    public void Reverse_outbound_calls_track_their_depth_on_the_client()
    {
        PluginStdioClient client = new(new PluginProcessSpec("dotnet", [], "/tmp"));
        client.CurrentReverseDepth.Should().Be(-1);

        using (client.RegisterOutboundReverseCall(2))
        {
            client.CurrentReverseDepth.Should().Be(2);
            using (client.RegisterOutboundReverseCall(5))
            {
                client.CurrentReverseDepth.Should().Be(5);
            }

            client.CurrentReverseDepth.Should().Be(2);
        }

        client.CurrentReverseDepth.Should().Be(-1);
    }

    // ---- Catalog --------------------------------------------------------------

    [Fact]
    public void Catalog_parses_requested_host_verbs()
    {
        Result<PluginCatalogIndex> parsed =
            PluginCatalogReader.Parse(Encoding.UTF8.GetBytes(CatalogIndex));
        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Plugins.Should().HaveCount(1);
        parsed.Value.Plugins[0].RequestedHostVerbs.Should().BeEquivalentTo("find", "put");

        string withoutHost = CatalogIndex.Replace(
            "\"requested_host_verbs\": [\"find\", \"put\"],", string.Empty,
            StringComparison.Ordinal);
        Result<PluginCatalogIndex> bare = PluginCatalogReader.Parse(Encoding.UTF8.GetBytes(withoutHost));
        bare.IsSuccess.Should().BeTrue();
        bare.Value.Plugins[0].RequestedHostVerbs.Should().BeEmpty();

        string badVerb = CatalogIndex.Replace("\"put\"", "\"execute\"", StringComparison.Ordinal);
        PluginCatalogReader.Parse(Encoding.UTF8.GetBytes(badVerb)).IsFailure.Should().BeTrue();
    }

    // ---- Full loop over pipes -------------------------------------------------

    [Fact]
    public async Task Reverse_find_root_round_trip()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["find"], ["find"])]);

        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.find",
            new { @in = "patchouli://", format = "json" }, ReverseTimeout);

        ToolResponseText(response).Should().Contain("patchouli://plugin/");
        ToolIsError(response).Should().BeFalse();
    }

    [Fact]
    public async Task Reverse_fetch_static_descriptor_never_starts_a_second_call()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["fetch"], ["fetch"]), (PluginB, ["fetch"], ["fetch"])]);
        ReverseFakeProcess other = harness.Process(PluginB);
        int callsBefore = other.ToolCalls;

        // The other plugin's descriptor is host-synthesized from its manifest:
        // answered without starting (or calling) any plugin process.
        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.fetch",
            new { uris = new[] { "patchouli://plugin/" + PluginB + "/plugin.yml" }, format = "json" },
            ReverseTimeout);

        ToolIsError(response).Should().BeFalse(ToolResponseText(response));
        ToolResponseText(response).Should().Contain(PluginB);
        other.ToolCalls.Should().Be(callsBefore);
    }

    [Fact]
    public async Task Reverse_fetch_other_plugin_goes_live()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["fetch"], ["fetch"]), (PluginB, ["fetch"], ["fetch"])]);
        ReverseFakeProcess other = harness.Process(PluginB);
        int callsBefore = other.ToolCalls;

        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.fetch",
            new { uris = new[] { "patchouli://plugin/" + PluginB + "/status.json" }, format = "json" },
            ReverseTimeout);

        ToolIsError(response).Should().BeFalse();
        ToolResponseText(response).Should().Contain("ok");
        other.ToolCalls.Should().Be(callsBefore + 1);
        other.ToolNames.Should().Contain("patchouli.plugin.fetch");
    }

    [Fact]
    public async Task Reverse_put_behind_grant_lands()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["put"], ["put"])]);

        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.put",
            new
            {
                uri = "patchouli://items/11111111-2222-3333-4444-555555555555.yml", content = "title: Reverse test\n",
                format = "json"
            },
            ReverseTimeout);

        ToolIsError(response).Should().BeFalse(ToolResponseText(response));
        harness.Writes.Puts.Should().HaveCount(1);
        harness.Writes.Puts[0].Uri.Should().Be("patchouli://items/11111111-2222-3333-4444-555555555555.yml");
        harness.Writes.Puts[0].Content.Should().Be("title: Reverse test\n");
    }

    [Fact]
    public async Task Reverse_call_without_grant_is_refused_and_never_invokes()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, [], [])]);

        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.find",
            new { @in = "patchouli://", format = "json" }, ReverseTimeout);

        ToolIsError(response).Should().BeTrue();
        ToolResponseText(response).Should().Contain("permission_denied");
        harness.InvokerCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reverse_undeclared_and_ungranted_verbs_are_refused_distinctly()
    {
        // Declares find only; the registry grants find + fetch: fetch is undeclared.
        await using ReverseHarness undeclared = await ReverseHarness.CreateAsync(
            [(PluginA, ["find"], ["find", "fetch"])]);
        JsonElement undeclaredResponse = await undeclared.Process(PluginA).SendReverseAsync(
            "patchouli.host.fetch",
            new { uris = new[] { "patchouli://plugin/" + PluginA + "/plugin.yml" }, format = "json" },
            ReverseTimeout);
        ToolIsError(undeclaredResponse).Should().BeTrue();
        ToolResponseText(undeclaredResponse).Should().Contain("does not declare");
        undeclared.InvokerCalls.Should().Be(0);

        // Declares find + fetch; the registry grants find only: fetch is ungranted.
        await using ReverseHarness ungranted = await ReverseHarness.CreateAsync(
            [(PluginA, ["find", "fetch"], ["find"])]);
        JsonElement ungrantedResponse = await ungranted.Process(PluginA).SendReverseAsync(
            "patchouli.host.fetch",
            new { uris = new[] { "patchouli://plugin/" + PluginA + "/plugin.yml" }, format = "json" },
            ReverseTimeout);
        ToolIsError(ungrantedResponse).Should().BeTrue();
        ToolResponseText(ungrantedResponse).Should().Contain("does not grant");
        ungranted.InvokerCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reverse_call_into_denied_domain_is_refused()
    {
        McpServerSettings denied =
            McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                DomainPermissions = [new McpDomainPermission("items", "find", false)]
            };
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["find"], ["find"])],
            _ => Task.FromResult(denied));

        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.find",
            new { @in = "patchouli://items/", format = "json" }, ReverseTimeout);

        ToolIsError(response).Should().BeTrue();
        ToolResponseText(response).Should().Contain("PERMISSION_DENIED");
    }

    [Fact]
    public async Task Reverse_put_refused_when_the_tool_switch_is_off()
    {
        McpServerSettings switchedOff =
            McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                ToolOverrides = [new McpToolOverride("patchouli.put", false)]
            };
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["put"], ["put"])],
            _ => Task.FromResult(switchedOff));

        JsonElement response = await harness.Process(PluginA).SendReverseAsync("patchouli.host.put",
            new
            {
                uri = "patchouli://items/11111111-2222-3333-4444-555555555555.yml", content = "title: Reverse test\n",
                format = "json"
            },
            ReverseTimeout);

        ToolIsError(response).Should().BeTrue();
        ToolResponseText(response).Should().Contain("permission_denied");
        harness.Writes.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Reverse_call_into_own_mount_is_refused_while_answering()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["fetch"], ["fetch"])]);
        ReverseFakeProcess process = harness.Process(PluginA);
        JsonElement? captured = null;
        process.OnToolCall = async (args, context) =>
        {
            captured = await context.SendReverseAndReadResponseAsync("patchouli.host.fetch",
                new { uris = new[] { "patchouli://plugin/" + PluginA + "/status.json" }, format = "json" });
            return DefaultFindPage();
        };

        McpCommandResult<McpFindMeta, object> outer = await harness.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://plugin/" + PluginA + "/results/", null));

        outer.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        ToolIsError(captured!.Value).Should().BeTrue();
        ToolResponseText(captured.Value).Should().Contain("reentrancy_refused");
    }

    [Fact]
    public async Task Reverse_call_to_library_succeeds_while_answering()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["find"], ["find"])]);
        ReverseFakeProcess process = harness.Process(PluginA);
        JsonElement? captured = null;
        process.OnToolCall = async (args, context) =>
        {
            captured = await context.SendReverseAndReadResponseAsync("patchouli.host.find",
                new { @in = "patchouli://", format = "json" });
            return DefaultFindPage();
        };

        McpCommandResult<McpFindMeta, object> outer = await harness.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://plugin/" + PluginA + "/results/", null));

        outer.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        ToolIsError(captured!.Value).Should().BeFalse();
        ToolResponseText(captured.Value).Should().Contain("patchouli://plugin/");
    }

    [Fact]
    public async Task Reverse_unknown_method_gets_a_jsonrpc_error()
    {
        await using ReverseHarness harness = await ReverseHarness.CreateAsync(
            [(PluginA, ["find"], ["find"])]);

        JsonElement response = await harness.Process(PluginA).SendReverseAsync(
            "patchouli.host.execute", new { }, ReverseTimeout);

        // patchouli.host.execute is a reverse method with an unknown verb: a tool
        // response, not a JSON-RPC error.
        ToolIsError(response).Should().BeTrue();
        ToolResponseText(response).Should().Contain("invalid_argument");

        JsonElement unknown = await harness.Process(PluginA).SendReverseAsync(
            "unknown/method", new { }, ReverseTimeout);
        unknown.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.GetProperty("code").GetInt32().Should().Be(-32601);
    }

    // ---- Helpers --------------------------------------------------------------

    private static object ToolResponse(string text)
    {
        return new { content = new[] { new { type = "text", text } }, isError = false };
    }

    private static string ToolError(ReverseHostReply reply)
    {
        reply.Result.Should().NotBeNull();
        return reply.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    /// <summary>Text of a tool-response result (or error) carried in a JSON-RPC response.</summary>
    private static string ToolResponseText(JsonElement response)
    {
        JsonElement result = response.GetProperty("result");
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static bool ToolIsError(JsonElement response)
    {
        JsonElement result = response.GetProperty("result");
        return result.GetProperty("isError").GetBoolean();
    }

    private static JsonElement DefaultFindPage()
    {
        return JsonSerializer.SerializeToElement(new
        {
            entries = new[] { new { uri = "status.json", title = "status", type = "file" } }
        });
    }

    private const string CatalogIndex =
        """
        {
          "schema": "patchouli.community.index/1",
          "generated_at": "2026-11-12T00:00:00Z",
          "plugins": [
            {
              "id": "test.reverse-a",
              "name": "Reverse A",
              "description": "Test plugin",
              "version": "0.1.0",
              "license": "MIT",
              "repository": "https://example.com/test/reverse-a",
              "source_revision": "0000000000000000000000000000000000000000",
              "requested_verbs": ["find", "fetch"],
              "requested_host_verbs": ["find", "put"],
              "library_grants": [],
              "artifacts": [
                { "platform": "linux-x64", "url": "https://example.com/test/reverse-a.zip", "size": 1234, "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }
              ]
            }
          ]
        }
        """;

    /// <summary>One mounted plugin (or two) over in-memory pipes, wired to the real command surface.</summary>
    private sealed class ReverseHarness : IAsyncDisposable
    {
        private readonly HostPluginApi _api;

        private ReverseHarness(HostPluginApi api, McpCommandService commands, RecordingMcpWriteApi writes,
            FakeLauncher launcher)
        {
            _api = api;
            Commands = commands;
            Writes = writes;
            Launcher = launcher;
        }

        public McpCommandService Commands { get; }
        public RecordingMcpWriteApi Writes { get; }
        public FakeLauncher Launcher { get; }
        public int InvokerCalls { get; private set; }

        public ReverseFakeProcess Process(string pluginId)
        {
            return Launcher.Processes[pluginId];
        }

        public static async Task<ReverseHarness> CreateAsync(
            (string Id, string[] HostVerbs, string[] GrantedHostVerbs)[] plugins,
            Func<CancellationToken, Task<McpServerSettings>>? permissions = null)
        {
            string root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            List<string> registryEntries = [];
            foreach ((string id, string[] hostVerbs, string[] granted) in plugins)
            {
                string package = WritePackage(root, id, hostVerbs);
                string verbs = string.Join(",", granted.Select(verb => "\"" + verb + "\""));
                registryEntries.Add(
                    $$"""{"manifest_path":"{{package.Replace("\\", "\\\\")}}/patchouli-plugin.toml","enabled":true,"allowed_verbs":["find","fetch","send"],"allowed_host_verbs":[{{verbs}}]}""");
            }

            string registryPath = Path.Combine(root, "dev-plugins.json");
            File.WriteAllText(registryPath, "[" + string.Join(",", registryEntries) + "]");

            FakeLauncher launcher = new();
            HostPluginApi api = new(registryPath, launcher);
            RecordingMcpWriteApi writes = new();
            Func<CancellationToken, Task<McpServerSettings>> settingsFunc = permissions ??
                                                                            (_ => Task.FromResult(
                                                                                McpServerSettingsService
                                                                                    .DefaultSettings(DateTimeOffset
                                                                                        .UtcNow)));
            McpCommandService commands = new(Stub<IMcpReadApi>.Create(), writes,
                Stub<IBiblatexImportService>.Create(), Stub<IItemService>.Create(),
                Stub<IVersionedEvidenceReader>.Create(),
                permissionSettings: settingsFunc, pluginApi: api);
            McpProtocolHandler handler = new(commands, Stub<IMcpReadApi>.Create());
            ReverseHarness harness = new(api, commands, writes, launcher);
            api.SetReverseHostInvoker(
                (tool, args, ct) =>
                {
                    harness.InvokerCalls++;
                    return handler.InvokeToolAsync(tool, args, ct);
                },
                settingsFunc);
            foreach ((string id, _, _) in plugins)
            {
                await harness.StartAsync(id);
            }

            return harness;
        }

        /// <summary>
        /// Starts the mount's process (handshake) via a live find on the declared
        /// results/ directory. The mount-root find is answered statically from the
        /// manifest and never starts the process.
        /// </summary>
        public async Task StartAsync(string pluginId)
        {
            McpCommandResult<McpFindMeta, object> started = await Commands.FindAsync(
                new McpFindRequest(null, "patchouli://plugin/" + pluginId + "/results/", null));
            started.IsSuccess.Should().BeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            await _api.DisposeAsync();
        }

        private static string WritePackage(string root, string pluginId, string[] hostVerbs)
        {
            string package = Path.Combine(root, pluginId);
            Directory.CreateDirectory(Path.Combine(package, "schemas"));
            Directory.CreateDirectory(Path.Combine(package, ".agents"));
            File.WriteAllText(Path.Combine(package, ".agents", "plugin-usage.md"), "# usage\n");
            File.WriteAllText(Path.Combine(package, "schemas", "status.json"),
                """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
            File.WriteAllText(Path.Combine(package, "schemas", "result.json"),
                """{"type":"object","required":["result_id","action"],"properties":{"result_id":{"type":"string"},"action":{"type":"string"}}}""");
            File.WriteAllText(Path.Combine(package, "schemas", "classify.json"),
                """{"type":"object","additionalProperties":false,"required":["image_base64"],"properties":{"image_base64":{"type":"string","minLength":1}}}""");
            string hostSection = hostVerbs.Length == 0
                ? string.Empty
                : "\n[host]\nverbs = [" + string.Join(",", hostVerbs.Select(verb => "\"" + verb + "\"")) + "]\n";
            File.WriteAllText(Path.Combine(package, PluginManifestLoader.ManifestFileName),
                ManifestFor(pluginId) + hostSection);
            return package;
        }

        private static string ManifestFor(string pluginId)
        {
            return $$"""
                     id = "{{pluginId}}"
                     version = "0.1.0"
                     schema = "patchouli.plugin/1"
                     host_api = "patchouli.plugins/1"
                     name = "Reverse {{pluginId}}"
                     description = "Reverse test plugin"
                     readme = ".agents/plugin-usage.md"

                     [runtime]
                     transport = "stdio"
                     command = "dotnet"
                     args = ["ReversePlugin.dll"]

                     [library]
                     grants = []

                     [[vfs.nodes]]
                     path = ""
                     kind = "directory"
                     verbs = ["find","fetch"]
                     summary = "root"

                     [[vfs.nodes]]
                     path = "status.json"
                     kind = "file"
                     verbs = ["fetch"]
                     media = "application/json"
                     schema = "schemas/status.json"

                     [[vfs.nodes]]
                     path = "results/"
                     kind = "directory"
                     verbs = ["find"]
                     summary = "results"

                     [[vfs.endpoints]]
                     path = "control"

                     [[vfs.endpoints.messages]]
                     action = "classify"
                     target_types = ["plugin-root"]
                     input_schema = "schemas/classify.json"
                     result_schema = "schemas/result.json"
                     execution_mode = "sync"
                     side_effects = "Creates an in-memory result."
                     """;
        }
    }

    private sealed class FakeLauncher : IPluginProcessLauncher
    {
        public Dictionary<string, ReverseFakeProcess> Processes { get; } = new(StringComparer.Ordinal);

        public IPluginChildProcess Start(PluginProcessSpec spec)
        {
            string pluginId = Path.GetFileName(spec.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar));
            ReverseFakeProcess process = new();
            Processes[pluginId] = process;
            return process;
        }
    }

    /// <summary>
    /// An in-memory plugin process: answers the handshake, serves tools/call with a
    /// test-set handler, and can send reverse host-verb requests (from the test
    /// thread via a pending map, or from inside a tools/call handler by reading
    /// the host's answer directly off the same reader).
    /// </summary>
    private sealed class ReverseFakeProcess : IPluginChildProcess
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stream _standardInput;
        private readonly Stream _standardOutput;
        private readonly Stream _outputStream;
        private readonly MemoryStream _standardError = new();
        private readonly Task _server;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingReverse = new();
        private int _hasExited;
        private long _nextReverseId = 1_000_000;

        public ReverseFakeProcess()
        {
            _standardInput = _input.Writer.AsStream();
            _standardOutput = _output.Reader.AsStream();
            _outputStream = _output.Writer.AsStream();
            _server = Task.Run(ServeAsync);
        }

        public Stream StandardInput => _standardInput;
        public Stream StandardOutput => _standardOutput;
        public Stream StandardError => _standardError;
        public bool HasExited => _hasExited != 0;
        public int? ExitCode => HasExited ? 0 : null;
        internal CancellationToken LifetimeToken => _lifetime.Token;

        public int ToolCalls { get; private set; }
        public List<string> ToolNames { get; } = [];
        public Func<JsonElement, ReverseToolCallContext, Task<JsonElement>>? OnToolCall { get; set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return _exited.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Sends a reverse request from the test thread; the server loop completes it.</summary>
        public async Task<JsonElement> SendReverseAsync(string method, object parameters, TimeSpan timeout)
        {
            long id = Interlocked.Increment(ref _nextReverseId);
            TaskCompletionSource<JsonElement> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReverse[id] = completion;
            await WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id, method, @params = parameters
            }));
            using CancellationTokenSource cts = new(timeout);
            return await completion.Task.WaitAsync(cts.Token);
        }

        internal long NextReverseId()
        {
            return Interlocked.Increment(ref _nextReverseId);
        }

        internal async Task WriteLineAsync(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _writeGate.WaitAsync(_lifetime.Token);
            try
            {
                await _outputStream.WriteAsync(bytes, _lifetime.Token);
                await _outputStream.FlushAsync(_lifetime.Token);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                using StreamReader reader = new(_input.Reader.AsStream(), Encoding.UTF8, false, 4096, true);
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    using JsonDocument request = JsonDocument.Parse(line);
                    JsonElement root = request.RootElement.Clone();
                    if (!root.TryGetProperty("id", out JsonElement idElement))
                    {
                        continue; // notification
                    }

                    if (!root.TryGetProperty("method", out JsonElement methodElement))
                    {
                        if (idElement.ValueKind == JsonValueKind.Number &&
                            idElement.TryGetInt64(out long responseId) &&
                            _pendingReverse.TryRemove(responseId, out TaskCompletionSource<JsonElement>? pending))
                        {
                            pending.TrySetResult(root);
                        }

                        continue;
                    }

                    string method = methodElement.GetString()!;
                    long id = idElement.GetInt64();
                    object result = method switch
                    {
                        "initialize" => new
                        {
                            protocolVersion = "2025-06-18", capabilities = new { }
                        },
                        "tools/list" => new
                        {
                            tools = PluginStdioClient.RequiredTools.Select(name => new { name }).ToArray()
                        },
                        "tools/call" => await AnswerToolCallAsync(root, reader),
                        _ => new { }
                    };
                    await WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await _output.Writer.CompleteAsync();
                Interlocked.Exchange(ref _hasExited, 1);
                _exited.TrySetResult();
            }
        }

        private async Task<object> AnswerToolCallAsync(JsonElement root, StreamReader reader)
        {
            JsonElement callParams = root.GetProperty("params");
            string tool = callParams.GetProperty("name").GetString()!;
            JsonElement args = callParams.GetProperty("arguments");
            ToolCalls++;
            ToolNames.Add(tool);
            JsonElement structured = OnToolCall is null
                ? DefaultToolResult(tool, args)
                : await OnToolCall(args, new ReverseToolCallContext(this, reader));
            return new
            {
                content = new[] { new { type = "text", text = "{}" } },
                structuredContent = structured,
                isError = false
            };
        }

        private static JsonElement DefaultToolResult(string tool, JsonElement args)
        {
            return tool switch
            {
                "patchouli.plugin.find" => DefaultFindPage(),
                "patchouli.plugin.fetch" => JsonSerializer.SerializeToElement(new
                {
                    uri = args.GetProperty("uri").GetString(),
                    content_type = "application/json",
                    content = """{"ok":true}"""
                }),
                _ => JsonSerializer.SerializeToElement(new { state = "completed" })
            };
        }

        public void KillTree()
        {
            _lifetime.Cancel();
            Interlocked.Exchange(ref _hasExited, 1);
            _exited.TrySetResult();
        }

        public void Dispose()
        {
            KillTree();
        }
    }

    /// <summary>Tools/call handler context: send a reverse request and read the host's answer directly.</summary>
    private sealed class ReverseToolCallContext(ReverseFakeProcess process, StreamReader reader)
    {
        public async Task<JsonElement> SendReverseAndReadResponseAsync(string method, object parameters)
        {
            long id = process.NextReverseId();
            await process.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id, method, @params = parameters
            }));
            string? line = await reader.ReadLineAsync(process.LifetimeToken);
            if (line is null)
            {
                throw new InvalidOperationException("The host closed the channel during a reverse call.");
            }

            using JsonDocument response = JsonDocument.Parse(line);
            JsonElement root = response.RootElement.Clone();
            if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long got) ||
                got != id)
            {
                throw new InvalidOperationException($"Expected the reverse answer to id {id}, got: {line}");
            }

            return root;
        }
    }

    /// <summary>Records put calls so the test proves the mutation landed on the write API.</summary>
    private sealed class RecordingMcpWriteApi : IMcpWriteApi
    {
        public List<McpPutRequest> Puts { get; } = [];

        public Task<Result<McpPutResponse>> PutAsync(McpPutRequest request,
            CancellationToken cancellationToken = default)
        {
            Puts.Add(request);
            return Task.FromResult(Result<McpPutResponse>.Success(
                new McpPutResponse(request.Uri, "item", true, request.Content?.Length ?? 0)));
        }

        public Task<Result<McpPutResponse>> PutPageTranslationAsync(string uri,
            DocumentInstanceId documentInstanceId, PageId pageId, string content,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
                "XML writes unavailable."));
        }
    }

    /// <summary>Defaulting proxy: returns failed Results for any API call not overridden.</summary>
    private class Stub<T> : DispatchProxy where T : class
    {
        public static T Create()
        {
            return Create<T, Stub<T>>()!;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Type resultType = method!.ReturnType.GenericTypeArguments[0];
            if (resultType == typeof(Result<McpLibraryStateResponse>))
            {
                return Task.FromResult(Result<McpLibraryStateResponse>.Success(
                    new McpLibraryStateResponse("lib", "lib:1")));
            }

            object failure = resultType
                .GetMethod("Failure", [typeof(string), typeof(string)])!
                .Invoke(null, ["fake", "x"])!;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType).Invoke(null, [failure]);
        }
    }
}
