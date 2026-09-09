using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Mcp;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class McpServerHostTests
{
    [Fact]
    public async Task Start_transitions_to_running_and_stop_transitions_to_stopped()
    {
        await using HostContext context = await HostContext.CreateAsync();
        McpServerHost host = new(context.Services, (_, _, _) => { });
        List<McpServerHostStatus> statuses = new();
        object sync = new();
        host.StatusChanged += (_, eventArgs) =>
        {
            lock (sync)
            {
                statuses.Add(eventArgs.Status);
            }
        };

        int port = GetFreeTcpPort();
        McpServerSettings settings = new(port, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);
        await host.StartAsync(settings);
        try
        {
            host.Status.Should().Be(McpServerHostStatus.Running);
            host.IsRunning.Should().BeTrue();
            host.Endpoint.Should().Be($"http://localhost:{port}/mcp",
                "loopback bind addresses are displayed as localhost");
            host.RunningSettingsRevision.Should().Be(settings.Revision);
            lock (sync)
            {
                statuses.Should().ContainInOrder(McpServerHostStatus.Starting, McpServerHostStatus.Running);
            }

            using HttpClient http = new();
            string health = await http.GetStringAsync(BaseEndpoint(host.Endpoint) + "/health");
            health.Should().Contain("ok");
        }
        finally
        {
            await host.StopAsync();
        }

        host.Status.Should().Be(McpServerHostStatus.Stopped);
        host.IsRunning.Should().BeFalse();
        host.RunningSettingsRevision.Should().BeNull();
    }

    [Fact]
    public async Task Port_conflict_reports_an_exception_and_reaches_the_error_status()
    {
        await using HostContext context = await HostContext.CreateAsync();
        List<McpServerHostExceptionEventArgs> exceptions = new();
        object sync = new();
        McpServerHost host = new(context.Services, (_, _, _) => { });
        host.ExceptionReported += (_, eventArgs) =>
        {
            lock (sync)
            {
                exceptions.Add(eventArgs);
            }
        };

        using TcpListener blocker = new(IPAddress.Loopback, 0);
        blocker.Start();
        int conflictPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
        McpServerSettings settings =
            new(conflictPort, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);

        await host.StartAsync(settings);
        try
        {
            host.Status.Should().Be(McpServerHostStatus.Error);
            host.IsRunning.Should().BeFalse();
            lock (sync)
            {
                exceptions.Should().ContainSingle().Which.Operation.Should().Be("start-listener");
            }
        }
        finally
        {
            await host.StopAsync();
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

    private static string BaseEndpoint(string mcpEndpoint)
    {
        return mcpEndpoint[..^4];
    }

    private sealed class HostContext : IAsyncDisposable
    {
        private HostContext(string tempRoot, HostServices services)
        {
            TempRoot = tempRoot;
            Services = services;
        }

        public string TempRoot { get; }
        public HostServices Services { get; }

        public static async Task<HostContext> CreateAsync()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-host-{Guid.NewGuid():N}");
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
            HostServices services =
                await HostServices.CreateAsync(databasePath, settings, Path.Combine(tempRoot, "appsettings.json"));
            Result<LibraryMetadata> create = await services.Library.CreateLibraryAsync("MCP Host Test");
            create.IsSuccess.Should().BeTrue(create.ErrorMessage);
            return new HostContext(tempRoot, services);
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
}
