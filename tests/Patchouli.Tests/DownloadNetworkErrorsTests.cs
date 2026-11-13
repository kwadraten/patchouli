using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Patchouli.Host.Downloads;

namespace Patchouli.Tests;

public sealed class DownloadNetworkErrorsTests
{
    [Fact]
    public async Task A_rejected_proxy_connect_is_reported_as_a_proxy_failure_without_addresses()
    {
        await using StubProxy proxy = StubProxy.Start(async stream =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n"));
        });

        HttpRequestException exception = await SendThroughAsync(proxy.Uri);

        string description = DownloadNetworkErrors.Describe(exception);
        description.Should().Be("proxy tunnel error (proxy returned HTTP 407)");
        proxy.FirstRequestLine.Should().Be("CONNECT example.invalid:443 HTTP/1.1");
        description.Should().NotContain("127.0.0.1").And.NotContain("example.invalid");
    }

    [Fact]
    public async Task A_tunnel_that_breaks_during_tls_is_reported_as_a_tls_failure()
    {
        await using StubProxy proxy = StubProxy.Start(async stream =>
        {
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
            // Not a TLS server: an intercepting proxy with an untrusted certificate surfaces
            // the same SecureConnectionError category.
            await stream.WriteAsync("not tls\r\n"u8.ToArray());
        });

        HttpRequestException exception = await SendThroughAsync(proxy.Uri);

        DownloadNetworkErrors.Describe(exception).Should().Be("TLS handshake failed");
    }

    [Fact]
    public void Uncategorized_failures_keep_the_generic_description()
    {
        DownloadNetworkErrors.Describe(new HttpRequestException("boom")).Should().Be("network error");
    }

    private static async Task<HttpRequestException> SendThroughAsync(Uri proxy)
    {
        using SocketsHttpHandler handler = new() { Proxy = new WebProxy(proxy), UseProxy = true };
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync("https://example.invalid/model.onnx");
        }
        catch (HttpRequestException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The request unexpectedly succeeded through the stub proxy.");
    }

    private sealed class StubProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;

        private StubProxy(TcpListener listener, Func<NetworkStream, Task> respond)
        {
            _listener = listener;
            _loop = Task.Run(async () =>
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                FirstRequestLine = await ReadHeadAsync(stream);
                await respond(stream);
                await stream.FlushAsync();
            });
        }

        public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        public string? FirstRequestLine { get; private set; }

        public static StubProxy Start(Func<NetworkStream, Task> respond)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new StubProxy(listener, respond);
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

            return head.ToString().Split("\r\n")[0];
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is SocketException or IOException
                                                  or ObjectDisposedException or TimeoutException)
            {
                // The stub is torn down after the client has already observed its response.
            }
        }
    }
}
