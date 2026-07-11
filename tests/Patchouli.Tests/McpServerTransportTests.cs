using System.Net.Sockets;
using FluentAssertions;
using System.Text.Json;
using Patchouli.Core.Mcp;
using Patchouli.Core.Time;
using Patchouli.McpServer;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;

namespace Patchouli.Tests;

public sealed class McpServerTransportTests
{
    [Fact]
    public void Sanitizer_redacts_paths_file_urls_cache_and_secrets()
    {
        string value = "file:///Users/test/cache/render.png api_key=FAKE_SECRET model_path=/bin/tool";
        string safe = McpOutputSanitizer.Sanitize(value);
        safe.Should().NotContain("/Users").And.NotContain("FAKE_SECRET").And.NotContain("/bin/tool");
    }

    [Fact]
    public void Sanitizer_identifies_safe_text()
    {
        McpOutputSanitizer.IsSafe("ordinary scholarly text").Should().BeTrue();
    }

    [Fact]
    public void Sanitizer_preserves_json_escaped_chinese_text()
    {
        string value = "{\\\"text\\\":\\\"\\u534e\\u5317\\u89e3\\u653e\\u533a\\\"}";
        McpOutputSanitizer.Sanitize(value).Should().Be(value);
    }

    [Fact]
    public async Task Initialize_reports_current_build_version()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string response = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        response.Should().Contain(Core.BuildInfo.Version);
    }

    [Fact]
    public void Server_options_default_to_http_port_4536()
    {
        McpServerOptionsParseResult options = McpServerOptions.Parse(["--db", "runtime.sqlite"]);
        options.IsFailure.Should().BeFalse();
        options.Value.Port.Should().Be(4536);
        options.Value.DatabasePath.Should().Be("runtime.sqlite");
    }

    [Fact]
    public void Server_options_accept_custom_http_port()
    {
        McpServerOptionsParseResult options = McpServerOptions.Parse(["--db", "runtime.sqlite", "--port", "4540"]);
        options.IsFailure.Should().BeFalse();
        options.Value.Port.Should().Be(4540);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void Server_options_reject_invalid_http_ports(string port)
    {
        string[] args = port == "--port"
            ? new[] { "--db", "runtime.sqlite", "--port" }
            : ["--db", "runtime.sqlite", "--port", port];
        McpServerOptions.Parse(args).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Http_server_counts_active_and_total_connections()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        int port = GetFreeTcpPort();
        McpHttpServer server = new(new McpProtocolHandler(new FakeApi(), new SqliteConnectionFactory(path)), port);
        try
        {
            await server.StartAsync();
            server.ActiveConnectionCount.Should().Be(0);
            server.TotalConnectionCount.Should().Be(0);
            using (HttpClient http = new())
            {
                string health = await http.GetStringAsync(HealthEndpoint(server.Endpoint));
                health.Should().Contain("ok");
                server.ActiveConnectionCount.Should().BeGreaterThan(0);
                server.TotalConnectionCount.Should().BeGreaterThan(0);
            }

            await WaitForAsync(() => server.ActiveConnectionCount == 0);
            server.TotalConnectionCount.Should().BeGreaterThan(0);
        }
        finally
        {
            await server.DisposeAsync();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Disabled_tool_is_hidden_and_direct_call_returns_stable_error()
    {
        McpServerSettings settings = new(4536, "127.0.0.1", false, [], false, null,
            [new McpToolOverride("get_page_blocks", false, "Disabled for test")], DateTimeOffset.UtcNow);
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")), settings);
        string list = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        list.Should().NotContain("get_page_blocks");
        string call =
            await h.HandleAsync(
                "{\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_page_blocks\",\"arguments\":{}}}");
        call.Should().Contain("tool_unavailable").And.Contain("disabled");
    }

    [Fact]
    public async Task Http_server_returns_401_when_bearer_token_missing_or_invalid()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        int port = GetFreeTcpPort();
        McpServerSettings settings = new(port, "127.0.0.1", false, [], true, "top-secret-token", [],
            DateTimeOffset.UtcNow);
        McpHttpServer server = new(new McpProtocolHandler(new FakeApi(), new SqliteConnectionFactory(path), settings),
            settings);
        try
        {
            await server.StartAsync();
            using HttpClient http = new();
            HttpResponseMessage response = await http.PostAsync(server.Endpoint,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}", System.Text.Encoding.UTF8,
                    "application/json"));
            ((int)response.StatusCode).Should().Be(401);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-token");
            HttpResponseMessage invalid = await http.PostAsync(server.Endpoint,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}", System.Text.Encoding.UTF8,
                    "application/json"));
            ((int)invalid.StatusCode).Should().Be(401);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "top-secret-token");
            HttpResponseMessage success = await http.PostAsync(server.Endpoint,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}", System.Text.Encoding.UTF8,
                    "application/json"));
            ((int)success.StatusCode).Should().Be(200);
        }
        finally
        {
            await server.DisposeAsync();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Http_server_uses_single_mcp_endpoint_for_post_sse_and_options()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        int port = GetFreeTcpPort();
        McpHttpServer server = new(new McpProtocolHandler(new FakeApi(), new SqliteConnectionFactory(path)), port);
        try
        {
            await server.StartAsync();
            using HttpClient http = new();
            HttpResponseMessage post = await http.PostAsync(server.Endpoint,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}", System.Text.Encoding.UTF8,
                    "application/json"));
            post.EnsureSuccessStatusCode();
            (await post.Content.ReadAsStringAsync()).Should().Contain("Patchouli");
            HttpRequestMessage options = new(HttpMethod.Options, server.Endpoint);
            HttpResponseMessage optionsResponse = await http.SendAsync(options);
            ((int)optionsResponse.StatusCode).Should().Be(204);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            HttpRequestMessage sseRequest = new(HttpMethod.Get, server.Endpoint);
            sseRequest.Headers.Accept.ParseAdd("text/event-stream");
            using HttpResponseMessage sse =
                await http.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sse.EnsureSuccessStatusCode();
            sse.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
            await using Stream stream = await sse.Content.ReadAsStreamAsync(cts.Token);
            using StreamReader reader = new(stream);
            (await ReadSseCommentAsync(reader, cts.Token)).Should().Be("connected");
            HttpResponseMessage legacySse = await http.GetAsync(BaseEndpoint(server.Endpoint) + "/sse", cts.Token);
            ((int)legacySse.StatusCode).Should().Be(404);
            HttpResponseMessage legacyMessage = await http.PostAsync(BaseEndpoint(server.Endpoint) + "/message",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cts.Token);
            ((int)legacyMessage.StatusCode).Should().Be(404);
        }
        finally
        {
            await server.DisposeAsync();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Http_server_rejects_non_json_and_accepts_notifications()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        int port = GetFreeTcpPort();
        McpHttpServer server = new(new McpProtocolHandler(new FakeApi(), new SqliteConnectionFactory(path)), port);
        try
        {
            await server.StartAsync();
            using HttpClient http = new();
            HttpResponseMessage bad = await http.PostAsync(server.Endpoint,
                new StringContent("{}", System.Text.Encoding.UTF8, "text/plain"));
            ((int)bad.StatusCode).Should().Be(415);
            HttpResponseMessage notification = await http.PostAsync(server.Endpoint,
                new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                    System.Text.Encoding.UTF8, "application/json"));
            ((int)notification.StatusCode).Should().Be(202);
        }
        finally
        {
            await server.DisposeAsync();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Mcp_interface_has_exactly_ten_read_tools()
    {
        typeof(Mcp.IMcpReadApi).GetMethods().Select(x => x.Name).Should().HaveCount(10).And.OnlyContain(x =>
            x.StartsWith("Get") || x.StartsWith("Render") || x == "SearchLibraryAsync" || x == "ListCslStylesAsync");
    }

    [Fact]
    public void Mcp_interface_has_no_transport_write_or_branch_methods()
    {
        typeof(Mcp.IMcpReadApi).GetMethods().Select(x => x.Name).Should().NotContain(x =>
            x.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Branch", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Queue", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Ocr", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/Users/a86186/secret/runtime.sqlite")]
    [InlineData("C:\\Users\\test\\secret.db")]
    [InlineData("file:///Users/a86186/test.pdf")]
    [InlineData("/tmp/app-cache/page-renders/render.png")]
    [InlineData("cache/page-renders/render.png")]
    [InlineData("FAKE_PROVIDER_SECRET_123")]
    [InlineData("sk-test-1234567890")]
    [InlineData("/usr/local/bin/ocr-engine")]
    [InlineData("/models/ocr/model.onnx")]
    [InlineData("/tmp/snapshots/manifest.json")]
    public void Response_sanitizer_redacts_sensitive_patterns(string value)
    {
        McpOutputSanitizer.Sanitize(value).Should().NotContain(value);
    }

    [Fact]
    public async Task Protocol_initialize_tools_list_unknown_and_invalid_args_are_sanitized()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string init =
            await h.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
        init.Should().Contain("Patchouli").And.Contain("2025-03-26").And.Contain("listChanged");
        string list = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        list.Should().Contain("search_library").And.Contain("get_page_blocks").And.Contain("render_item_bibliography")
            .And.Contain("readOnlyHint");
        string unknownMethod = await h.HandleAsync("{\"id\":3,\"method\":\"unknown/method\"}");
        unknownMethod.Should().Contain("\"code\":-32601");
        string unknown =
            await h.HandleAsync(
                "{\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"write_secret\",\"arguments\":{}}}");
        unknown.Should().Contain("unknown_tool");
        string invalid =
            await h.HandleAsync(
                "{\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"search_library\",\"arguments\":{}}}");
        invalid.Should().Contain("error");
        string parseError = await h.HandleAsync("{");
        parseError.Should().Contain("\"code\":-32700");
    }

    [Fact]
    public async Task Protocol_errors_preserve_request_id()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string invalid =
            await h.HandleAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"search_library\",\"arguments\":{}}}");
        using JsonDocument json = JsonDocument.Parse(invalid);
        json.RootElement.GetProperty("id").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Tools_list_exposes_explicit_input_schema_properties()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string list = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        using JsonDocument json = JsonDocument.Parse(list);
        JsonElement[] tools = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        JsonElement search = tools.Single(tool => tool.GetProperty("name").GetString() == "search_library");
        search.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        search.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("query", out _).Should().BeTrue();
        JsonElement renderMany =
            tools.Single(tool => tool.GetProperty("name").GetString() == "render_items_bibliography");
        renderMany.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("item_ids", out _).Should()
            .BeTrue();
    }

    [Fact]
    public async Task Tools_call_delegates_search_item_document_context_and_csl_arguments()
    {
        TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        try
        {
            FakeApi api = new();
            McpProtocolHandler h = new(api, db.ConnectionFactory);
            await h.HandleAsync(
                "{\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"search_library\",\"arguments\":{\"query\":\"needle\",\"limit\":7,\"cursor\":\"c\",\"profile_alias\":\"p\",\"include_evidence_refs\":false}}}");
            api.Search!.Query.Should().Be("needle");
            api.Search.PageSize.Should().Be(7);
            api.Search.Cursor.Should().Be("c");
            api.Search.IncludeEvidenceRefs.Should().BeFalse();
            ItemId item = ItemId.New();
            await h.HandleAsync(
                $"{{\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"get_item_metadata\",\"arguments\":{{\"item_id\":\"{item}\"}}}}}}");
            api.Item.Should().Be(item);
            DocumentInstanceId doc = DocumentInstanceId.New();
            await h.HandleAsync(
                $"{{\"id\":3,\"method\":\"tools/call\",\"params\":{{\"name\":\"get_document_status\",\"arguments\":{{\"document_instance_id\":\"{doc}\"}}}}}}");
            api.Document.Should().Be(doc);
            SearchUnitId unit = SearchUnitId.New();
            await h.HandleAsync(
                $"{{\"id\":4,\"method\":\"tools/call\",\"params\":{{\"name\":\"get_search_result_context\",\"arguments\":{{\"search_unit_id\":\"{unit}\"}}}}}}");
            api.Context!.SearchUnitId.Should().Be(unit);
            await h.HandleAsync(
                "{\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"list_csl_styles\",\"arguments\":{}}}");
            api.ListStylesCalled.Should().BeTrue();
            await h.HandleAsync(
                "{\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"get_csl_style\",\"arguments\":{\"style_id\":\"apa\"}}}");
            api.StyleId.Should().Be("apa");
            await h.HandleAsync(
                $"{{\"id\":7,\"method\":\"tools/call\",\"params\":{{\"name\":\"render_item_bibliography\",\"arguments\":{{\"item_id\":\"{item}\",\"style_id\":\"apa\",\"locale\":\"en-US\"}}}}}}");
            api.RenderItem.Should().Be(item);
            await h.HandleAsync(
                $"{{\"id\":8,\"method\":\"tools/call\",\"params\":{{\"name\":\"render_items_bibliography\",\"arguments\":{{\"item_ids\":[\"{item}\"],\"style_id\":\"apa\"}}}}}}");
            api.RenderMany!.ItemIds.Should().ContainSingle();
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tools_call_delegates_page_text_and_blocks_bbox_flag()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        DocumentInstanceId doc = DocumentInstanceId.New();
        PageId page = PageId.New();
        await using (SqliteConnection c = db.ConnectionFactory.CreateConnection())
        {
            await c.OpenAsync();
            await c.ExecuteAsync(
                "pragma foreign_keys=off;insert into pages(page_id,document_instance_id,page_index,rotation,coordinate_basis,renderer_basis_version,created_at,updated_at) values(@P,@D,3,0,'normalized_page','t','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');",
                new { P = page.ToString(), D = doc.ToString() });
        }

        FakeApi api = new();
        McpProtocolHandler h = new(api, db.ConnectionFactory);
        await h.HandleAsync(
            $"{{\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"get_page_text\",\"arguments\":{{\"document_instance_id\":\"{doc}\",\"page_number\":3,\"mode\":\"pinned\",\"evidence_ref\":\"evref:v1:x\"}}}}}}");
        api.Text!.PageId.Should().Be(page);
        api.Text.ReadMode.Should().Be("pinned");
        await h.HandleAsync(
            $"{{\"id\":2,\"method\":\"tools/call\",\"params\":{{\"name\":\"get_page_blocks\",\"arguments\":{{\"document_instance_id\":\"{doc}\",\"page_number\":3,\"include_bbox\":true}}}}}}");
        api.Blocks!.PageId.Should().Be(page);
        api.Blocks.IncludeBbox.Should().BeTrue();
    }

    [Fact]
    public void Agent_prd_documents_http_read_only_mcp_boundaries()
    {
        string r = File.ReadAllText(TestPaths.FromRepositoryRoot(".agent", "PRD.md"));
        r.Should().Contain("第一版 MCP 是只读且纯文本的").And.Contain("MCP 从不触发 OCR 或索引重建").And.Contain("提供程序密钥").And
            .Contain("缓存图像");
    }

    [Fact]
    public void Standalone_mcp_program_wires_csl_services()
    {
        string source = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.McpServer", "Program.cs"));
        source.Should().Contain("CslStyleStore").And.Contain("CslRenderer").And.Contain("CslItemMapper");
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue();
    }

    private static string BaseEndpoint(string mcpEndpoint)
    {
        return mcpEndpoint[..^4];
    }

    private static string HealthEndpoint(string mcpEndpoint)
    {
        return BaseEndpoint(mcpEndpoint) + "/health";
    }

    private static async Task<string> ReadSseCommentAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith(": ", StringComparison.Ordinal))
            {
                return line[2..];
            }
        }

        throw new InvalidOperationException("SSE comment was not received.");
    }

    private sealed class FakeApi : Mcp.IMcpReadApi
    {
        public Mcp.McpSearchLibraryRequest? Search;
        public ItemId? Item;
        public DocumentInstanceId? Document;
        public Mcp.McpPageTextRequest? Text;
        public Mcp.McpPageBlocksRequest? Blocks;
        public Mcp.McpSearchContextRequest? Context;
        public bool ListStylesCalled;
        public string? StyleId;
        public ItemId? RenderItem;
        public Mcp.McpRenderBibliographyRequest? RenderMany;

        public Task<Core.Results.Result<Mcp.McpSearchLibraryResponse>> SearchLibraryAsync(Mcp.McpSearchLibraryRequest r,
            CancellationToken c = default)
        {
            Search = r;
            return Task.FromResult(
                Core.Results.Result<Mcp.McpSearchLibraryResponse>.Failure("fake",
                    "/Users/test/FAKE_PROVIDER_SECRET_123"));
        }

        public Task<Core.Results.Result<Mcp.McpItemMetadataResponse>> GetItemMetadataAsync(ItemId i,
            CancellationToken c = default)
        {
            Item = i;
            return Task.FromResult(Core.Results.Result<Mcp.McpItemMetadataResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpDocumentStatusResponse>> GetDocumentStatusAsync(
            DocumentInstanceId i, CancellationToken c = default)
        {
            Document = i;
            return Task.FromResult(Core.Results.Result<Mcp.McpDocumentStatusResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpPageTextResponse>> GetPageTextAsync(Mcp.McpPageTextRequest r,
            CancellationToken c = default)
        {
            Text = r;
            return Task.FromResult(Core.Results.Result<Mcp.McpPageTextResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpPageBlocksResponse>> GetPageBlocksAsync(Mcp.McpPageBlocksRequest r,
            CancellationToken c = default)
        {
            Blocks = r;
            return Task.FromResult(Core.Results.Result<Mcp.McpPageBlocksResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpSearchContextResponse>> GetSearchResultContextAsync(
            Mcp.McpSearchContextRequest r, CancellationToken c = default)
        {
            Context = r;
            return Task.FromResult(Core.Results.Result<Mcp.McpSearchContextResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<IReadOnlyList<Mcp.McpCslStyleSummary>>> ListCslStylesAsync(
            CancellationToken c = default)
        {
            ListStylesCalled = true;
            return Task.FromResult(Core.Results.Result<IReadOnlyList<Mcp.McpCslStyleSummary>>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpCslStyleResponse>> GetCslStyleAsync(string styleId,
            CancellationToken c = default)
        {
            StyleId = styleId;
            return Task.FromResult(Core.Results.Result<Mcp.McpCslStyleResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpRenderBibliographyResponse>> RenderItemBibliographyAsync(
            ItemId itemId, string? styleId = null, string? locale = null, CancellationToken c = default)
        {
            RenderItem = itemId;
            StyleId = styleId;
            return Task.FromResult(Core.Results.Result<Mcp.McpRenderBibliographyResponse>.Failure("fake", "x"));
        }

        public Task<Core.Results.Result<Mcp.McpRenderBibliographyResponse>> RenderItemsBibliographyAsync(
            Mcp.McpRenderBibliographyRequest r, CancellationToken c = default)
        {
            RenderMany = r;
            return Task.FromResult(Core.Results.Result<Mcp.McpRenderBibliographyResponse>.Failure("fake", "x"));
        }
    }
}
