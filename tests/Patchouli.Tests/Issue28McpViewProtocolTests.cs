using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.View;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Tests;

/// <summary>
/// Issue #28 independent tests: the <c>boxes</c> argument of patchouli.view across
/// the three isomorphic surfaces — the MCP command service (McpViewRequest →
/// PageImageViewRequest.NumberedBoxes and the result metadata), the JSON-RPC
/// protocol handler (argument whitelist, boolean parsing, tool schema), and the
/// CLI flag mapping. Also pins the regression contract: without boxes, view
/// metadata carries no "boxes" field at all.
/// </summary>
public sealed class Issue28McpViewProtocolTests
{
    private const string DocId = "11111111-1111-1111-1111-111111111111";
    private const string PageUri = "patchouli://texts/" + DocId + "/page-1.xml";
    private static readonly DocumentBoxId BoxOne = DocumentBoxId.New();
    private static readonly DocumentBoxId BoxTwo = DocumentBoxId.New();
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ---------- command service ----------

    [Fact]
    public async Task View_boxes_true_reaches_the_service_and_maps_metadata()
    {
        (Stub<IPageImageViewService> pageView, List<PageImageViewRequest> seen) = PageViewStub();
        McpCommandService service = CreateService(pageView.Proxy);

        McpViewCommandResult result = await service.ViewAsync(new McpViewRequest(PageUri, Boxes: true));

        result.Result.IsSuccess.Should().BeTrue();
        seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeTrue();
        McpViewImageMetadata metadata = result.Result.Envelope!.Entries.Should().ContainSingle().Subject;
        metadata.Boxes.Should().NotBeNull();
        metadata.Boxes!.Select(box => (box.Index, box.BoxId))
            .Should().Equal((1, BoxOne.ToString()), (2, BoxTwo.ToString()));
        string json = JsonSerializer.Serialize(metadata);
        json.Should().Contain("\"boxes\"").And.Contain("\"box_id\"");
    }

    [Fact]
    public async Task View_without_boxes_keeps_metadata_free_of_the_boxes_field()
    {
        (Stub<IPageImageViewService> pageView, List<PageImageViewRequest> seen) = PageViewStub();
        McpCommandService service = CreateService(pageView.Proxy);

        McpViewCommandResult result = await service.ViewAsync(new McpViewRequest(PageUri));

        result.Result.IsSuccess.Should().BeTrue();
        seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeFalse();
        McpViewImageMetadata metadata = result.Result.Envelope!.Entries.Should().ContainSingle().Subject;
        metadata.Boxes.Should().BeNull();
        JsonSerializer.Serialize(metadata).Should().NotContain("boxes");
    }

    // ---------- protocol handler ----------

    [Fact]
    public async Task Protocol_view_boxes_true_is_forwarded_and_answered_with_the_mapping()
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.view",
            new { uri = PageUri, boxes = true, format = "json" });

        IsError(response).Should().BeFalse(response);
        harness.Seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeTrue();
        using JsonDocument envelope = Envelope(response);
        JsonElement metadata = envelope.RootElement.GetProperty("entries")[0];
        List<(int, string)> boxes = metadata.GetProperty("boxes").EnumerateArray()
            .Select(element => (element.GetProperty("index").GetInt32(),
                element.GetProperty("box_id").GetString()!)).ToList();
        boxes.Should().Equal((1, BoxOne.ToString()), (2, BoxTwo.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protocol_view_boxes_absent_or_false_means_no_overlay(bool explicitFalse)
    {
        Harness harness = new();
        object arguments = explicitFalse
            ? new { uri = PageUri, boxes = false, format = "json" }
            : new { uri = PageUri, format = "json" };
        string response = await harness.Tool("patchouli.view", arguments);

        IsError(response).Should().BeFalse(response);
        harness.Seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeFalse();
        using JsonDocument envelope = Envelope(response);
        envelope.RootElement.GetProperty("entries")[0].TryGetProperty("boxes", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Protocol_view_rejects_a_non_boolean_boxes_argument()
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.view",
            new { uri = PageUri, boxes = "yes", format = "json" });

        IsError(response).Should().BeTrue(response);
        harness.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Protocol_view_rejects_unknown_arguments()
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.view",
            new { uri = PageUri, boxez = true, format = "json" });

        IsError(response).Should().BeTrue(response);
        harness.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Protocol_tools_list_advertises_the_boxes_argument()
    {
        Harness harness = new();
        string response = await harness.Handler.HandleAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 7, method = "tools/list", @params = new { }
        }));

        using JsonDocument json = JsonDocument.Parse(response);
        json.RootElement.TryGetProperty("error", out _).Should().BeFalse(response);
        JsonElement view = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "patchouli.view");
        JsonElement properties = view.GetProperty("inputSchema").GetProperty("properties");
        properties.TryGetProperty("boxes", out JsonElement boxes).Should().BeTrue();
        boxes.GetProperty("type").GetString().Should().Be("boolean");
    }

    // ---------- CLI isomorphism ----------

    [Fact]
    public void Cli_view_boxes_flag_maps_to_the_tool_argument()
    {
        CliToolCall call = CliArguments.BuildToolCall("view",
            [PageUri, "--output", "out.png", "--boxes"], false);

        call.Tool.Should().Be(CliArguments.View);
        call.Arguments["boxes"].Should().Be(true);
    }

    [Fact]
    public void Cli_view_without_boxes_flag_omits_the_argument()
    {
        CliToolCall call = CliArguments.BuildToolCall("view",
            [PageUri, "--output", "out.png", "--max-edge", "512"], false);

        call.Arguments.Should().NotContainKey("boxes");
        call.Arguments["max_edge"].Should().Be(512);
    }

    // ---------- harness ----------

    private static (Stub<IPageImageViewService> Stub, List<PageImageViewRequest> Seen) PageViewStub()
    {
        (IPageImageViewService _, Stub<IPageImageViewService> stub) = Stub<IPageImageViewService>.Create();
        List<PageImageViewRequest> seen = [];
        stub.Handle(nameof(IPageImageViewService.ViewAsync), args =>
        {
            PageImageViewRequest request = (PageImageViewRequest)args[0]!;
            seen.Add(request);
            PageImageViewResult image = new(
                PageUri + "?rev=rev-1", PageImageViewTypes.Page, "image/png", 800, 600,
                new NormalizedBBox(0, 0, 1, 1), 0, "rev-1", FakePng,
                request.NumberedBoxes
                    ? [new PageImageBoxNumber(1, BoxOne), new PageImageBoxNumber(2, BoxTwo)]
                    : null);
            return Task.FromResult(Result<PageImageViewResult>.Success(image));
        });
        return (stub, seen);
    }

    private static McpCommandService CreateService(IPageImageViewService pageView)
    {
        (_, Stub<IMcpReadApi> read) = Stub<IMcpReadApi>.Create();
        read.Handle(nameof(IMcpReadApi.GetCurrentLibraryStateAsync), _ =>
            Task.FromResult(Result<McpLibraryStateResponse>.Success(
                new McpLibraryStateResponse("lib", "lib:99"))));
        (_, Stub<IMcpWriteApi> write) = Stub<IMcpWriteApi>.Create();
        (_, Stub<IBiblatexImportService> biblatex) = Stub<IBiblatexImportService>.Create();
        (_, Stub<IItemService> items) = Stub<IItemService>.Create();
        (_, Stub<IVersionedEvidenceReader> evidence) = Stub<IVersionedEvidenceReader>.Create();
        return new McpCommandService(read.Proxy, write.Proxy, biblatex.Proxy, items.Proxy,
            evidence.Proxy, pageView: pageView);
    }

    private static bool IsError(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        return json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();
    }

    private static JsonDocument Envelope(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        return JsonDocument.Parse(json.RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!);
    }

    private sealed class Harness
    {
        public Harness()
        {
            (Stub<IPageImageViewService> pageView, List<PageImageViewRequest> seen) = PageViewStub();
            Seen = seen;
            (_, Stub<IMcpReadApi> read) = Stub<IMcpReadApi>.Create();
            read.Handle(nameof(IMcpReadApi.GetCurrentLibraryStateAsync), _ =>
                Task.FromResult(Result<McpLibraryStateResponse>.Success(
                    new McpLibraryStateResponse("test", "lib:1"))));
            (_, Stub<IMcpWriteApi> write) = Stub<IMcpWriteApi>.Create();
            (_, Stub<IBiblatexImportService> biblatex) = Stub<IBiblatexImportService>.Create();
            (_, Stub<IItemService> items) = Stub<IItemService>.Create();
            (_, Stub<IVersionedEvidenceReader> evidence) = Stub<IVersionedEvidenceReader>.Create();
            string[] tools = ["find", "fetch", "put", "cite", "send", "add", "view"];
            McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                ToolOverrides = tools.Select(tool => new McpToolOverride("patchouli." + tool, true)).ToArray()
            };
            Handler = new McpProtocolHandler(read.Proxy, write.Proxy, biblatex.Proxy, items.Proxy,
                evidence.Proxy,
                new SqliteConnectionFactory(
                    Path.Combine(Path.GetTempPath(), $"issue28-view-{Guid.NewGuid():N}.sqlite")),
                settings, pageView: pageView.Proxy);
        }

        public McpProtocolHandler Handler { get; }
        public List<PageImageViewRequest> Seen { get; }

        public Task<string> Tool(string name, object arguments)
        {
            return Handler.HandleAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 42, method = "tools/call", @params = new { name, arguments }
            }));
        }
    }

    /// <summary>Minimal DispatchProxy stub: unhandled methods throw loudly.</summary>
    private class Stub<T> : DispatchProxy where T : class
    {
        private readonly Dictionary<string, Func<object?[], object?>> _handlers = new();

        public T Proxy => (T)(object)this;

        public void Handle(string methodName, Func<object?[], object?> handler)
        {
            _handlers[methodName] = handler;
        }

        public static (T Proxy, Stub<T> Impl) Create()
        {
            T proxy = Create<T, Stub<T>>();
            return (proxy, (Stub<T>)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null &&
                _handlers.TryGetValue(targetMethod.Name, out Func<object?[], object?>? handler))
            {
                return handler(args ?? []);
            }

            throw new NotSupportedException(
                $"Stub method not configured: {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
        }
    }
}
