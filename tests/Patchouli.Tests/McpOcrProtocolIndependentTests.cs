using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Tests;

public sealed class McpOcrProtocolIndependentTests
{
    private const string DocumentId = "11111111-1111-1111-1111-111111111111";
    private const string TaskId = "22222222-2222-2222-2222-222222222222";
    private const string DocumentUri = "patchouli://texts/" + DocumentId + "/";
    private const string TaskUri = "patchouli://runs/ocr/" + TaskId + "/";

    [Fact]
    public async Task Typed_start_returns_started_receipt_and_queryable_run_without_claiming_completion()
    {
        Harness harness = new();
        string response = await harness.Call("ocr.start", DocumentUri,
            new { pages = "1,3-5", engine = "rapidocr", request_id = "stable-request" });
        IsError(response).Should().BeFalse(response);
        harness.LastStart.Should().Be(new McpOcrStartRequest(DocumentInstanceId.Parse(DocumentId),
            "1,3-5", "rapidocr", "stable-request"));
        using JsonDocument envelope = Envelope(response);
        JsonElement entry = envelope.RootElement.GetProperty("entries")[0];
        envelope.RootElement.GetProperty("meta").GetProperty("state").GetString().Should().Be("started");
        entry.GetProperty("state").GetString().Should().Be("started");
        entry.GetProperty("run_uri").GetString().Should().Be(TaskUri + "status");
        JsonElement receipt = entry.GetProperty("result");
        receipt.GetProperty("task_id").GetString().Should().Be(TaskId);
        receipt.GetProperty("state").GetString().Should().Be("queued");
        receipt.GetProperty("engine_id").GetString().Should().Be("rapidocr");
        receipt.GetProperty("preset_version_id").GetString().Should().Be("33333333-3333-3333-3333-333333333333");
        harness.Calls.Should().Equal("start");
    }

    [Theory]
    [InlineData("ocr.cancel", "completed")]
    [InlineData("ocr.retry", "started")]
    public async Task Task_actions_distinguish_direct_acknowledgement_from_background_acceptance(string action,
        string state)
    {
        Harness harness = new();
        object args = action == "ocr.retry" ? new { request_id = "retry-id" } : new { };
        string response = await harness.Call(action, TaskUri + "status", args);
        IsError(response).Should().BeFalse(response);
        harness.LastTask.Should().Be(OcrQueueTaskId.Parse(TaskId));
        using JsonDocument envelope = Envelope(response);
        JsonElement entry = envelope.RootElement.GetProperty("entries")[0];
        entry.GetProperty("state").GetString().Should().Be(state);
        entry.GetProperty("result").GetProperty("state").GetString().Should().Be("queued",
            "completed acknowledges cancel handling and does not claim the worker has reached a terminal state");
        if (action == "ocr.retry")
        {
            harness.LastRetryRequest.Should().Be("retry-id");
            entry.GetProperty("run_uri").GetString().Should().Be(TaskUri + "status");
        }
        else
        {
            entry.TryGetProperty("run_uri", out _).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("ocr.start", DocumentUri)]
    [InlineData("ocr.cancel", TaskUri)]
    [InlineData("ocr.retry", TaskUri)]
    public async Task Each_exact_target_action_grant_is_required_before_handler(string action, string target)
    {
        Harness harness = new(deniedAction: action);
        ErrorName(await harness.Call(action, target, new { })).Should().Be("PERMISSION_DENIED");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ocr.cancel")]
    [InlineData("ocr.retry")]
    public async Task Other_action_denial_does_not_disable_granted_start(string deniedAction)
    {
        Harness harness = new(deniedAction: deniedAction);
        string response = await harness.Call("ocr.start", DocumentUri, new { });
        IsError(response).Should().BeFalse(response);
        harness.Calls.Should().Equal("start");
    }

    [Fact]
    public async Task Old_send_grants_alone_do_not_authorize_ocr_actions()
    {
        Harness harness = new(noActionGrants: true);
        ErrorName(await harness.Call("ocr.start", DocumentUri, new { })).Should().Be("PERMISSION_DENIED");
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_shared_send_without_permission_settings_does_not_inherit_legacy_allow_all()
    {
        Stub<IMcpReadApi> read = Stub<IMcpReadApi>.Create();
        read.Handler = (method, _) => method == nameof(IMcpReadApi.GetCurrentLibraryStateAsync)
            ? Task.FromResult(
                Result<McpLibraryStateResponse>.Success(new McpLibraryStateResponse("independent", "lib:1")))
            : throw new NotSupportedException(method);
        List<string> calls = [];
        Stub<IMcpOcrCommandApi> ocr = Stub<IMcpOcrCommandApi>.Create();
        ocr.Handler = (method, _) =>
        {
            calls.Add(method);
            return Task.FromResult(Result<McpOcrCommandResult>.Success(new McpOcrCommandResult(
                TaskId, TaskUri + "status", "queued", "rapidocr", "preset-fixture",
                "33333333-3333-3333-3333-333333333333")));
        };
        McpCommandService commands = new(read.Value, Stub<IMcpWriteApi>.Create().Value,
            Stub<IBiblatexImportService>.Create().Value, Stub<IItemService>.Create().Value,
            Stub<IVersionedEvidenceReader>.Create().Value, ocrCommands: ocr.Value);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> result = await commands.SendAsync(
            new McpActionSendRequest(DocumentUri, "ocr.start", JsonSerializer.SerializeToElement(new { })));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
        calls.Should()
            .BeEmpty("new mutating actions require an explicit action grant even without a settings delegate");
    }

    [Fact]
    public async Task Send_switch_is_separate_from_action_authorization()
    {
        Harness harness = new(false);
        ErrorName(await harness.Call("ocr.start", DocumentUri, new { })).Should().Be("PERMISSION_DENIED");
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Tool_surface_remains_the_existing_seven_tools_without_top_level_ocr()
    {
        Harness harness = new();
        using JsonDocument list = JsonDocument.Parse(await harness.Handler.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).Should().BeEquivalentTo(
            [
                "patchouli.find", "patchouli.fetch", "patchouli.put", "patchouli.cite", "patchouli.send",
                "patchouli.add", "patchouli.view"
            ]);
        string oldTool =
            await harness.Tool("patchouli.ocr", new { action = "start", uri = DocumentUri, format = "json" });
        ErrorName(oldTool).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ocr.start", TaskUri)]
    [InlineData("ocr.start", "patchouli://texts/11111111-1111-1111-1111-111111111111/page-1.xml")]
    [InlineData("ocr.cancel", DocumentUri)]
    [InlineData("ocr.retry", "patchouli://runs/agent/22222222-2222-2222-2222-222222222222/")]
    public async Task Registry_rejects_wrong_action_target_before_domain_handler(string action, string target)
    {
        Harness harness = new();
        ErrorName(await harness.Call(action, target, new { })).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ocr.start", """{"pages":2}""")]
    [InlineData("ocr.start", """{"engine":false}""")]
    [InlineData("ocr.start", """{"request_id":[]}""")]
    [InlineData("ocr.start", """{"pages":""}""")]
    [InlineData("ocr.start", """{"engine":""}""")]
    [InlineData("ocr.start", """{"request_id":""}""")]
    [InlineData("ocr.start", """{"preset":"p"}""")]
    [InlineData("ocr.start", """{"engine":"mock","engine":"rapidocr"}""")]
    [InlineData("ocr.cancel", """{"pages":"1"}""")]
    [InlineData("ocr.retry", """{"engine":"rapidocr"}""")]
    [InlineData("ocr.retry", """{"request_id":1}""")]
    [InlineData("cite.render", """{"refs":[]}""")]
    [InlineData("cite.render", """{"html":"true"}""")]
    [InlineData("cite.render", """{"locale":[]}""")]
    [InlineData("cite.render", """{"html":true,"html":false}""")]
    [InlineData("ocr.start", "null")]
    [InlineData("ocr.start", "[]")]
    [InlineData("ocr.start", "1")]
    public async Task Authoritative_action_schema_rejects_unknown_duplicate_and_non_object_args(string action,
        string args)
    {
        Harness harness = new();
        string target = action is "ocr.start" or "cite.render" ? DocumentUri : TaskUri;
        string response = await harness.Handler.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"patchouli.send\",\"arguments\":{\"target\":"
            + JsonSerializer.Serialize(target) + ",\"action\":" + JsonSerializer.Serialize(action) + ",\"args\":" +
            args + ",\"format\":\"json\"}}}");
        ErrorName(response).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"instruction":"start","workflow":"patchouli://workflows/wf"}""")]
    [InlineData("""{"instruction":"message","session":"patchouli://runs/agent/s/","message_id":"m","text":"go"}""")]
    [InlineData(
        """{"target":"patchouli://texts/11111111-1111-1111-1111-111111111111/","action":"ocr.start","args":{},"parameters":{}}""")]
    public async Task Old_send_fields_are_rejected_with_migration_diagnostic(string legacy)
    {
        Harness harness = new();
        using JsonDocument parsed = JsonDocument.Parse(legacy);
        Dictionary<string, object?> args = parsed.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => (object?)property.Value.Clone());
        args["format"] = "json";
        string response = await harness.Tool("patchouli.send", args);
        ErrorName(response).Should().Be("INVALID_ARGUMENT");
        using JsonDocument envelope = Envelope(response);
        envelope.RootElement.GetProperty("message").GetProperty("error").GetString()!.ToLowerInvariant()
            .Should().Contain("target").And.Contain("action");
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_action_is_rejected_without_handler_invocation()
    {
        Harness harness = new();
        string response = await harness.Call("unknown.action", DocumentUri, new { });
        IsError(response).Should().BeTrue();
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"action":"ocr.start","args":{}}""")]
    [InlineData("""{"target":"patchouli://texts/11111111-1111-1111-1111-111111111111/","args":{}}""")]
    [InlineData("""{"target":"patchouli://texts/11111111-1111-1111-1111-111111111111/","action":"ocr.start"}""")]
    public async Task Generic_send_requires_target_action_and_explicit_args_object(string incomplete)
    {
        Harness harness = new();
        using JsonDocument parsed = JsonDocument.Parse(incomplete);
        Dictionary<string, object?> arguments = parsed.RootElement.EnumerateObject().ToDictionary(
            property => property.Name, property => (object?)property.Value.Clone());
        arguments["format"] = "json";
        ErrorName(await harness.Tool("patchouli.send", arguments)).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Generic_send_published_open_args_schema_validates_without_null_reference()
    {
        using JsonDocument arguments = JsonDocument.Parse("""
                                                          {"target":"patchouli://texts/11111111-1111-1111-1111-111111111111/",
                                                           "action":"ocr.start","args":{"pages":"1","engine":"rapidocr"},"format":"json"}
                                                          """);
        McpProtocolHandler.ValidateToolArguments("patchouli.send", arguments.RootElement).Should().BeNull();
    }

    [Fact]
    public async Task Generic_send_with_no_arguments_object_is_rejected_without_internal_error_or_handler_call()
    {
        Harness harness = new();
        string response = await harness.Handler.HandleAsync("""
                                                            {"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"patchouli.send"}}
                                                            """);
        using JsonDocument rpc = JsonDocument.Parse(response);
        rpc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        string text = rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        string json = text.TrimStart().StartsWith('{') ? text : McpToonCodec.DecodeToJson(text);
        using JsonDocument envelope = JsonDocument.Parse(json);
        envelope.RootElement.GetProperty("message").GetProperty("error").GetString().Should()
            .StartWith("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("target", "\"patchouli://texts/11111111-1111-1111-1111-111111111111/\"")]
    [InlineData("action", "\"ocr.start\"")]
    [InlineData("args", "{}")]
    public async Task Generic_send_rejects_duplicate_outer_fields_before_action_handler(string name, string value)
    {
        Harness harness = new();
        string response = await harness.Handler.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"patchouli.send\",\"arguments\":{\"target\":"
            + JsonSerializer.Serialize(DocumentUri) + ",\"action\":\"ocr.start\",\"args\":{},\"format\":\"json\","
            + JsonSerializer.Serialize(name) + ":" + value + "}}}");
        ErrorName(response).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should()
            .BeEmpty("duplicate outer fields must not be silently resolved before registry validation");
    }

    [Fact]
    public async Task Disabling_send_hides_ocr_action_schemas_even_when_domain_actions_are_granted()
    {
        Harness harness = new(false);
        string response = await harness.Tool("patchouli.find", new { @in = "patchouli://actions/", format = "json" });
        IsError(response).Should().BeFalse(response);
        using JsonDocument list = Envelope(response);
        list.RootElement.GetProperty("entries").GetRawText().Should().NotContain("ocr.start")
            .And.NotContain("ocr.cancel").And.NotContain("ocr.retry").And.Contain("cite.render");
        string fetched = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "patchouli://actions/ocr.start" }, format = "json" });
        using JsonDocument denied = Envelope(fetched);
        bool refused = IsError(fetched) || denied.RootElement.GetProperty("entries").EnumerateArray()
            .Any(entry =>
                entry.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null);
        refused.Should().BeTrue("the disabled send switch also protects direct descriptor fetches");
        fetched.Should().NotContain("input_schema").And.NotContain("required_capabilities");
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Registry_discovery_lists_only_authorized_actions_and_direct_fetch_cannot_bypass_policy()
    {
        Harness harness = new(deniedAction: "ocr.retry");
        using JsonDocument list =
            Envelope(await harness.Tool("patchouli.find", new { @in = "patchouli://actions/", format = "json" }));
        string listed = list.RootElement.GetProperty("entries").GetRawText();
        listed.Should().Contain("ocr.start").And.Contain("ocr.cancel").And.NotContain("ocr.retry");
        string denied = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "patchouli://actions/ocr.retry" }, format = "json" });
        using JsonDocument deniedEnvelope = Envelope(denied);
        bool refused = IsError(denied) || deniedEnvelope.RootElement.GetProperty("entries").EnumerateArray()
            .Any(entry =>
                entry.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null);
        refused.Should().BeTrue("guessing an action URI must not bypass discovery authorization");
        denied.Should().NotContain("input_schema").And.NotContain("required_capabilities");
        string allowed = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "patchouli://actions/ocr.start" }, format = "json" });
        IsError(allowed).Should().BeFalse(allowed);
        using JsonDocument fetched = Envelope(allowed);
        string descriptor = fetched.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()!;
        string json = descriptor.TrimStart().StartsWith('{') ? descriptor : McpToonCodec.DecodeToJson(descriptor);
        using JsonDocument schema = JsonDocument.Parse(json);
        schema.RootElement.GetRawText().Should().Contain("ocr.start").And.Contain("request_id").And.Contain("engine");
        harness.Calls.Should().BeEmpty("discovery must never execute an action");
    }

    [Theory]
    [InlineData("ocr.start", "Document", "started", "texts:ocr.start")]
    [InlineData("ocr.cancel", "RunOcrStatus", "completed", "runs:ocr.cancel")]
    [InlineData("ocr.retry", "RunOcrStatus", "started", "runs:ocr.retry")]
    [InlineData("cite.render", "Item", "completed", "refs:cite")]
    public async Task Discoverable_descriptors_expose_target_schema_execution_and_permission_contracts(
        string action, string targetType, string mode, string requiredCapability)
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "patchouli://actions/" + action }, format = "json" });
        IsError(response).Should().BeFalse(response);
        using JsonDocument envelope = Envelope(response);
        using JsonDocument descriptor = JsonDocument.Parse(envelope.RootElement.GetProperty("entries")[0]
            .GetProperty("content").GetString()!);
        JsonElement root = descriptor.RootElement;
        root.GetProperty("action_id").GetString().Should().Be(action);
        root.GetProperty("target_types").EnumerateArray().Select(value => value.GetString()).Should()
            .Contain(targetType);
        root.GetProperty("execution_mode").GetString().Should().Be(mode);
        root.GetProperty("side_effects").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("required_capabilities").EnumerateArray().Select(value => value.GetString())
            .Should().Contain(requiredCapability);
        JsonElement input = root.GetProperty("input_schema");
        input.GetProperty("type").GetString().Should().Be("object");
        input.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        JsonElement properties = input.GetProperty("properties");
        string[] expected = action switch
        {
            "ocr.start" => ["pages", "engine", "request_id"],
            "ocr.cancel" => [],
            "ocr.retry" => ["request_id"],
            _ => ["refs", "style", "locale", "bibliography", "html"]
        };
        properties.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(expected);
        if (action.StartsWith("ocr.", StringComparison.Ordinal))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                property.Value.GetProperty("type").GetString().Should().Be("string");
            }

            JsonElement result = root.GetProperty("result_schema");
            result.GetProperty("type").GetString().Should().Be("object");
            result.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should()
                .Contain(["task_id", "status_uri", "state", "engine_id", "preset_id", "preset_version_id"]);
            result.GetProperty("properties").GetProperty("preset_version_id").GetProperty("type")
                .GetString().Should().Be("string");
        }
        else
        {
            properties.GetProperty("refs").GetProperty("type").GetString().Should().Be("array");
            properties.GetProperty("bibliography").GetProperty("type").GetString().Should().Be("boolean");
            root.GetProperty("result_schema").GetProperty("required").EnumerateArray()
                .Select(value => value.GetString()).Should().Contain(["meta", "entries"]);
        }

        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_actions_scope_returns_an_authorized_descriptor_array_without_executing_actions()
    {
        Harness harness = new(deniedAction: "ocr.retry");
        string response = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "patchouli://actions/" }, format = "json" });
        IsError(response).Should().BeFalse(response);
        using JsonDocument envelope = Envelope(response);
        using JsonDocument descriptors = JsonDocument.Parse(envelope.RootElement.GetProperty("entries")[0]
            .GetProperty("content").GetString()!);
        descriptors.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        descriptors.RootElement.EnumerateArray().Select(value => value.GetProperty("action_id").GetString())
            .Should().BeEquivalentTo(["ocr.start", "ocr.cancel", "cite.render"]);
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AppErrorCodes.InvalidArgument, "INVALID_ARGUMENT")]
    [InlineData(AppErrorCodes.NotFound, "NOT_FOUND")]
    [InlineData(AppErrorCodes.Conflict, "REVISION_CONFLICT")]
    [InlineData("permission_denied", "PERMISSION_DENIED")]
    [InlineData("operation_cancelled", "CANCELLED")]
    public async Task Domain_failures_keep_stable_public_error_names(string code, string expected)
    {
        Harness harness = new();
        harness.CommandResult = Result<McpOcrCommandResult>.Failure(code, "Independent domain failure.");
        ErrorName(await harness.Call("ocr.start", DocumentUri, new { })).Should().Be(expected);
        harness.Calls.Should().Equal("start");
    }

    [Fact]
    public async Task Domain_error_boundary_sanitizes_paths_and_secrets()
    {
        Harness harness = new();
        harness.CommandResult = Result<McpOcrCommandResult>.Failure(AppErrorCodes.UnsupportedOperation,
            "Unavailable model C:\\private\\models\\ocr.onnx; cache=/private/cache; api_key=FAKE_SECRET_INDEPENDENT");
        string response = await harness.Call("ocr.start", DocumentUri, new { });
        ErrorName(response).Should().Be("UNAVAILABLE");
        response.Should().NotContain("FAKE_SECRET_INDEPENDENT").And.NotContain("private").And.NotContain("ocr.onnx");
    }

    private static bool IsError(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        return json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();
    }

    private static JsonDocument Envelope(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        return JsonDocument.Parse(json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text")
            .GetString()!);
    }

    private static string ErrorName(string response)
    {
        IsError(response).Should().BeTrue(response);
        using JsonDocument envelope = Envelope(response);
        return envelope.RootElement.GetProperty("message").GetProperty("error").GetString()!.Split(" [", 2,
            StringSplitOptions.None)[0];
    }

    private sealed class Harness
    {
        public readonly List<string> Calls = [];
        public readonly McpProtocolHandler Handler;
        public McpOcrStartRequest? LastStart;
        public OcrQueueTaskId LastTask;
        public string? LastRetryRequest;
        public Result<McpOcrCommandResult>? CommandResult;

        public Harness(bool sendEnabled = true, string? deniedAction = null, bool noActionGrants = false)
        {
            Stub<IMcpReadApi> read = Stub<IMcpReadApi>.Create();
            read.Handler = (method, _) => method == nameof(IMcpReadApi.GetCurrentLibraryStateAsync)
                ? Task.FromResult(
                    Result<McpLibraryStateResponse>.Success(new McpLibraryStateResponse("independent", "lib:1")))
                : throw new NotSupportedException(method);
            Stub<IMcpOcrCommandApi> commands = Stub<IMcpOcrCommandApi>.Create();
            commands.Handler = (method, args) =>
            {
                Calls.Add(method.Replace("Async", "", StringComparison.Ordinal).ToLowerInvariant());
                if (method == nameof(IMcpOcrCommandApi.StartAsync))
                {
                    LastStart = (McpOcrStartRequest)args[0]!;
                }
                else
                {
                    LastTask = (OcrQueueTaskId)args[0]!;
                    if (method == nameof(IMcpOcrCommandApi.RetryAsync))
                    {
                        LastRetryRequest = (string?)args[1];
                    }
                }

                return Task.FromResult(CommandResult ?? Result<McpOcrCommandResult>.Success(new McpOcrCommandResult(
                    TaskId, TaskUri + "status", "queued", "rapidocr", "preset-fixture",
                    "33333333-3333-3333-3333-333333333333")));
            };
            string[] tools = ["find", "fetch", "put", "cite", "send", "add", "view"];
            McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                ToolOverrides =
                tools.Select(tool => new McpToolOverride("patchouli." + tool, tool != "send" || sendEnabled)).ToArray(),
                DomainPermissions = noActionGrants
                    ? [new McpDomainPermission("texts", "send", true), new McpDomainPermission("runs", "send", true)]
                    :
                    [
                        new McpDomainPermission("texts", "ocr.start", deniedAction != "ocr.start"),
                        new McpDomainPermission("runs", "ocr.cancel", deniedAction != "ocr.cancel"),
                        new McpDomainPermission("runs", "ocr.retry", deniedAction != "ocr.retry")
                    ]
            };
            Handler = new McpProtocolHandler(read.Value, Stub<IMcpWriteApi>.Create().Value,
                Stub<IBiblatexImportService>.Create().Value, Stub<IItemService>.Create().Value,
                Stub<IVersionedEvidenceReader>.Create().Value,
                new SqliteConnectionFactory(
                    Path.Combine(Path.GetTempPath(), $"action-unused-{Guid.NewGuid():N}.sqlite")),
                settings, ocrCommands: commands.Value);
        }

        public Task<string> Call(string action, string target, object args)
        {
            return Tool("patchouli.send", new { target, action, args, format = "json" });
        }

        public Task<string> Tool(string name, object arguments)
        {
            return Handler.HandleAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 42, method = "tools/call", @params = new { name, arguments }
            }));
        }
    }

    private class Stub<T> : DispatchProxy where T : class
    {
        public Func<string, object?[], object?> Handler { get; set; } =
            (method, _) => throw new NotSupportedException(method);

        public T Value => (T)(object)this;

        public static Stub<T> Create()
        {
            return (Stub<T>)(object)Create<T, Stub<T>>();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod!.Name, args ?? []);
        }
    }
}
