using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;
using Patchouli.Host.Composition;
using Patchouli.UI;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Independent verification of the interop host lifecycle (issue #14 §5,
/// checklist items 1/7): a disabled service binds nothing, an occupied port
/// is a diagnosable error state rather than a silent rebind, and a running
/// host serves exactly its own library over real HTTP.
/// </summary>
public sealed class CaywIndependentHostTests
{
    private sealed class HostFixture : IAsyncDisposable
    {
        public required string TempRoot { get; init; }

        public required HostServices Services { get; init; }

        public static async Task<HostFixture> CreateAsync()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"patchouli-cayw-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            string databasePath = Path.Combine(tempRoot, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            PatchouliAppSettings settings = defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = databasePath,
                    DefaultSyncRoot = Path.Combine(tempRoot, "sync"),
                    DefaultStagingRoot = Path.Combine(tempRoot, "staging"),
                    LogDirectory = Path.Combine(tempRoot, "logs"),
                    UseMockOcrOnly = true
                }
            };
            HostServices services = await HostServices.CreateAsync(
                databasePath, settings, Path.Combine(tempRoot, "appsettings.json"));
            Result<LibraryMetadata> create = await services.Library.CreateLibraryAsync("CAYW Host Test");
            create.IsSuccess.Should().BeTrue(create.ErrorMessage);
            return new HostFixture { TempRoot = tempRoot, Services = services };
        }

        public ValueTask DisposeAsync()
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(TempRoot);
            try
            {
                Directory.Delete(TempRoot, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disabled_service_binds_nothing()
    {
        await using HostFixture fixture = await HostFixture.CreateAsync();
        int port = GetFreeTcpPort();
        await using CitationInteropHost host = new(
            fixture.Services, UnavailableCitationDesktopBridge.Instance, (_, _, _) => { });

        await host.StartAsync(new CitationInteropAppSettings(false, port));

        host.Status.Status.Should().Be(CitationInteropHostStatus.Stopped);
        host.IsRunning.Should().BeFalse();

        // Nothing may listen on the configured port while disabled (curl
        // reports a refused connection: non-zero exit code, no HTTP status).
        LoopbackHttpResponse probe = await LoopbackHttp.SendAsync(
            "GET", $"http://127.0.0.1:{port}/better-bibtex/cayw?probe=1");
        probe.StatusCode.Should().Be(0);
        probe.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task Occupied_port_is_an_error_state_and_the_host_recovers_on_a_free_port()
    {
        await using HostFixture fixture = await HostFixture.CreateAsync();
        int port = GetFreeTcpPort();
        TcpListener squatter = new(IPAddress.Loopback, port);
        squatter.Start();
        await using CitationInteropHost host = new(
            fixture.Services, UnavailableCitationDesktopBridge.Instance, (_, _, _) => { });
        try
        {
            await host.StartAsync(new CitationInteropAppSettings(true, port));

            host.Status.Status.Should().Be(CitationInteropHostStatus.Error);
            host.IsRunning.Should().BeFalse();
            host.Status.Detail.Should().Contain(port.ToString(),
                "the settings page must be able to explain the bind conflict");
        }
        finally
        {
            squatter.Stop();
        }

        // After the conflict clears, the same host starts cleanly — and did
        // not silently move to another port in the meantime.
        await host.StartAsync(new CitationInteropAppSettings(true, port));
        host.Status.Status.Should().Be(CitationInteropHostStatus.Running);
        host.Endpoint.Should().Be($"http://127.0.0.1:{port}");

        LoopbackHttpResponse probe = await LoopbackHttp.SendAsync(
            "GET", $"http://127.0.0.1:{port}/better-bibtex/cayw?probe=1");
        // Headless bridge: probe must report the non-interactive state.
        probe.StatusCode.Should().Be(503);

        await host.StopAsync();
        host.Status.Status.Should().Be(CitationInteropHostStatus.Stopped);
    }

    [Fact]
    public async Task Running_host_serves_its_own_library_and_interactive_probe_is_ready()
    {
        await using HostFixture fixture = await HostFixture.CreateAsync();
        Result<ItemMetadata> created = await fixture.Services.Items.CreateItemAsync(
            new CreateItemRequest("book", "Hosted Library Book"));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);

        int port = GetFreeTcpPort();
        CountingCitationBridge bridge = new();
        await using CitationInteropHost host = new(fixture.Services, bridge, (_, _, _) => { });
        await host.StartAsync(new CitationInteropAppSettings(true, port));
        host.IsRunning.Should().BeTrue();

        LoopbackHttpResponse probe = await LoopbackHttp.SendAsync(
            "GET", $"http://127.0.0.1:{port}/better-bibtex/cayw?probe=1");
        probe.StatusCode.Should().Be(200);
        probe.Body.Should().Be("ready");
        bridge.AssertNoBridgeCalls();

        LoopbackHttpResponse rpc = await LoopbackHttp.SendAsync(
            "POST", $"http://127.0.0.1:{port}/better-bibtex/json-rpc",
            """{"jsonrpc":"2.0","method":"item.search","params":["Hosted"]}""",
            ("Content-Type", "application/json"));
        rpc.StatusCode.Should().Be(200);
        JsonObject envelope = JsonNode.Parse(rpc.Body)!.AsObject();
        envelope["result"]!.AsArray().Single()!.AsObject()["citekey"]!.GetValue<string>()
            .Should().Be(created.Value.CitationKey);
    }
}
