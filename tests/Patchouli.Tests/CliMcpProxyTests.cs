using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;
using Patchouli.Host.Lifecycle;

namespace Patchouli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliProxyIsolationCollection
{
    public const string Name = "CliProxyIsolation";
}

/// <summary>
/// The CLI talks only to a same-machine MCP endpoint. These tests keep a fake proxy in front
/// of a real loopback listener to prove the default client never sends that traffic (or its
/// bearer token) through an HTTP proxy.
/// </summary>
[Collection(CliProxyIsolationCollection.Name)]
public sealed class CliMcpProxyTests
{
    [Fact]
    public void Default_cli_handler_disables_proxy()
    {
        using HttpMessageHandler handler = McpHttpClient.CreateLoopbackHandler();

        SocketsHttpHandler sockets = handler.Should().BeOfType<SocketsHttpHandler>().Which;
        sockets.UseProxy.Should().BeFalse();
    }

    [Fact]
    public async Task Loopback_initialize_ignores_a_configured_proxy_and_keeps_the_bearer_token_direct()
    {
        await using RecordingProxy proxy = new();
        await using LoopbackMcpServer server = new();
        IWebProxy originalProxy = HttpClient.DefaultProxy;
        try
        {
            // Stands in for an environment proxy: with the previous default HttpClientHandler
            // this proxy would receive http://127.0.0.1:<port>/mcp, exactly like the
            // http_proxy/no_proxy report in issue #75.
            HttpClient.DefaultProxy = new WebProxy(proxy.Address);

            using McpHttpClient client = new(server.Endpoint, "secret-token");
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            await client.InitializeAsync(deadline.Token);
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
        }

        server.Requests.Should().Be(1);
        proxy.Connections.Should().Be(0, "loopback MCP traffic must not be sent through HTTP_PROXY");
        server.Authorization.Should().Be("Bearer secret-token");
    }

    private sealed class RecordingProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _connections;

        public RecordingProxy()
        {
            _listener.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _acceptLoop = AcceptAsync();
        }

        public string Address { get; }

        public int Connections => Volatile.Read(ref _connections);

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or
                                                      ObjectDisposedException)
                {
                    break;
                }

                Interlocked.Increment(ref _connections);
                client.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or
                                                  ObjectDisposedException)
            {
                // The accept loop has already ended.
            }

            _cts.Dispose();
        }
    }

    /// <summary>Minimal one-shot MCP HTTP host that answers <c>initialize</c>.</summary>
    private sealed class LoopbackMcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _requests;
        private string? _authorization;

        public LoopbackMcpServer()
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp";
            _acceptLoop = AcceptAsync();
        }

        public string Endpoint { get; }

        public int Requests => Volatile.Read(ref _requests);

        public string? Authorization => Volatile.Read(ref _authorization);

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or
                                                      ObjectDisposedException)
                {
                    break;
                }

                // One request per test: handling it inline lets DisposeAsync drain the server
                // before the cancellation source is disposed.
                await HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    (_, Dictionary<string, string> headers, string body) =
                        await ReadRequestAsync(stream, _cts.Token);
                    if (headers.TryGetValue("Authorization", out string? authorization))
                    {
                        Volatile.Write(ref _authorization, authorization);
                    }

                    Interlocked.Increment(ref _requests);
                    int id;
                    using (JsonDocument request = JsonDocument.Parse(body))
                    {
                        id = request.RootElement.GetProperty("id").GetInt32();
                    }

                    string json = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            protocolVersion = RuntimeHostCoordinator.ProtocolVersion,
                            capabilities = new { },
                            serverInfo = new { name = "fake-mcp", version = "0" }
                        }
                    });
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    string response =
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n" +
                        "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
                    await stream.WriteAsync(payload, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                    // A fixture disconnect or test cancellation is the only expected handler failure.
                    // Malformed requests still fault the accept loop and fail the test.
                }
            }
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers, string Body)>
            ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
            string requestLine = await reader.ReadLineAsync(cancellationToken) ??
                                 throw new InvalidOperationException("The MCP request was empty.");
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
            {
                int separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            int contentLength = headers.TryGetValue("Content-Length", out string? value) &&
                                int.TryParse(value, out int parsed)
                ? parsed
                : 0;
            char[] body = new char[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int next = await reader.ReadAsync(body.AsMemory(read, contentLength - read), cancellationToken);
                if (next == 0)
                {
                    break;
                }

                read += next;
            }

            return (requestLine, headers, new string(body, 0, read));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or
                                                  ObjectDisposedException)
            {
                // The accept loop has already ended.
            }

            _cts.Dispose();
        }
    }
}
