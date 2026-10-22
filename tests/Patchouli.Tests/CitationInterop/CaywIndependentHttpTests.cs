using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Host.CitationInterop;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Transport-level verification against a real Kestrel listener (the handler
/// suites bypass HTTP entirely): Host/Origin policy, status codes for unknown
/// paths and wrong methods, body/query limits, the no-id RPC envelope over
/// the wire, client disconnect, and port release on stop. Requests are made
/// with curl via <see cref="LoopbackHttp"/> (see its doc comment for why an
/// in-process HttpClient cannot reach an IPv4 loopback listener in this
/// sandbox).
/// </summary>
public sealed class CaywIndependentHttpTests
{
    [Fact]
    public async Task Probe_over_http_returns_plain_text_ready()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse response = await LoopbackHttp.SendAsync("GET", server.Url("/better-bibtex/cayw?probe=1"));

        response.StatusCode.Should().Be(200);
        response.ContentType.Should().StartWith("text/plain");
        response.Body.Should().Be("ready");
        bridge.AssertNoBridgeCalls();
    }

    [Fact]
    public async Task Non_loopback_host_header_is_forbidden()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse response = await LoopbackHttp.SendAsync(
            "GET", server.Url("/better-bibtex/cayw?probe=1"), null, ("Host", "evil.example"));

        response.StatusCode.Should().Be(403);
        response.Body.Should().Contain("forbidden");
        bridge.AssertNoBridgeCalls();
    }

    [Fact]
    public async Task Untrusted_origin_is_forbidden_but_loopback_and_absent_origins_pass()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse untrusted = await LoopbackHttp.SendAsync(
            "GET", server.Url("/better-bibtex/cayw?probe=1"), null, ("Origin", "https://evil.example"));
        untrusted.StatusCode.Should().Be(403);

        LoopbackHttpResponse loopback = await LoopbackHttp.SendAsync(
            "GET", server.Url("/better-bibtex/cayw?probe=1"), null, ("Origin", "http://127.0.0.1:23119"));
        loopback.StatusCode.Should().Be(200);

        LoopbackHttpResponse localhost = await LoopbackHttp.SendAsync(
            "GET", server.Url("/better-bibtex/cayw?probe=1"), null, ("Origin", "http://localhost:9999"));
        localhost.StatusCode.Should().Be(200);

        LoopbackHttpResponse absent = await LoopbackHttp.SendAsync("GET", server.Url("/better-bibtex/cayw?probe=1"));
        absent.StatusCode.Should().Be(200, "local non-browser clients send no Origin and must be reachable");
    }

    [Fact]
    public async Task Unknown_path_is_404_and_wrong_methods_are_405()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        (await LoopbackHttp.SendAsync("GET", server.Url("/definitely-not-a-route"))).StatusCode.Should().Be(404);

        LoopbackHttpResponse postCayw = await LoopbackHttp.SendAsync("POST", server.Url("/better-bibtex/cayw"), "");
        postCayw.StatusCode.Should().Be(405);
        postCayw.Body.Should().Contain("unsupported");

        (await LoopbackHttp.SendAsync("GET", server.Url("/better-bibtex/json-rpc"))).StatusCode.Should().Be(405);
    }

    [Fact]
    public async Task Overlong_query_string_is_414()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse response = await LoopbackHttp.SendAsync(
            "GET", server.Url("/better-bibtex/cayw?probe=1&pad=" + new string('a', 3000)));

        response.StatusCode.Should().Be(414);
    }

    [Fact]
    public async Task Json_rpc_without_id_over_http_returns_a_result_envelope()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Wire Book"));
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse response = await LoopbackHttp.SendAsync(
            "POST", server.Url("/better-bibtex/json-rpc"),
            """{"jsonrpc":"2.0","method":"item.search","params":["Wire"]}""",
            ("Content-Type", "application/json"));

        response.StatusCode.Should().Be(200, "a request without an id must still be answered, not dropped to 204");
        response.Body.Should().Contain("\"id\":null");
        response.Body.Should().Contain($"\"citekey\":\"{book.CitationKey}\"");
        response.Body.Should().Contain($"\"citation-key\":\"{book.CitationKey}\"");
    }

    [Fact]
    public async Task Json_rpc_bad_json_over_http_is_a_parse_error_without_leaks()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse response = await LoopbackHttp.SendAsync(
            "POST", server.Url("/better-bibtex/json-rpc"), "{ this is not json", ("Content-Type", "application/json"));

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("-32700");
        response.Body.Should().NotContain("/home/", "errors must not leak host paths");
        response.Body.Should().NotContain("StackTrace");
        response.Body.Should().NotContain(" at Patchouli.", "errors must not leak stack frames");
    }

    [Fact]
    public async Task Json_rpc_oversized_body_is_413()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        await using RunningServer server = await StartServerAsync(context, bridge);

        LoopbackHttpResponse response = await LoopbackHttp.SendAsync(
            "POST", server.Url("/better-bibtex/json-rpc"), new string('x', 70_000),
            ("Content-Type", "application/json"));

        response.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Client_disconnect_over_http_cancels_the_pick_and_frees_the_gate()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Wire Book"));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CountingCitationBridge bridge = new()
        {
            OnPick = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new CitationPickBridgeResult(
                    CitationPickOutcome.Confirmed, [new CitationBridgePickEntry(book.ItemId)]);
            }
        };
        await using RunningServer server = await StartServerAsync(context, bridge);

        using Process client = LoopbackHttp.StartRequest("GET", server.Url("/better-bibtex/cayw?format=latex"), out _);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            server.Handler.IsPickInProgress.Should().BeTrue();

            client.Kill(true);
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!client.HasExited)
            {
                client.Kill(true);
            }
        }

        // The client is gone; the picker's late confirmation must be inert.
        // Wait for the handler to fully exit first — only then is the
        // clipboard count final.
        release.TrySetResult();
        await WaitUntilAsync(() => !server.Handler.IsPickInProgress, TimeSpan.FromSeconds(10));
        bridge.ClipboardCalls.Should().Be(0);

        LoopbackHttpResponse probe = await LoopbackHttp.SendAsync("GET", server.Url("/better-bibtex/cayw?probe=1"));
        probe.StatusCode.Should().Be(200);
        probe.Body.Should().Be("ready");
        bridge.ClipboardCalls.Should().Be(0, "no late write may land after the client disconnected");
    }

    [Fact]
    public async Task Stopping_the_server_frees_the_port_for_a_new_listener()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        int port = GetFreeTcpPort();

        CitationInteropHttpServer first = CreateServer(context, bridge);
        await first.StartAsync(port);
        await first.DisposeAsync();
        first.IsRunning.Should().BeFalse();

        CitationInteropHttpServer second = CreateServer(context, bridge);
        await second.StartAsync(port);
        try
        {
            second.IsRunning.Should().BeTrue();
            LoopbackHttpResponse probe =
                await LoopbackHttp.SendAsync("GET", $"http://127.0.0.1:{port}/better-bibtex/cayw?probe=1");
            probe.StatusCode.Should().Be(200);
            probe.Body.Should().Be("ready");
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Repeated_failed_starts_leave_the_server_usable()
    {
        await using CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        CountingCitationBridge bridge = new();
        int port = GetFreeTcpPort();
        TcpListener squatter = new(IPAddress.Loopback, port);
        squatter.Start();
        CitationInteropHttpServer server = CreateServer(context, bridge);
        try
        {
            // Two consecutive bind failures on the occupied port. Each one
            // builds a WebApplication; if a failed start leaked it (only a
            // successful start reaches the field StopAsync disposes), the
            // instance would accumulate undisposed hosts per retry.
            (await StartFailsAsync(server, port)).Should().BeTrue();
            server.IsRunning.Should().BeFalse();
            (await StartFailsAsync(server, port)).Should().BeTrue();
            server.IsRunning.Should().BeFalse();
        }
        finally
        {
            squatter.Stop();
        }

        // Once the port frees up, the same instance still starts cleanly
        // and serves.
        await server.StartAsync(port);
        try
        {
            server.IsRunning.Should().BeTrue();
            LoopbackHttpResponse probe =
                await LoopbackHttp.SendAsync("GET", $"http://127.0.0.1:{port}/better-bibtex/cayw?probe=1");
            probe.StatusCode.Should().Be(200);
            probe.Body.Should().Be("ready");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static async Task<bool> StartFailsAsync(CitationInteropHttpServer server, int port)
    {
        try
        {
            await server.StartAsync(port);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static CitationInteropHttpServer CreateServer(
        CaywIndependentTestContext context,
        CountingCitationBridge bridge)
    {
        return new CitationInteropHttpServer(
            new CaywRequestHandler(context.Interop, bridge),
            new JsonRpcHandler(context.Interop));
    }

    private static async Task<RunningServer> StartServerAsync(
        CaywIndependentTestContext context,
        CountingCitationBridge bridge)
    {
        int port = GetFreeTcpPort();
        CaywRequestHandler handler = new(context.Interop, bridge);
        CitationInteropHttpServer server = new(handler, new JsonRpcHandler(context.Interop));
        await server.StartAsync(port);
        return new RunningServer(server, handler, port);
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        public RunningServer(CitationInteropHttpServer server, CaywRequestHandler handler, int port)
        {
            Server = server;
            Handler = handler;
            Port = port;
        }

        public CitationInteropHttpServer Server { get; }
        public CaywRequestHandler Handler { get; }
        public int Port { get; }

        public string Url(string pathAndQuery)
        {
            return $"http://127.0.0.1:{Port}{pathAndQuery}";
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
        }
    }
}
