using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// The patchouli:plugin-request bridge: permission-gated fetch/send scoped to the session's
/// own plugin, path safety, malformed/oversize handling, pending cap, release cancellation
/// and the JS surface installed by <see cref="PluginViewContextScript"/>.
/// </summary>
public sealed class PluginViewBridgeTests
{
    private static readonly McpPluginViewInfo TabView = new(
        "main", "视图", "tab",
        "patchouli://plugin/demo/views/main",
        "patchouli://plugin/demo/ui/main.html", "ui/main.html");

    private const string ViewHtml = "<html><head></head><body>hi</body></html>";

    private static async Task<PluginViewSession> LoadedSession(
        FakeBridgeApi api, string pluginId = "demo")
    {
        PluginViewSession session = new(pluginId, TabView);
        (await session.LoadAsync(api)).Should().BeTrue();
        api.FetchCalls.Clear(); // the view-source load fetch is not bridge traffic
        return session;
    }

    private static string Request(
        string id = "r1", string operation = "fetch", string path = "status.json",
        string? action = null, string? args = null, string? requestId = null)
    {
        return "{\"type\":\"patchouli:plugin-request\",\"id\":" + JsonSerializer.Serialize(id) +
               ",\"operation\":" + JsonSerializer.Serialize(operation) +
               ",\"path\":" + JsonSerializer.Serialize(path) +
               (action is null ? "" : ",\"action\":" + JsonSerializer.Serialize(action)) +
               (args is null ? "" : ",\"args\":" + args) +
               (requestId is null ? "" : ",\"request_id\":" + JsonSerializer.Serialize(requestId)) + "}";
    }

    /// <summary>files requests carry no path — the operation addresses the whole managed set.</summary>
    private static string FilesRequest(string id = "r1", string? action = "status")
    {
        return "{\"type\":\"patchouli:plugin-request\",\"id\":" + JsonSerializer.Serialize(id) +
               ",\"operation\":\"files\"" +
               (action is null ? "" : ",\"action\":" + JsonSerializer.Serialize(action)) + "}";
    }

    private static PluginManagedFilesView ManagedView(string state = "missing",
        params PluginManagedFileView[] files)
    {
        return new PluginManagedFilesView("demo", state, files);
    }

    private static JsonElement Reply(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    // ---------- success paths ----------

    [Fact]
    public async Task Bridge_fetch_returns_content_for_the_sessions_own_plugin()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("application/json", "{\"ready\":true}"));
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request()))!);

        reply.GetProperty("id").GetString().Should().Be("r1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        reply.GetProperty("value").GetProperty("content_type").GetString()
            .Should().Be("application/json");
        reply.GetProperty("value").GetProperty("content").GetString().Should().Be("{\"ready\":true}");
        api.FetchCalls.Should().ContainSingle().Which.PluginId.Should().Be("demo");
        api.FetchCalls[0].Path.Should().Be("status.json");
    }

    [Fact]
    public async Task Bridge_fetch_accepts_the_plugin_root_path()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("application/json", "{}"));
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request(path: "")))!);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        api.FetchCalls.Single().Path.Should().Be("");
    }

    [Fact]
    public async Task Bridge_send_returns_state_and_result_with_args_and_request_id()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "send");
        api.SendResult = Result<McpPluginSendOutcome>.Success(
            new McpPluginSendOutcome("completed", JsonDocument.Parse("{\"n\":1}").RootElement));
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request(
            operation: "send", path: "control", action: "prepare_assets",
            args: "{\"allow_download\":true}", requestId: "req-9")))!);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        reply.GetProperty("value").GetProperty("state").GetString().Should().Be("completed");
        reply.GetProperty("value").GetProperty("result").GetProperty("n").GetInt32().Should().Be(1);
        api.SendCalls.Should().ContainSingle();
        api.SendCalls[0].Endpoint.Should().Be("control");
        api.SendCalls[0].Action.Should().Be("prepare_assets");
        api.SendCalls[0].RequestId.Should().Be("req-9");
        api.SendCalls[0].Args.GetProperty("allow_download").GetBoolean().Should().BeTrue();
    }

    // ---------- files: status / ensure ----------

    [Fact]
    public async Task Files_status_returns_sanitized_managed_files()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        session.ManagedFileStatusAsync = (pluginId, _) =>
            Task.FromResult(Result<PluginManagedFilesView>.Success(
                ManagedView("missing",
                    new PluginManagedFileView("models/ocr.onnx", "missing", 4096,
                        "aabbcc", "1.2.0"))));

        string replyJson = (await session.HandleRequestAsync(FilesRequest()))!;
        JsonElement reply = Reply(replyJson);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        JsonElement value = reply.GetProperty("value");
        value.GetProperty("state").GetString().Should().Be("missing");
        JsonElement file = value.GetProperty("files")[0];
        file.GetProperty("path").GetString().Should().Be("models/ocr.onnx");
        file.GetProperty("state").GetString().Should().Be("missing");
        file.GetProperty("expected_bytes").GetInt64().Should().Be(4096);
        file.GetProperty("sha256").GetString().Should().Be("aabbcc");
        file.GetProperty("version").GetString().Should().Be("1.2.0");
        replyJson.Should().NotContain("local_path")
            .And.NotContain("destination")
            .And.NotContain("\"url\"", "download URLs may carry query secrets — withheld")
            .And.NotContain("C:\\", "local paths never cross the bridge");
    }

    [Fact]
    public async Task Files_ensure_reports_the_confirmed_completed_state()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        string? ensuredPlugin = null;
        session.ConfirmManagedFilesAsync = (pluginId, _) =>
        {
            ensuredPlugin = pluginId;
            return Task.FromResult(Result<PluginManagedFilesView>.Success(
                ManagedView("completed",
                    new PluginManagedFileView("models/ocr.onnx", "present", 4096))));
        };

        JsonElement reply = Reply((await session.HandleRequestAsync(FilesRequest(action: "ensure")))!);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        reply.GetProperty("value").GetProperty("state").GetString().Should().Be("completed");
        reply.GetProperty("value").GetProperty("files")[0]
            .GetProperty("state").GetString().Should().Be("present");
        ensuredPlugin.Should().Be("demo", "the callback always runs for the session's own plugin");
    }

    [Fact]
    public async Task Files_status_is_scoped_to_the_sessions_own_plugin()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        string? statusPlugin = null;
        session.ManagedFileStatusAsync = (pluginId, _) =>
        {
            statusPlugin = pluginId;
            return Task.FromResult(Result<PluginManagedFilesView>.Success(ManagedView("satisfied")));
        };

        // Even a request smuggling a foreign plugin id stays scoped to the bound session plugin.
        string request = "{\"type\":\"patchouli:plugin-request\",\"id\":\"x\"," +
                         "\"operation\":\"files\",\"action\":\"status\",\"plugin_id\":\"other\"}";
        JsonElement reply = Reply((await session.HandleRequestAsync(request))!);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        statusPlugin.Should().Be("demo");
    }

    [Fact]
    public async Task Files_requires_no_fetch_or_send_verb_beyond_view()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view"); // no fetch/send grants
        PluginViewSession session = await LoadedSession(api);
        session.ManagedFileStatusAsync = (pluginId, _) =>
            Task.FromResult(Result<PluginManagedFilesView>.Success(ManagedView("satisfied")));

        JsonElement reply = Reply((await session.HandleRequestAsync(FilesRequest()))!);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Files_decline_or_error_codes_pass_through()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        session.ConfirmManagedFilesAsync = (pluginId, _) =>
            Task.FromResult(Result<PluginManagedFilesView>.Failure("declined", "已取消插件文件下载。"));

        JsonElement reply = Reply((await session.HandleRequestAsync(FilesRequest(action: "ensure")))!);

        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("declined");
    }

    [Fact]
    public async Task Files_without_a_host_capability_reports_unsupported()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        // No ManagedFileStatusAsync/ConfirmManagedFilesAsync wired.

        Reply((await session.HandleRequestAsync(FilesRequest()))!)
            .GetProperty("error").GetProperty("code").GetString().Should().Be("unsupported");
        Reply((await session.HandleRequestAsync(FilesRequest(action: "ensure")))!)
            .GetProperty("error").GetProperty("code").GetString().Should().Be("unsupported");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("download")]
    [InlineData("")]
    public async Task Files_rejects_missing_or_unknown_actions(string? action)
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        int calls = 0;
        session.ManagedFileStatusAsync = (pluginId, _) =>
        {
            calls++;
            return Task.FromResult(Result<PluginManagedFilesView>.Success(ManagedView()));
        };

        JsonElement reply = Reply((await session.HandleRequestAsync(FilesRequest(action: action)))!);

        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_request");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Revoked_view_grant_denies_files_without_invoking_callbacks()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        int calls = 0;
        session.ManagedFileStatusAsync = (pluginId, _) =>
        {
            calls++;
            return Task.FromResult(Result<PluginManagedFilesView>.Success(ManagedView()));
        };
        api.Revoke("demo", "view");

        JsonElement reply = Reply((await session.HandleRequestAsync(FilesRequest()))!);

        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("permission_denied");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Released_session_cancels_a_pending_ensure()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);
        session.ConfirmManagedFilesAsync = async (pluginId, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Result<PluginManagedFilesView>.Success(ManagedView("completed"));
        };

        Task<string?> pending = session.HandleRequestAsync(FilesRequest(action: "ensure"));
        session.Release();

        Reply((await pending)!).GetProperty("error").GetProperty("code").GetString()
            .Should().Be("cancelled");
    }

    // ---------- permission gating ----------

    [Fact]
    public async Task Revoked_view_grant_denies_bridge_without_invoking_the_api()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        PluginViewSession session = await LoadedSession(api);
        api.Revoke("demo", "view");

        JsonElement reply = Reply((await session.HandleRequestAsync(Request()))!);

        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("permission_denied");
        api.FetchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_fetch_grant_denies_bridge_without_invoking_the_api()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request()))!);

        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("permission_denied");
        api.FetchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_send_grant_denies_bridge_without_invoking_the_api()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view");
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request(
            operation: "send", path: "control", action: "analyze", args: "{}")))!);

        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("permission_denied");
        api.SendCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Released_session_reports_unavailable_and_cancels_pending_calls()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        api.BlockFetches();
        PluginViewSession session = await LoadedSession(api);

        Task<string?> pending = session.HandleRequestAsync(Request());
        Task<string?> afterRelease = session.HandleRequestAsync(Request("r2"));
        session.Release();

        // r2 was already in flight when the session released → cancelled, like r1.
        Reply((await afterRelease)!).GetProperty("error").GetProperty("code").GetString()
            .Should().Be("cancelled");
        Reply((await pending)!).GetProperty("error").GetProperty("code").GetString()
            .Should().Be("cancelled");
        Reply((await session.HandleRequestAsync(Request("r3")))!)
            .GetProperty("error").GetProperty("code").GetString()
            .Should().Be("session_unavailable");
    }

    [Fact]
    public async Task Session_never_loaded_reports_unavailable()
    {
        FakeBridgeApi api = new();
        PluginViewSession session = new("demo", TabView);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request()))!);

        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("session_unavailable");
        api.FetchCalls.Should().BeEmpty();
    }

    // ---------- request validation ----------

    [Theory]
    [InlineData("/abs.json")]
    [InlineData("../secret")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("c:/windows")]
    [InlineData("http://evil/x")]
    [InlineData("patchouli://plugin/other/status.json")]
    [InlineData("./x")]
    public async Task Foreign_or_traversal_paths_are_rejected(string path)
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(Request(path: path)))!);

        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_path");
        api.FetchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Path_containing_nul_is_rejected()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(
            Request(path: "a\u0000b")))!);

        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_path");
    }

    [Fact]
    public async Task Plugin_isolation_is_enforced_by_scope_not_by_request_fields()
    {
        // The bridge has no plugin-id field: whatever the document asks, the call is
        // always executed against the session's own plugin.
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        api.FetchResult = Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent("text/plain", "ok"));
        PluginViewSession session = await LoadedSession(api);

        string request = "{\"type\":\"patchouli:plugin-request\",\"id\":\"x\"," +
                         "\"operation\":\"fetch\",\"path\":\"status.json\",\"plugin_id\":\"other\"}";
        JsonElement reply = Reply((await session.HandleRequestAsync(request))!);

        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        api.FetchCalls.Single().PluginId.Should().Be("demo");
    }

    [Theory]
    [InlineData("{\"type\":\"patchouli:plugin-request\",\"operation\":\"fetch\",\"path\":\"x\"}", "invalid_request")]
    [InlineData("{\"type\":\"patchouli:plugin-request\",\"id\":5,\"operation\":\"fetch\",\"path\":\"x\"}",
        "invalid_request")]
    [InlineData("{\"type\":\"patchouli:plugin-request\",\"id\":\"x\",\"operation\":\"exec\",\"path\":\"x\"}",
        "invalid_request")]
    [InlineData("{\"type\":\"patchouli:plugin-request\",\"id\":\"x\",\"operation\":\"fetch\"}", "invalid_path")]
    [InlineData("{\"type\":\"patchouli:plugin-request\",\"id\":\"x\",\"operation\":\"send\",\"path\":\"control\"}",
        "invalid_request")]
    [InlineData(
        "{\"type\":\"patchouli:plugin-request\",\"id\":\"x\",\"operation\":\"send\",\"path\":\"control\",\"action\":\"a\",\"args\":[]}",
        "invalid_request")]
    public async Task Malformed_requests_get_structured_errors(string request, string code)
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch", "send");
        PluginViewSession session = await LoadedSession(api);

        JsonElement reply = Reply((await session.HandleRequestAsync(request))!);

        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetProperty("code").GetString().Should().Be(code);
        api.FetchCalls.Should().BeEmpty();
        api.SendCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_request_bodies_are_rejected()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        PluginViewSession session = await LoadedSession(api);

        string request = "{\"type\":\"patchouli:plugin-request\",\"id\":\"big\"," +
                         "\"operation\":\"fetch\",\"path\":\"status.json\",\"pad\":\"" +
                         new string('x', PluginViewSession.MaxRequestBodyChars) + "\"}";

        JsonElement reply = Reply((await session.HandleRequestAsync(request))!);

        reply.GetProperty("id").GetString().Should().Be("big");
        reply.GetProperty("error").GetProperty("code").GetString().Should().Be("request_too_large");
        api.FetchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_calls_are_capped()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        api.BlockFetches();
        PluginViewSession session = await LoadedSession(api);
        try
        {
            List<Task<string?>> pending = [];
            for (int i = 0; i < PluginViewSession.MaxPendingRequests; i++)
            {
                pending.Add(session.HandleRequestAsync(Request("p" + i)));
            }

            JsonElement overflow = Reply((await session.HandleRequestAsync(Request("overflow")))!);
            overflow.GetProperty("error").GetProperty("code").GetString().Should().Be("busy");
            api.FetchCalls.Should().HaveCount(PluginViewSession.MaxPendingRequests);
        }
        finally
        {
            session.Release();
        }
    }

    [Fact]
    public async Task Non_request_messages_are_not_bridge_traffic()
    {
        FakeBridgeApi api = new();
        api.Allow("demo", "view", "fetch");
        PluginViewSession session = await LoadedSession(api);

        (await session.HandleRequestAsync("{\"type\":\"patchouli:request-context\"}"))
            .Should().BeNull();
        (await session.HandleRequestAsync("{\"type\":\"other\"}")).Should().BeNull();
        (await session.HandleRequestAsync("not json")).Should().BeNull();
        (await session.HandleRequestAsync("[1,2]")).Should().BeNull();
        api.FetchCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("status.json", true)]
    [InlineData("results/r_1.json", true)]
    [InlineData("results/", true)]
    [InlineData("/", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a/../b", false)]
    [InlineData("a//b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    [InlineData("//server", false)]
    public void Plugin_path_validation(string path, bool expected)
    {
        PluginViewSession.IsValidPluginPath(path).Should().Be(expected);
    }

    // ---------- JS surface ----------

    [Fact]
    public void Init_script_installs_fetch_and_send_promises_once()
    {
        PluginViewContext context = new("light", "light", false,
            new Dictionary<string, string>(), null);

        string init = PluginViewContextScript.BuildInitScript(context);

        init.Should().Contain("invokeCSharpAction");
        init.Should().Contain("patchouli:plugin-request");
        init.Should().Contain("w.fetch=function");
        init.Should().Contain("w.send=function");
        init.Should().Contain("patchouli:plugin-response");
        init.Should().Contain("MAX_PENDING=16");
        init.Should().Contain("TIMEOUT_MS=60000");
        init.Should().Contain("w.files=function(){return request('files',null,'status');}");
        init.Should().Contain("w.ensureFiles=function()");
        init.Should().Contain("'ensure',undefined,undefined,ENSURE_TIMEOUT_MS");
        init.Should().Contain("ENSURE_TIMEOUT_MS=900000",
            "GB-sized managed downloads get a 15-minute reply bound, not the 60s default");
    }

    [Fact]
    public void Push_script_preserves_api_function_identity()
    {
        PluginViewContext context = new("dark", "dark", true,
            new Dictionary<string, string>(), null);

        string push = PluginViewContextScript.BuildPushScript(context);

        push.Should().NotContain("w.fetch=");
        push.Should().NotContain("w.send=");
        push.Should().NotContain("window.patchouli=p;",
            "push merges context fields instead of replacing the object that owns the APIs");
        push.Should().Contain("window.patchouli=window.patchouli||{}");
    }

    [Fact]
    public void Response_dispatch_emits_the_reply_as_event_detail()
    {
        string script = PluginViewContextScript.BuildResponseDispatchScript(
            "{\"id\":\"r1\",\"ok\":true,\"value\":{\"content_type\":\"text/plain\",\"content\":\"x\"}}");

        script.Should().StartWith("window.dispatchEvent(new CustomEvent('patchouli:plugin-response'");
        script.Should().Contain("\"id\":\"r1\"");
        script.Should().Contain("\"ok\":true");
    }

    [Fact]
    public void Csp_is_unchanged_by_the_bridge()
    {
        PluginViewContextScript.ContentSecurityPolicy.Should().Be(
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
            "img-src data:; font-src data:; media-src data:; connect-src 'none'; " +
            "frame-src 'none'; object-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'");
    }

    // ---------- fake ----------

    private sealed class FakeBridgeApi : IMcpPluginApi
    {
        private readonly HashSet<string> _allowed = new(StringComparer.Ordinal);
        private volatile Task _fetchGate = Task.CompletedTask;
        private volatile string _blockedPath = "\u0001";

        public List<(string PluginId, string Path)> FetchCalls { get; } = [];

        public List<(string PluginId, string Endpoint, string Action, JsonElement Args, string? RequestId)>
            SendCalls { get; } = [];

        public Result<McpPluginFetchContent> FetchResult { get; set; } =
            Result<McpPluginFetchContent>.Success(new McpPluginFetchContent("text/html", ViewHtml));

        public Result<McpPluginSendOutcome> SendResult { get; set; } =
            Result<McpPluginSendOutcome>.Failure(AppErrorCodes.NotFound, "unset");

        public IReadOnlyList<McpPluginMountInfo> Mounts => [];

        public void Allow(string pluginId, params string[] verbs)
        {
            foreach (string verb in verbs)
            {
                _allowed.Add(pluginId + "\n" + verb);
            }
        }

        public void Revoke(string pluginId, string verb)
        {
            _allowed.Remove(pluginId + "\n" + verb);
        }

        /// <summary>Blocks fetches of "status.json" only — the view-source load fetch
        /// must stay unblocked for the session to reach its live state.</summary>
        public void BlockFetches()
        {
            _blockedPath = "status.json";
            _fetchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        public McpPluginMountInfo? FindMount(string pluginId)
        {
            return null;
        }

        public bool AllowsVerb(string pluginId, string verb)
        {
            return _allowed.Contains(pluginId + "\n" + verb);
        }

        public bool IsEndpoint(string pluginId, string relativePath)
        {
            return relativePath == "control";
        }

        public IReadOnlyList<McpPluginViewInfo> Views(string pluginId)
        {
            return [];
        }

        public McpPluginViewInfo? FindView(string pluginId, string viewId)
        {
            return null;
        }

        public Result<McpPluginFetchContent?> TryFetchStatic(string pluginId, string relativePath)
        {
            return Result<McpPluginFetchContent?>.Success(null);
        }

        public Task<Result<McpPluginFindPage>> FindAsync(string pluginId, string relativeScope,
            string? query, int limit, string? cursor, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task<Result<McpPluginFetchContent>> FetchAsync(string pluginId,
            string relativePath, CancellationToken cancellationToken = default)
        {
            FetchCalls.Add((pluginId, relativePath));
            if (relativePath == _blockedPath)
            {
                await _fetchGate.WaitAsync(cancellationToken);
            }

            return FetchResult;
        }

        public Task<Result<McpPluginSendOutcome>> SendAsync(string pluginId, string endpointPath,
            string action, JsonElement args, string? requestId,
            CancellationToken cancellationToken = default)
        {
            SendCalls.Add((pluginId, endpointPath, action, args.Clone(), requestId));
            return Task.FromResult(SendResult);
        }
    }
}
