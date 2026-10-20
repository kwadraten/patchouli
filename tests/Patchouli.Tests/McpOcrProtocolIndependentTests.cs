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
    public async Task Start_returns_receipt_and_preserves_flat_request_identity()
    {
        Harness harness = new();
        string response = await harness.Call("start", DocumentUri, new { pages = "1,3-5", engine = "rapidocr" },
            "stable-request");
        IsError(response).Should().BeFalse(response);
        harness.LastStart.Should()
            .Be(new McpOcrStartRequest(DocumentInstanceId.Parse(DocumentId), "1,3-5", "rapidocr", "stable-request"));
        using JsonDocument envelope = Envelope(response);
        envelope.RootElement.GetProperty("meta").GetProperty("endpoint").GetString().Should()
            .Be("patchouli://runs/ocr/control");
        JsonElement entry = envelope.RootElement.GetProperty("entries")[0];
        entry.GetProperty("state").GetString().Should().Be("started");
        entry.GetProperty("run_uri").GetString().Should().Be(TaskUri + "status");
        entry.GetProperty("result").GetProperty("state").GetString().Should().Be("queued");
        harness.Calls.Should().Equal("start");
    }

    [Theory]
    [InlineData("cancel", "completed")]
    [InlineData("retry", "started")]
    public async Task Task_messages_accept_instance_targets_and_distinguish_receipts(string action, string state)
    {
        Harness harness = new();
        string response = await harness.Call(action, TaskUri, new { }, action == "retry" ? "retry-key" : null);
        IsError(response).Should().BeFalse(response);
        harness.LastTask.Should().Be(OcrQueueTaskId.Parse(TaskId));
        using JsonDocument envelope = Envelope(response);
        JsonElement entry = envelope.RootElement.GetProperty("entries")[0];
        entry.GetProperty("state").GetString().Should().Be(state);
        if (action == "retry")
        {
            harness.LastRetryRequest.Should().Be("retry-key");
        }
        else
        {
            entry.TryGetProperty("run_uri", out _).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("start", TaskUri)]
    [InlineData("start", "patchouli://texts/11111111-1111-1111-1111-111111111111/page-1.xml")]
    [InlineData("cancel", DocumentUri)]
    [InlineData("retry", "patchouli://runs/translation/22222222-2222-2222-2222-222222222222/")]
    [InlineData("cancel", "runs/ocr/not-a-uuid/")]
    [InlineData("ocr.start", DocumentUri)]
    [InlineData("unknown", DocumentUri)]
    public async Task Wrong_action_or_target_never_reaches_handler(string action, string target)
    {
        Harness harness = new();
        ErrorName(await harness.Call(action, target, new { })).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("start", "{\"pages\":2}")]
    [InlineData("start", "{\"engine\":false}")]
    [InlineData("start", "{\"engine\":\"\"}")]
    [InlineData("start", "{\"engine\":\"mock\",\"engine\":\"rapidocr\"}")]
    [InlineData("start", "{\"request_id\":\"nested\"}")]
    [InlineData("start", "{\"preset\":\"p\"}")]
    [InlineData("cancel", "{\"pages\":\"1\"}")]
    [InlineData("retry", "{\"engine\":\"mock\"}")]
    [InlineData("start", "null")]
    [InlineData("start", "[]")]
    public async Task Message_schema_rejects_bad_arguments(string action, string json)
    {
        Harness harness = new();
        using JsonDocument args = JsonDocument.Parse(json);
        ErrorName(await harness.Call(action, action == "start" ? DocumentUri : TaskUri, args.RootElement)).Should()
            .Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("endpoint", "\"runs/translation/control\"")]
    [InlineData("action", "\"cancel\"")]
    [InlineData("target", "\"runs/ocr/task/\"")]
    [InlineData("args", "{}")]
    [InlineData("request_id", "\"another\"")]
    public async Task Duplicate_outer_fields_are_rejected(string field, string value)
    {
        Harness harness = new();
        string json =
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"patchouli.send\",\"arguments\":{\"endpoint\":\"runs/ocr/control\",\"action\":\"start\",\"target\":" +
            JsonSerializer.Serialize(DocumentUri) + ",\"args\":{},\"request_id\":\"first\",\"format\":\"json\",\"" +
            field + "\":" + value + "}}}";
        ErrorName(await harness.Handler.HandleAsync(json)).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("action")]
    [InlineData("target")]
    [InlineData("args")]
    public async Task Required_fields_cannot_be_omitted(string field)
    {
        Harness harness = new();
        Dictionary<string, object> message = new()
        {
            ["endpoint"] = "runs/ocr/control", ["action"] = "start", ["target"] = DocumentUri, ["args"] = new { },
            ["format"] = "json"
        };
        message.Remove(field);
        ErrorName(await harness.Tool("patchouli.send", message)).Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_requests_are_explicitly_rejected()
    {
        Harness harness = new();
        ErrorName(await harness.Tool("patchouli.send",
                new { target = DocumentUri, action = "ocr.start", args = new { }, format = "json" })).Should()
            .Be("INVALID_ARGUMENT");
        ErrorName(await harness.Tool("patchouli.send", new { instruction = "start", workflow = "wf", format = "json" }))
            .Should().Be("INVALID_ARGUMENT");
        harness.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Global_switch_and_child_grant_are_both_required(bool sendEnabled, bool noGrants)
    {
        Harness harness = new(sendEnabled, noActionGrants: noGrants);
        ErrorName(await harness.Call("start", DocumentUri, new { })).Should().Be("PERMISSION_DENIED");
        harness.Calls.Should().BeEmpty();
        string listed = await harness.Tool("patchouli.find", new { @in = "runs/ocr/", format = "json" });
        using JsonDocument list = Envelope(listed);
        list.RootElement.GetProperty("entries").GetRawText().Should().NotContain("control");
        string fetched = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "runs/ocr/control" }, format = "json" });
        fetched.Should().NotContain("input_schema");
    }

    [Fact]
    public async Task Endpoint_discovery_exposes_all_domain_messages_and_never_executes_them()
    {
        Harness harness = new();
        string listed = await harness.Tool("patchouli.find", new { @in = "runs/ocr/", format = "json" });
        IsError(listed).Should().BeFalse(listed);
        using JsonDocument list = Envelope(listed);
        list.RootElement.GetProperty("entries")[0].GetProperty("uri").GetString().Should()
            .Be("patchouli://runs/ocr/control");
        string fetched = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "runs/ocr/control" }, format = "json" });
        IsError(fetched).Should().BeFalse(fetched);
        using JsonDocument envelope = Envelope(fetched);
        using JsonDocument schema =
            JsonDocument.Parse(envelope.RootElement.GetProperty("entries")[0].GetProperty("content").GetString()!);
        schema.RootElement.GetProperty("messages").EnumerateArray()
            .Select(value => value.GetProperty("action").GetString())
            .Should().BeEquivalentTo(["start", "cancel", "retry"]);
        foreach (JsonElement message in schema.RootElement.GetProperty("messages").EnumerateArray())
        {
            message.GetProperty("required_capabilities")[0].GetString().Should().Be("runs/ocr:send");
            message.GetProperty("input_schema").GetProperty("properties").TryGetProperty("request_id", out _).Should()
                .BeFalse();
        }

        harness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Endpoint_schema_is_complete_JSON_or_an_explicit_size_error()
    {
        Harness harness = new();
        string complete = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "runs/ocr/control" }, format = "json", limit_bytes = 65536 });
        IsError(complete).Should().BeFalse(complete);
        using JsonDocument envelope = Envelope(complete);
        JsonElement entry = envelope.RootElement.GetProperty("entries")[0];
        entry.GetProperty("content_type").GetString().Should().Be("application/json");
        using JsonDocument schema = JsonDocument.Parse(entry.GetProperty("content").GetString()!);
        schema.RootElement.GetProperty("messages").GetArrayLength().Should().Be(3);
        string bounded = await harness.Tool("patchouli.fetch",
            new { uris = new[] { "runs/ocr/control" }, format = "json", limit_bytes = 1024 });
        using JsonDocument limited = Envelope(bounded);
        limited.RootElement.GetProperty("entries")[0].GetProperty("error").GetString().Should()
            .StartWith("RESPONSE_TRUNCATED");
        bounded.Should().NotContain("input_schema");
    }

    [Theory]
    [InlineData(AppErrorCodes.NotFound, "NOT_FOUND")]
    [InlineData(AppErrorCodes.Conflict, "REVISION_CONFLICT")]
    [InlineData(AppErrorCodes.UnsupportedOperation, "UNAVAILABLE")]
    public async Task Domain_failures_preserve_codes_and_sanitize_details(string code, string expected)
    {
        Harness harness = new();
        harness.CommandResult = Result<McpOcrCommandResult>.Failure(code,
            "Unavailable model C:\\private\\models\\ocr.onnx; cache=/private/cache; api_key=FAKE_SECRET_INDEPENDENT");
        string response = await harness.Call("start", DocumentUri, new { });
        ErrorName(response).Should().Be(expected);
        response.Should().NotContain("FAKE_SECRET_INDEPENDENT").And.NotContain("private");
    }

    [Fact]
    public async Task Surface_keeps_seven_tools_and_publishes_flat_fields()
    {
        Harness harness = new();
        using JsonDocument list =
            JsonDocument.Parse(
                await harness.Handler.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"));
        JsonElement tools = list.RootElement.GetProperty("result").GetProperty("tools");
        tools.GetArrayLength().Should().Be(7);
        JsonElement send = tools.EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "patchouli.send");
        send.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo(["endpoint", "action", "target", "args"]);
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
                        new McpDomainPermission("runs/ocr", "send", deniedAction is null)
                    ]
            };
            Handler = new McpProtocolHandler(read.Value, Stub<IMcpWriteApi>.Create().Value,
                Stub<IBiblatexImportService>.Create().Value, Stub<IItemService>.Create().Value,
                Stub<IVersionedEvidenceReader>.Create().Value,
                new SqliteConnectionFactory(
                    Path.Combine(Path.GetTempPath(), $"action-unused-{Guid.NewGuid():N}.sqlite")),
                settings, ocrCommands: commands.Value);
        }

        public Task<string> Call(string action, string target, object args, string? requestId = null)
        {
            Dictionary<string, object> message = new()
            {
                ["endpoint"] = "runs/ocr/control", ["target"] = target, ["action"] = action, ["args"] = args,
                ["format"] = "json"
            };
            if (requestId is not null)
            {
                message["request_id"] = requestId;
            }

            return Tool("patchouli.send", message);
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
