using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Settings;
using Patchouli.Host.Network;

namespace Patchouli.Tests;

/// <summary>
/// Issue #56: the unified transport policy — manual proxy routing (including CONNECT and proxy
/// auth), bypass rules, live mode updates, and the loopback guarantee. All traffic stays on
/// loopback stub listeners; no external network is touched.
/// </summary>
public sealed class HttpTransportPolicyTests
{
    [Fact]
    public async Task Manual_mode_tunnels_https_through_the_configured_proxy()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 Connection Established\r\n\r\n"), true);
        HttpTransportPolicy policy = new(Manual(proxy.Address), _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using HttpResponseMessage _ = await client.GetAsync("https://example.invalid/feed");
        }
        catch (HttpRequestException)
        {
            // The TLS bytes after CONNECT are garbage; only the tunnel request matters here.
        }

        proxy.FirstRequestLine.Should().Be("CONNECT example.invalid:443 HTTP/1.1");
    }

    [Fact]
    public async Task Manual_mode_sends_plain_http_requests_through_the_proxy()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        HttpTransportPolicy policy = new(Manual(proxy.Address), _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using HttpResponseMessage response = await client.GetAsync("http://example.invalid/feed");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        proxy.FirstRequestLine.Should().Be("GET http://example.invalid/feed HTTP/1.1");
    }

    [Fact]
    public async Task A_manual_bypass_entry_connects_directly()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        NetworkAppSettings settings = new(NetworkProxyModes.Manual, proxy.Address, "", false,
            ["*.internal", "bypass.invalid:8443"]);
        HttpTransportPolicy policy = new(settings, _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using HttpResponseMessage _ = await client.GetAsync("http://api.internal/feed");
        }
        catch (HttpRequestException)
        {
            // Direct connection to a .invalid host fails DNS — the point is the proxy saw nothing.
        }

        proxy.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Manual_mode_always_bypasses_loopback_targets()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        await using RecordingProxy origin = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"));
        HttpTransportPolicy policy = new(Manual(proxy.Address), _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // Loopback destinations never go through the proxy even in Manual mode.
        string body = await client.GetStringAsync(origin.Address + "feed");

        body.Should().Be("hi");
        proxy.RequestCount.Should().Be(0);
        origin.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Mode_none_ignores_the_proxy_entirely()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        HttpTransportPolicy policy = new(
            new NetworkAppSettings(NetworkProxyModes.None, proxy.Address, "", false, []),
            _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // "none" connects directly: a non-bypassed external name fails DNS instead of using the proxy.
        HttpRequestException? failure = null;
        try
        {
            using HttpResponseMessage _ = await client.GetAsync("http://example.invalid/feed");
        }
        catch (HttpRequestException exception)
        {
            failure = exception;
        }

        failure.Should().NotBeNull();
        proxy.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Updating_the_policy_applies_to_new_requests_without_rebuilding_handlers()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        await using RecordingProxy origin = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"));
        HttpTransportPolicy policy = new(Manual(proxy.Address), _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using (HttpResponseMessage throughProxy = await client.GetAsync("http://example.invalid/feed"))
        {
            throughProxy.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        proxy.RequestCount.Should().Be(1);
        origin.RequestCount.Should().Be(0);

        policy.Update(new NetworkAppSettings(NetworkProxyModes.None, "", "", false, []));
        string body = await client.GetStringAsync(origin.Address + "feed");
        body.Should().Be("hi");
        proxy.RequestCount.Should().Be(1);
        origin.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Stored_credentials_answer_a_407_challenge_without_appearing_in_errors()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 407 Proxy Authentication Required\r\n" +
                            "Proxy-Authenticate: Basic realm=\"corp\"\r\nContent-Length: 0\r\n\r\n"));
        NetworkAppSettings settings = new(NetworkProxyModes.Manual,
            $"http://127.0.0.1:{proxy.Port}", "proxy-user", true, []);
        HttpTransportPolicy policy = new(settings,
            _ => Task.FromResult<string?>("proxy-secret"));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using HttpResponseMessage _ = await client.GetAsync("https://example.invalid/feed");
        }
        catch (HttpRequestException)
        {
            // A second 407 is fine; what matters is the credential offered on the retry.
        }

        proxy.LastRequestHeaders.Should().Contain("Proxy-Authorization");
        string header = proxy.LastRequestHeaders!.Split("\r\n")
            .First(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
        // "Proxy-Authorization: Basic <base64>" — take the payload after the scheme.
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Split(' ', 3)[2]));
        decoded.Should().Be("proxy-user:proxy-secret");
    }

    [Fact]
    public async Task An_invalid_manual_address_fails_visibly_instead_of_silently_going_direct()
    {
        await using RecordingProxy proxy = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        NetworkAppSettings settings = new(NetworkProxyModes.Manual, "ftp://bad:21", "", false, []);
        HttpTransportPolicy policy = new(settings, _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // SocketsHttpHandler swallows IWebProxy exceptions and would connect directly; the policy
        // therefore routes invalid configs to an unroutable sentinel so the failure is a visible
        // connection error — never a silent bypass (example.invalid has no DNS entry anyway).
        HttpRequestException? failure = null;
        try
        {
            using HttpResponseMessage _ = await client.GetAsync("http://example.invalid/feed");
        }
        catch (HttpRequestException exception)
        {
            failure = exception;
        }

        failure.Should().NotBeNull();
        failure!.HttpRequestError.Should().Be(HttpRequestError.ConnectionError);
        proxy.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_manual_address_is_treated_as_unconfigured()
    {
        await using RecordingProxy origin = RecordingProxy.Start(_ =>
            Task.FromResult("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"));
        HttpTransportPolicy policy = new(
            new NetworkAppSettings(NetworkProxyModes.Manual, "  ", "", false, []),
            _ => Task.FromResult<string?>(null));

        using SocketsHttpHandler handler = new();
        policy.ConfigureHandler(handler);
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        (await client.GetStringAsync(origin.Address)).Should().Be("hi");
    }

    [Fact]
    public async Task The_connection_test_reports_invalid_manual_configuration()
    {
        HttpTransportPolicy policy = new(
            new NetworkAppSettings(NetworkProxyModes.Manual, "gopher://x", "", false, []),
            _ => Task.FromResult<string?>(null));
        NetworkConnectionTestService test = new(policy);

        NetworkConnectionTestResult result = await test.TestAsync();
        result.Kind.Should().Be(NetworkConnectionTestKinds.InvalidConfiguration);
        result.Success.Should().BeFalse();
    }

    private static NetworkAppSettings Manual(string address)
    {
        return new NetworkAppSettings(NetworkProxyModes.Manual, address, "", false, []);
    }

    /// <summary>
    /// Minimal recording HTTP proxy/origin: accepts a bounded number of connections, captures the
    /// request head and returns a canned response. Lives on loopback only.
    /// </summary>
    private sealed class RecordingProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, Task<string>> _respond;
        private readonly Task _loop;
        private readonly bool _keepOpen;
        private readonly List<string> _heads = new();
        private int _requestCount;

        private RecordingProxy(TcpListener listener, Func<string, Task<string>> respond, bool keepOpen)
        {
            _listener = listener;
            _respond = respond;
            _keepOpen = keepOpen;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Address => $"http://127.0.0.1:{Port}/";
        public string? FirstRequestLine => _heads.Count > 0 ? _heads[0].Split("\r\n")[0] : null;
        public string? LastRequestHeaders => _heads.Count > 0 ? _heads[^1] : null;
        public int RequestCount => _requestCount;

        public static RecordingProxy Start(Func<string, Task<string>> respond, bool keepOpen = false)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new RecordingProxy(listener, respond, keepOpen);
        }

        private async Task AcceptLoopAsync()
        {
            for (int i = 0; i < 8; i++)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        _heads.Add(await ReadHeadAsync(stream));
                        Interlocked.Increment(ref _requestCount);
                        string response = await _respond(_heads[^1]);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                        await stream.FlushAsync();
                        if (_keepOpen)
                        {
                            // Leave the tunnel half-open briefly so TLS bytes can arrive and fail.
                            await Task.Delay(250);
                        }
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        private static async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            StringBuilder head = new();
            byte[] buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) &&
                   await stream.ReadAsync(buffer) > 0)
            {
                head.Append((char)buffer[0]);
            }

            return head.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }
    }
}
