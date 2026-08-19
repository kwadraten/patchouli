using System.Net.Sockets;
using FluentAssertions;
using System.Text.Json;
using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Infrastructure.Database;

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
    public void Sanitizer_preserves_versioned_patchouli_resource_uris()
    {
        string value =
            "patchouli://texts/doc/page-1.md?rev=20000000-0000-0000-0000-000000000001&box=30000000-0000-0000-0000-000000000001";
        McpOutputSanitizer.Sanitize(value).Should().Be(value);
    }

    [Fact]
    public void Sanitizer_identifies_safe_text()
    {
        McpOutputSanitizer.IsSafe("ordinary scholarly text").Should().BeTrue();
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
    public async Task Initialize_reports_current_build_version_and_structured_tools()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string response = await h.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
        response.Should().Contain(Core.BuildInfo.Version)
            .And.Contain("Patchouli")
            .And.Contain("2025-03-26")
            .And.Contain("listChanged")
            .And.Contain("structured Library tools")
            .And.Contain("instructions");
    }

    [Fact]
    public async Task Protocol_initialize_tools_list_unknown_and_invalid_args_are_sanitized()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string list = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        list.Should().Contain("patchouli.find").And.Contain("patchouli.fetch").And.Contain("patchouli.put")
            .And.Contain("patchouli.cite").And.NotContain("patchouli_shell");

        string unknownMethod = await h.HandleAsync("{\"id\":3,\"method\":\"unknown/method\"}");
        unknownMethod.Should().Contain("\"code\":-32601");
        string unknownTool =
            await h.HandleAsync(
                "{\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"write_secret\",\"arguments\":{}}}");
        ToolErrorCode(unknownTool).Should().Be(2);
        string parseError = await h.HandleAsync("{");
        parseError.Should().Contain("\"code\":-32700");
    }

    [Fact]
    public async Task Tools_list_exposes_v3_input_schemas_without_legacy_surface()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string list = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        using JsonDocument json = JsonDocument.Parse(list);
        JsonElement[] tools = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        tools.Should().HaveCount(4);

        JsonElement find = tools.Single(tool => tool.GetProperty("name").GetString() == "patchouli.find");
        JsonElement findProps = find.GetProperty("inputSchema").GetProperty("properties");
        findProps.TryGetProperty("query", out _).Should().BeTrue();
        findProps.TryGetProperty("in", out _).Should().BeTrue();
        findProps.TryGetProperty("where", out _).Should().BeTrue();
        findProps.TryGetProperty("literal", out _).Should().BeTrue();
        findProps.TryGetProperty("limit", out _).Should().BeTrue();
        findProps.TryGetProperty("cursor", out _).Should().BeTrue();
        findProps.TryGetProperty("detail", out _).Should().BeTrue();
        findProps.TryGetProperty("format", out _).Should().BeTrue();
        findProps.TryGetProperty("regex", out _).Should().BeFalse();
        find.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Should().BeEmpty();

        JsonElement fetch = tools.Single(tool => tool.GetProperty("name").GetString() == "patchouli.fetch");
        JsonElement fetchProps = fetch.GetProperty("inputSchema").GetProperty("properties");
        fetchProps.TryGetProperty("uris", out _).Should().BeTrue();
        fetchProps.TryGetProperty("range", out _).Should().BeTrue();
        fetchProps.TryGetProperty("limit_bytes", out _).Should().BeTrue();
        fetchProps.TryGetProperty("format", out _).Should().BeTrue();
        fetchProps.TryGetProperty("revision", out _).Should().BeFalse();
        fetch.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("uris");
        fetch.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();

        JsonElement put = tools.Single(tool => tool.GetProperty("name").GetString() == "patchouli.put");
        JsonElement putProps = put.GetProperty("inputSchema").GetProperty("properties");
        putProps.TryGetProperty("uri", out _).Should().BeTrue();
        putProps.TryGetProperty("content", out _).Should().BeTrue();
        putProps.TryGetProperty("base", out _).Should().BeFalse();
        put.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("uri", "content");
        put.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeFalse();

        JsonElement cite = tools.Single(tool => tool.GetProperty("name").GetString() == "patchouli.cite");
        JsonElement citeProps = cite.GetProperty("inputSchema").GetProperty("properties");
        citeProps.TryGetProperty("refs", out _).Should().BeTrue();
        citeProps.TryGetProperty("style", out _).Should().BeTrue();
        citeProps.TryGetProperty("locale", out _).Should().BeTrue();
        citeProps.TryGetProperty("bibliography", out _).Should().BeTrue();
        citeProps.TryGetProperty("html", out _).Should().BeTrue();
        citeProps.TryGetProperty("format", out _).Should().BeTrue();
        cite.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("refs");
        citeProps.GetProperty("style").GetProperty("description").GetString().Should().Contain("default style");
    }

    [Fact]
    public async Task Find_clean_success_omits_message_and_returns_vfs_entries()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string response = await h.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":" +
            "{\"name\":\"patchouli.find\",\"arguments\":{\"format\":\"json\"}}}");
        using JsonDocument envelope = ToolResultEnvelope(response);
        envelope.RootElement.TryGetProperty("message", out _).Should().BeFalse();
        JsonElement meta = envelope.RootElement.GetProperty("meta");
        meta.GetProperty("library_revision").GetString().Should().Be("lib:1");
        meta.GetProperty("domain_total").GetInt32().Should().Be(3);
        meta.GetProperty("shown_total").GetInt32().Should().Be(3);
        JsonElement[] entries = envelope.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        entries.Should().HaveCount(3);
        entries.Select(e => e.GetProperty("uri").GetString())
            .Should().Equal("patchouli://items/", "patchouli://texts/", "patchouli://csl-styles/");
        entries.Should().OnlyContain(e => e.GetProperty("type").GetString() == "directory");
        envelope.RootElement.GetProperty("continuation").ValueKind.Should().Be(JsonValueKind.Null);
        ToolIsError(response).Should().BeFalse();
    }

    [Fact]
    public async Task Default_encoding_returns_the_unified_envelope()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string response = await h.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":" +
            "{\"name\":\"patchouli.find\",\"arguments\":{}}}");
        using JsonDocument envelope = ToolResultEnvelope(response);
        envelope.RootElement.TryGetProperty("meta", out _).Should().BeTrue();
        envelope.RootElement.TryGetProperty("continuation", out _).Should().BeTrue();
        envelope.RootElement.TryGetProperty("entries", out _).Should().BeTrue();
        envelope.RootElement.TryGetProperty("revision", out _).Should().BeFalse();
        envelope.RootElement.TryGetProperty("data", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Protocol_rejects_legacy_regex_base_revision_arguments()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));

        string findRegex = await h.HandleAsync(ToolCall("patchouli.find", """{"regex":true}"""));
        ToolErrorCode(findRegex).Should().Be(2);
        ToolErrorName(findRegex).Should().Be("INVALID_ARGUMENT");

        string fetchRevision = await h.HandleAsync(
            ToolCall("patchouli.fetch", """{"uris":["patchouli://items/abc.bib"],"revision":"item:x"}"""));
        ToolErrorCode(fetchRevision).Should().Be(2);

        string putBase = await h.HandleAsync(
            ToolCall("patchouli.put", """{"uri":"patchouli://items/abc.bib","content":"x","base":"item:x"}"""));
        ToolErrorCode(putBase).Should().Be(2);
    }

    [Fact]
    public async Task Protocol_argument_shape_errors_use_invalid_argument_code()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));

        string wrongInteger = await h.HandleAsync(ToolCall("patchouli.find", """{"limit":"many"}"""));
        ToolErrorCode(wrongInteger).Should().Be(2);

        string scalarUris = await h.HandleAsync(
            ToolCall("patchouli.fetch", """{"uris":"patchouli://items/x.bib"}"""));
        ToolErrorCode(scalarUris).Should().Be(2);

        string wrongFormat = await h.HandleAsync(ToolCall("patchouli.find", """{"format":"yaml"}"""));
        ToolErrorCode(wrongFormat).Should().Be(2);
    }

    [Fact]
    public async Task Protocol_internal_errors_are_compact_and_sanitized_with_a_reference()
    {
        FakeApi api = new() { ThrowOnLibraryState = true };
        McpProtocolHandler h = new(api,
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string response = await h.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":" +
            "{\"name\":\"patchouli.find\",\"arguments\":{}}}");
        using JsonDocument envelope = ToolResultEnvelope(response);
        ToolIsError(response).Should().BeTrue();
        string error = envelope.RootElement.GetProperty("message").GetProperty("error").GetString()!;
        error.Should().StartWith("INTERNAL [code 1; ref ").And.Contain("]: The host could not complete the request.");
        string text = envelope.RootElement.GetRawText();
        text.Should().NotContain("Exception").And.NotContain("SqliteConnectionFactory");
        envelope.RootElement.TryGetProperty("revision", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Protocol_errors_preserve_request_id()
    {
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        string invalid = await h.HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":" +
            "{\"name\":\"patchouli.find\",\"arguments\":{\"regex\":true}}}");
        using JsonDocument json = JsonDocument.Parse(invalid);
        json.RootElement.GetProperty("id").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Disabled_tool_is_hidden_and_direct_call_returns_stable_error()
    {
        McpServerSettings settings = new(4536, "127.0.0.1", false, [], false, null,
            [new McpToolOverride("patchouli.find", false, "Disabled for test")], DateTimeOffset.UtcNow);
        McpProtocolHandler h = new(new FakeApi(),
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")), settings);
        string list = await h.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        list.Should().NotContain("patchouli.find");
        string call = await h.HandleAsync(
            "{\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"patchouli.find\",\"arguments\":{}}}");
        using JsonDocument envelope = ToolResultEnvelope(call);
        ToolIsError(call).Should().BeTrue();
        string error = envelope.RootElement.GetProperty("message").GetProperty("error").GetString()!;
        error.Should().Be("UNAVAILABLE [code 8]: disabled: Disabled for test");
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
    public async Task Http_server_rejects_oversized_request_with_413_before_tool_invocation()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        int port = GetFreeTcpPort();
        McpHttpServer server = new(new McpProtocolHandler(new FakeApi(), new SqliteConnectionFactory(path)), port,
            maxRequestBytes: 256);
        try
        {
            await server.StartAsync();
            using HttpClient http = new();
            string largeQuery = new('a', 400);
            string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{" +
                             $"\"name\":\"patchouli.put\",\"arguments\":{{\"uri\":\"patchouli://items/abc.bib\"," +
                             $"\"content\":\"{largeQuery}\"}}}}";
            HttpResponseMessage overLimit = await http.PostAsync(server.Endpoint,
                new StringContent(request, System.Text.Encoding.UTF8, "application/json"));
            ((int)overLimit.StatusCode).Should().Be(413);

            HttpResponseMessage small = await http.PostAsync(server.Endpoint,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
                    System.Text.Encoding.UTF8, "application/json"));
            ((int)small.StatusCode).Should().Be(200);
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
    public void Read_api_surface_is_text_only_and_exposes_library_state()
    {
        string[] names = typeof(IMcpReadApi).GetMethods().Select(x => x.Name).ToArray();
        names.Should().Contain("GetCurrentLibraryStateAsync");
        names.Should().Contain("GetPrimaryDocumentOcrIndexStatusAsync");
        names.Should().NotContain(name =>
            !string.Equals(name, "GetPrimaryDocumentOcrIndexStatusAsync", StringComparison.Ordinal) &&
            (name.Contains("Ocr", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Branch", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Queue", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Agent_prd_documents_http_read_only_mcp_boundaries()
    {
        string r = File.ReadAllText(TestPaths.FromRepositoryRoot(".agents", "PRD.md"));
        r.Should().Contain("v1/v2 首发 MCP 是只读且纯文本的").And.Contain("MCP 从不触发 OCR 或索引重建").And.Contain("提供程序密钥").And
            .Contain("缓存图像");
    }

    [Fact]
    public void Standalone_mcp_program_wires_csl_services()
    {
        string source = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.McpServer", "Program.cs"));
        source.Should().Contain("CslStyleStore").And.Contain("CslRenderer").And.Contain("CslItemMapper")
            .And.NotContain("ShellSidecarHost");
    }

    private static string ToolCall(string tool, string argumentsJson)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{" +
               $"\"name\":\"{tool}\",\"arguments\":{argumentsJson}}}}}";
    }

    private static JsonDocument ToolResultEnvelope(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        string text = json.RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        return JsonDocument.Parse(text);
    }

    private static bool ToolIsError(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        return json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();
    }

    private static int ToolErrorCode(string response)
    {
        using JsonDocument envelope = ToolResultEnvelope(response);
        string line = envelope.RootElement.GetProperty("message").GetProperty("error").GetString()!;
        McpToolError.TryGetCode(line, out McpErrorCode code).Should().BeTrue();
        return (int)code;
    }

    private static string ToolErrorName(string response)
    {
        using JsonDocument envelope = ToolResultEnvelope(response);
        string line = envelope.RootElement.GetProperty("message").GetProperty("error").GetString()!;
        return line.Split(" [", 2, StringSplitOptions.None)[0];
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

    private sealed class FakeApi : IMcpReadApi
    {
        public bool ThrowOnLibraryState;

        public Task<Result<McpLibraryStateResponse>> GetCurrentLibraryStateAsync(
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnLibraryState)
            {
                throw new InvalidOperationException("boom: secret database detail");
            }

            return Task.FromResult(Result<McpLibraryStateResponse>.Success(
                new McpLibraryStateResponse("lib", "lib:1")));
        }

        public Task<Result<McpSearchLibraryResponse>> SearchLibraryAsync(McpSearchLibraryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpSearchLibraryResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpItemMetadataResponse>> GetItemMetadataAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpItemMetadataResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpDocumentStatusResponse>> GetDocumentStatusAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpDocumentStatusResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpPageTextResponse>> GetPageTextAsync(McpPageTextRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPageTextResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpPageBlocksResponse>> GetPageBlocksAsync(McpPageBlocksRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPageBlocksResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(McpSearchContextRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpSearchContextResponse>.Failure("fake", "x"));
        }

        public Task<Result<IReadOnlyList<McpCslStyleSummary>>> ListCslStylesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpCslStyleSummary>>.Failure("fake", "x"));
        }

        public Task<Result<McpCslStyleResponse>> GetCslStyleAsync(string styleId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpCslStyleResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpRenderBibliographyResponse>> RenderItemBibliographyAsync(ItemId itemId,
            string? styleId = null, string? locale = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpRenderBibliographyResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpRenderBibliographyResponse>> RenderItemsBibliographyAsync(
            McpRenderBibliographyRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpRenderBibliographyResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseItemPage>> BrowseItemsAsync(int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseItemPage>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseItemPage>> SearchItemsAsync(string query, bool literal, int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseItemPage>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseDocumentPage>> BrowseDocumentsAsync(int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseDocumentPage>.Failure("fake", "x"));
        }

        public Task<Result<IReadOnlyList<McpTextResourceProjection>>> GetTextResourceProjectionsAsync(
            IReadOnlyList<DocumentInstanceId> documentInstanceIds, IReadOnlyList<McpWhereClause>? where = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpTextResourceProjection>>.Failure("fake", "x"));
        }

        public Task<Result<string>> GetPrimaryDocumentOcrIndexStatusAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseStylePage>> BrowseStylesAsync(int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseStylePage>.Failure("fake", "x"));
        }

        public Task<Result<McpDocumentOutlineResponse>> GetDocumentOutlineAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpDocumentOutlineResponse>.Failure("fake", "x"));
        }

        public Task<Result<ItemId>> GetItemIdForDocumentAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ItemId>.Failure("fake", "x"));
        }
    }
}
