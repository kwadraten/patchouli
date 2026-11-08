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
/// Issue #28 independent tests: the <c>?overlay=boxes</c> query of the images/
/// domain across the three isomorphic surfaces — the MCP command service
/// (fetch of an images/ URI → PageImageViewRequest.NumberedBoxes and the result
/// image metadata), the JSON-RPC protocol handler (fetch schema, the native image
/// content block, unknown-argument rejection), and the CLI flag mapping
/// (fetch --output). Also pins the regression contract: without overlay, image
/// metadata carries no "boxes" field at all, and the retired patchouli.view tool
/// no longer appears in tools/list.
/// </summary>
public sealed class Issue28McpFetchImageProtocolTests
{
    private const string DocId = "11111111-1111-1111-1111-111111111111";
    private const string RevId = "22222222-2222-2222-2222-222222222222";
    private const string ImageUri = "patchouli://images/" + DocId + "/page-1.png";
    private static readonly DocumentBoxId BoxOne = DocumentBoxId.New();
    private static readonly DocumentBoxId BoxTwo = DocumentBoxId.New();
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ---------- command service ----------

    [Fact]
    public async Task Fetch_overlay_boxes_reaches_the_service_and_maps_metadata()
    {
        (Stub<IPageImageViewService> pageView, List<PageImageViewRequest> seen) = PageViewStub();
        McpCommandService service = CreateService(pageView.Proxy);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await service.FetchAsync(
            new McpFetchRequest([ImageUri + "?overlay=boxes"], null, null));

        result.IsSuccess.Should().BeTrue();
        seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeTrue();
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ImageBytes.Should().BeEquivalentTo(FakePng);
        entry.Uri.Should().Be(ImageUri + "?rev=" + RevId);
        McpFetchImageMetadata metadata = entry.Image!;
        metadata.Boxes.Should().NotBeNull();
        metadata.Boxes!.Select(box => (box.Index, box.BoxId))
            .Should().Equal((1, BoxOne.ToString()), (2, BoxTwo.ToString()));
        string json = JsonSerializer.Serialize(metadata);
        json.Should().Contain("\"boxes\"").And.Contain("\"box_id\"");
    }

    [Fact]
    public async Task Fetch_without_overlay_keeps_metadata_free_of_the_boxes_field()
    {
        (Stub<IPageImageViewService> pageView, List<PageImageViewRequest> seen) = PageViewStub();
        McpCommandService service = CreateService(pageView.Proxy);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await service.FetchAsync(
            new McpFetchRequest([ImageUri], null, null));

        result.IsSuccess.Should().BeTrue();
        seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeFalse();
        McpFetchImageMetadata metadata =
            result.Envelope!.Entries.Should().ContainSingle().Subject.Image!;
        metadata.Boxes.Should().BeNull();
        JsonSerializer.Serialize(metadata).Should().NotContain("boxes");
    }

    // ---------- protocol handler ----------

    [Fact]
    public async Task Protocol_fetch_overlay_boxes_is_forwarded_and_answered_with_the_mapping()
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.fetch",
            new { uris = new[] { ImageUri + "?overlay=boxes" }, format = "json" });

        IsError(response).Should().BeFalse(response);
        harness.Seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeTrue();
        using JsonDocument envelope = Envelope(response);
        JsonElement metadata = envelope.RootElement.GetProperty("entries")[0].GetProperty("image");
        List<(int, string)> boxes = metadata.GetProperty("boxes").EnumerateArray()
            .Select(element => (element.GetProperty("index").GetInt32(),
                element.GetProperty("box_id").GetString()!)).ToList();
        boxes.Should().Equal((1, BoxOne.ToString()), (2, BoxTwo.ToString()));
        // The image itself is a native image content block, not text.
        using JsonDocument json = JsonDocument.Parse(response);
        JsonElement content = json.RootElement.GetProperty("result").GetProperty("content");
        content.GetArrayLength().Should().Be(2);
        content[1].GetProperty("type").GetString().Should().Be("image");
        content[1].GetProperty("mimeType").GetString().Should().Be("image/png");
        content[1].GetProperty("data").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Protocol_fetch_without_overlay_means_no_overlay_but_still_returns_the_image()
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.fetch",
            new { uris = new[] { ImageUri }, format = "json" });

        IsError(response).Should().BeFalse(response);
        harness.Seen.Should().ContainSingle().Which.NumberedBoxes.Should().BeFalse();
        using JsonDocument envelope = Envelope(response);
        envelope.RootElement.GetProperty("entries")[0].GetProperty("image")
            .TryGetProperty("boxes", out _).Should().BeFalse();
        using JsonDocument json = JsonDocument.Parse(response);
        JsonElement content = json.RootElement.GetProperty("result").GetProperty("content");
        content.GetArrayLength().Should().Be(2);
        content[1].GetProperty("type").GetString().Should().Be("image");
    }

    [Fact]
    public async Task Protocol_fetch_rejects_boxes_as_a_tool_argument()
    {
        // Overlay is a URI query parameter now, not a tool argument.
        Harness harness = new();
        string response = await harness.Tool("patchouli.fetch",
            new { uris = new[] { ImageUri }, boxes = true, format = "json" });

        IsError(response).Should().BeTrue(response);
        harness.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Protocol_fetch_rejects_unknown_arguments()
    {
        Harness harness = new();
        string response = await harness.Tool("patchouli.fetch",
            new { uris = new[] { ImageUri }, boxez = true, format = "json" });

        IsError(response).Should().BeTrue(response);
        harness.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Protocol_tools_list_has_no_view_tool_and_fetch_mentions_images()
    {
        Harness harness = new();
        string response = await harness.Handler.HandleAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 7, method = "tools/list", @params = new { }
        }));

        using JsonDocument json = JsonDocument.Parse(response);
        json.RootElement.TryGetProperty("error", out _).Should().BeFalse(response);
        JsonElement[] tools = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        tools.Select(tool => tool.GetProperty("name").GetString()).Should().NotContain("patchouli.view");
        JsonElement fetch = tools.Single(tool => tool.GetProperty("name").GetString() == "patchouli.fetch");
        JsonElement uris = fetch.GetProperty("inputSchema").GetProperty("properties").GetProperty("uris");
        uris.GetProperty("description").GetString().Should().Contain("images/");
    }

    // ---------- CLI isomorphism ----------

    [Fact]
    public void Cli_fetch_images_uri_with_output_maps_to_the_tool_call()
    {
        CliToolCall call = CliArguments.BuildToolCall("fetch",
            [ImageUri, "--output", "out.png"], false);

        call.Tool.Should().Be(CliArguments.Fetch);
        call.FetchOutputPath.Should().Be("out.png");
        ((string[])call.Arguments["uris"]!).Should().Equal(ImageUri);
    }

    [Fact]
    public void Cli_fetch_images_uri_without_output_is_a_usage_error()
    {
        Action act = () => CliArguments.BuildToolCall("fetch", [ImageUri], false);

        act.Should().Throw<CliUsageException>()
            .WithMessage("*--output*");
    }

    [Fact]
    public void Cli_fetch_text_uri_needs_no_output()
    {
        CliToolCall call = CliArguments.BuildToolCall("fetch",
            ["patchouli://library.yml"], false);

        call.Tool.Should().Be(CliArguments.Fetch);
        call.FetchOutputPath.Should().BeNull();
    }

    [Fact]
    public void Cli_view_command_points_at_fetch()
    {
        Action act = () => CliArguments.BuildToolCall("view", [ImageUri], false);

        act.Should().Throw<CliUsageException>()
            .WithMessage("*fetch patchouli://images/*");
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
                ImageUri + "?rev=" + RevId, PageImageViewTypes.Page, "image/png", 800, 600,
                new NormalizedBBox(0, 0, 1, 1), 0, RevId, FakePng,
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
        read.Handle(nameof(IMcpReadApi.GetItemIdForDocumentAsync), _ =>
            Task.FromResult(Result<ItemId>.Failure("fake", "x")));
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
            read.Handle(nameof(IMcpReadApi.GetItemIdForDocumentAsync), _ =>
                Task.FromResult(Result<ItemId>.Failure("fake", "x")));
            (_, Stub<IMcpWriteApi> write) = Stub<IMcpWriteApi>.Create();
            (_, Stub<IBiblatexImportService> biblatex) = Stub<IBiblatexImportService>.Create();
            (_, Stub<IItemService> items) = Stub<IItemService>.Create();
            (_, Stub<IVersionedEvidenceReader> evidence) = Stub<IVersionedEvidenceReader>.Create();
            string[] tools = ["find", "fetch", "put", "cite", "send", "add"];
            McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                ToolOverrides = tools.Select(tool => new McpToolOverride("patchouli." + tool, true)).ToArray()
            };
            Handler = new McpProtocolHandler(read.Proxy, write.Proxy, biblatex.Proxy, items.Proxy,
                evidence.Proxy,
                new SqliteConnectionFactory(
                    Path.Combine(Path.GetTempPath(), $"issue28-fetch-images-{Guid.NewGuid():N}.sqlite")),
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
