using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Settings;
using Patchouli.Host.Network;

namespace Patchouli.Tests;

/// <summary>
/// Issue #56: the non-UI connectivity probe distinguishes proxy tunnel, TLS, DNS, connection and
/// authentication failures — and never echoes proxy/target addresses or credentials.
/// </summary>
public sealed class NetworkConnectionTestServiceTests
{
    [Fact]
    public async Task A_rejected_proxy_connect_is_classified_as_a_proxy_tunnel_failure()
    {
        await using StubProxy proxy = StubProxy.Start(
            "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
        NetworkConnectionTestService test = new(ManualPolicy(proxy.Uri));

        NetworkConnectionTestResult result = await test.TestAsync("https://example.invalid/");

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(NetworkConnectionTestKinds.ProxyTunnel);
        result.Summary.Should().NotContain("127.0.0.1").And.NotContain("example.invalid");
    }

    [Fact]
    public async Task A_407_tunnel_rejection_is_classified_as_authentication()
    {
        await using StubProxy proxy = StubProxy.Start(
            "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n");
        NetworkConnectionTestService test = new(ManualPolicy(proxy.Uri));

        NetworkConnectionTestResult result = await test.TestAsync("https://example.invalid/");

        result.Kind.Should().Be(NetworkConnectionTestKinds.Authentication);
        result.HttpStatus.Should().Be(407);
    }

    [Fact]
    public async Task A_broken_tls_handshake_is_classified_as_tls()
    {
        await using StubProxy proxy = StubProxy.Start(
            "HTTP/1.1 200 Connection Established\r\n\r\nnot tls\r\n");
        NetworkConnectionTestService test = new(ManualPolicy(proxy.Uri));

        NetworkConnectionTestResult result = await test.TestAsync("https://example.invalid/");

        result.Kind.Should().Be(NetworkConnectionTestKinds.Tls);
        result.Summary.Should().Contain("CA certificate");
    }

    [Fact]
    public async Task An_unresolvable_host_is_classified_as_dns()
    {
        NetworkConnectionTestService test = new(DirectPolicy());

        NetworkConnectionTestResult result = await test.TestAsync("https://nonexistent.invalid/");

        result.Kind.Should().Be(NetworkConnectionTestKinds.Dns);
    }

    [Fact]
    public async Task A_refused_connection_is_classified_as_connection()
    {
        // Bind-then-release a loopback port so nothing is listening there.
        TcpListener reserved = new(IPAddress.Loopback, 0);
        reserved.Start();
        int port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();

        NetworkConnectionTestService test = new(DirectPolicy());
        NetworkConnectionTestResult result = await test.TestAsync($"http://127.0.0.1:{port}/");

        result.Kind.Should().Be(NetworkConnectionTestKinds.Connection);
    }

    [Fact]
    public async Task A_non_success_status_reports_the_http_kind_and_code()
    {
        await using StubProxy origin = StubProxy.Start(
            "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
        NetworkConnectionTestService test = new(DirectPolicy());

        NetworkConnectionTestResult result = await test.TestAsync(origin.Uri);

        result.Kind.Should().Be(NetworkConnectionTestKinds.Http);
        result.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task A_successful_probe_reports_ok()
    {
        await using StubProxy origin = StubProxy.Start(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        NetworkConnectionTestService test = new(DirectPolicy());

        NetworkConnectionTestResult result = await test.TestAsync(origin.Uri);

        result.Success.Should().BeTrue();
        result.Kind.Should().Be(NetworkConnectionTestKinds.Ok);
    }

    [Fact]
    public async Task A_socks5_manual_configuration_is_accepted_for_connectivity()
    {
        // Classification path only: socks5 reaches the transport and fails at the proxy connection,
        // which is "connection", not "invalid configuration".
        HttpTransportPolicy policy = new(
            new NetworkAppSettings(NetworkProxyModes.Manual, "socks5h://127.0.0.1:1", "", false, []),
            _ => Task.FromResult<string?>(null));
        NetworkConnectionTestService test = new(policy);

        NetworkConnectionTestResult result = await test.TestAsync("https://example.invalid/");

        result.Kind.Should().Be(NetworkConnectionTestKinds.Connection);
    }

    private static HttpTransportPolicy DirectPolicy()
    {
        return new HttpTransportPolicy(NetworkAppSettings.Default() with
        {
            ProxyMode = NetworkProxyModes.None
        }, _ => Task.FromResult<string?>(null));
    }

    private static HttpTransportPolicy ManualPolicy(string proxyAddress)
    {
        return new HttpTransportPolicy(new NetworkAppSettings(NetworkProxyModes.Manual, proxyAddress,
            "", false, []), _ => Task.FromResult<string?>(null));
    }

    private sealed class StubProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;

        private StubProxy(TcpListener listener, string cannedResponse)
        {
            _listener = listener;
            _loop = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync();
                    NetworkStream stream = client.GetStream();
                    await ReadHeadAsync(stream);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(cannedResponse));
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }

        public string Uri => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public static StubProxy Start(string cannedResponse)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new StubProxy(listener, cannedResponse);
        }

        private static async Task ReadHeadAsync(NetworkStream stream)
        {
            StringBuilder head = new();
            byte[] buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) &&
                   await stream.ReadAsync(buffer) > 0)
            {
                head.Append((char)buffer[0]);
            }
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
