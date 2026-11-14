using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Settings;
using Patchouli.Host.Composition;
using Patchouli.Host.Network;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
/// Issue #56 host wiring: the persisted "Network" settings drive the host's transport policy,
/// clients created through <see cref="HostServices.CreateHttpClient"/> honor it, and settings
/// updates apply without rebuilding the host.
/// </summary>
public sealed class HostNetworkIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"patchouli-host-network-{Guid.NewGuid():N}");

    [Fact]
    public async Task Host_clients_route_through_the_persisted_manual_proxy_and_live_updates()
    {
        Directory.CreateDirectory(_root);
        await using StubProxy proxy = StubProxy.Start();
        await using StubProxy origin = StubProxy.Start();
        string database = Path.Combine(_root, "runtime.sqlite");
        PatchouliAppSettings settings = PatchouliAppSettings.Default() with
        {
            Runtime = PatchouliAppSettings.Default().Runtime with
            {
                RuntimeDatabasePath = database,
                DefaultSyncRoot = Path.Combine(_root, "sync"),
                DefaultStagingRoot = Path.Combine(_root, "staging"),
                LogDirectory = Path.Combine(_root, "logs")
            },
            Network = new NetworkAppSettings(NetworkProxyModes.Manual, proxy.Address, "", false, [])
        };

        HostServices host = await HostServices.CreateAsync(database, settings,
            Path.Combine(_root, "settings.json"));
        try
        {
            host.NetworkSettings.ProxyMode.Should().Be(NetworkProxyModes.Manual);
            host.HttpTransportPolicy.Settings.ProxyAddress.Should().Be(proxy.Address);

            // An external name tunnels through the manual proxy; the stub answers 200.
            using HttpClient agentDownload = host.CreateHttpClient(HostHttpPurpose.AgentDownload);
            using HttpResponseMessage throughProxy = await agentDownload.GetAsync("http://example.invalid/");
            throughProxy.StatusCode.Should().Be(HttpStatusCode.OK);
            proxy.RequestCount.Should().Be(1);

            // Loopback stays direct even in Manual mode (the origin stub answers directly).
            NetworkConnectionTestResult probe = await host.NetworkConnectionTest.TestAsync(origin.Uri);
            probe.Success.Should().BeTrue();
            proxy.RequestCount.Should().Be(1);

            host.UpdateNetworkSettings(new NetworkAppSettings(NetworkProxyModes.None, "", "", false, []));
            host.NetworkSettings.ProxyMode.Should().Be(NetworkProxyModes.None);
            using HttpClient direct = host.CreateHttpClient(HostHttpPurpose.AgentDownload);
            HttpRequestException? failure = null;
            try
            {
                using HttpResponseMessage _ = await direct.GetAsync("http://example.invalid/");
            }
            catch (HttpRequestException exception)
            {
                failure = exception;
            }

            // "none" connects directly: the .invalid name cannot resolve instead of using a proxy.
            failure.Should().NotBeNull();
            proxy.RequestCount.Should().Be(1);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task An_injected_handler_factory_still_wins_over_the_transport_policy()
    {
        Directory.CreateDirectory(_root);
        string database = Path.Combine(_root, "runtime.sqlite");
        PatchouliAppSettings settings = PatchouliAppSettings.Default() with
        {
            Runtime = PatchouliAppSettings.Default().Runtime with
            {
                RuntimeDatabasePath = database,
                DefaultSyncRoot = Path.Combine(_root, "sync"),
                DefaultStagingRoot = Path.Combine(_root, "staging"),
                LogDirectory = Path.Combine(_root, "logs")
            },
            Network = new NetworkAppSettings(NetworkProxyModes.Manual, "http://127.0.0.1:1",
                "", false, [])
        };

        HostServices host = await HostServices.CreateAsync(database, settings,
            Path.Combine(_root, "settings.json"),
            infrastructure: new HostInfrastructureOptions
            {
                HttpHandlerFactory = _ => new EmptyHandler()
            });
        try
        {
            // The test seam replaces the transport entirely — the manual proxy must not apply.
            using HttpClient client = host.CreateHttpClient(HostHttpPurpose.UpdateApi);
            using HttpResponseMessage response = await client.GetAsync("http://example.invalid/");
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleasePoolsInDirectory(_root);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class StubProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;
        private int _requestCount;

        private StubProxy(TcpListener listener)
        {
            _listener = listener;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        public string Uri => Address;
        public int RequestCount => _requestCount;

        public static StubProxy Start()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new StubProxy(listener);
        }

        private async Task AcceptLoopAsync()
        {
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync();
                    NetworkStream stream = client.GetStream();
                    await ReadHeadAsync(stream);
                    Interlocked.Increment(ref _requestCount);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    await stream.FlushAsync();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (IOException)
                {
                }
            }
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
