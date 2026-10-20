using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Patchouli.Host.Lifecycle;

namespace Patchouli.Tests;

public sealed class RuntimeHostCoordinatorTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-runtime-host-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Same_database_has_exactly_one_lifetime_owner()
    {
        string database = Path.Combine(_root, "library.sqlite");
        await using RuntimeHostLease? first = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Headless, _root);

        first.Should().NotBeNull();
        RuntimeHostCoordinator.TryAcquire(database, RuntimeHostKind.Desktop, _root).Should().BeNull();
    }

    [Fact]
    public async Task Different_database_paths_have_independent_owners()
    {
        await using RuntimeHostLease? first = RuntimeHostCoordinator.TryAcquire(
            Path.Combine(_root, "first.sqlite"), RuntimeHostKind.Desktop, _root);
        await using RuntimeHostLease? second = RuntimeHostCoordinator.TryAcquire(
            Path.Combine(_root, "second.sqlite"), RuntimeHostKind.Headless, _root);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.PathKey.Should().NotBe(second!.PathKey);
    }

    [Fact]
    public async Task Published_record_is_validated_against_the_live_process()
    {
        string database = Path.Combine(_root, "published.sqlite");
        await using RuntimeHostLease lease = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Desktop, _root)!;
        RuntimeHostDiscoveryRecord published = await lease.PublishAsync(
            Guid.NewGuid().ToString("D"), "http://localhost:4536/mcp");

        RuntimeHostDiscoveryRecord? discovered = await RuntimeHostCoordinator.ReadAsync(database, _root);

        discovered.Should().Be(published);
    }

    [Fact]
    public async Task Stale_pid_start_time_record_is_not_discoverable()
    {
        string database = Path.Combine(_root, "stale.sqlite");
        await using RuntimeHostLease lease = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Desktop, _root)!;
        RuntimeHostDiscoveryRecord published = await lease.PublishAsync(
            Guid.NewGuid().ToString("D"), "http://localhost:4536/mcp");
        // Stale both UTC and /proc ticks: the procTicks path is preferred on Linux
        // and would otherwise early-return true, bypassing the UTC check.
        // A stale record must not be discoverable via either identity mechanism.
        RuntimeHostDiscoveryRecord stale = published with
        {
            ProcessStartedUtc = published.ProcessStartedUtc.AddSeconds(-1),
            ProcessStartTimeProcTicks = published.ProcessStartTimeProcTicks.HasValue
                ? published.ProcessStartTimeProcTicks.Value + 100 // different clock tick
                : null
        };
        string recordPath = Path.Combine(_root, lease.PathKey + ".json");
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(stale));

        (await RuntimeHostCoordinator.ReadAsync(database, _root)).Should().BeNull();
    }

    [Fact]
    public async Task Publishing_replaces_record_while_existing_reader_retains_old_snapshot()
    {
        string database = Path.Combine(_root, "shared-reader.sqlite");
        await using RuntimeHostLease lease = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Headless, _root)!;
        RuntimeHostDiscoveryRecord original = await lease.PublishAsync(
            Guid.NewGuid().ToString("D"), "http://localhost:4536/mcp");
        string recordPath = Path.Combine(_root, lease.PathKey + ".json");
        await using FileStream reader = new(recordPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        RuntimeHostDiscoveryRecord replacement = await lease.PublishAsync(
            original.LibraryId, "http://localhost:4537/mcp");

        (await RuntimeHostCoordinator.ReadAsync(database, _root)).Should().Be(replacement);
        (await JsonSerializer.DeserializeAsync<RuntimeHostDiscoveryRecord>(
            reader, RuntimeHostCoordinator.JsonOptions)).Should().Be(original);
        Directory.GetFiles(_root, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Discovery_releases_record_before_waiting_for_stale_host_health_probe()
    {
        string database = Path.Combine(_root, "slow-health.sqlite");
        await using RuntimeHostLease lease = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Headless, _root)!;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RuntimeHostDiscoveryRecord original = await lease.PublishAsync(
            Guid.NewGuid().ToString("D"), $"http://127.0.0.1:{port}/mcp");
        string recordPath = Path.Combine(_root, lease.PathKey + ".json");
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(
            original with { ProcessId = int.MaxValue }, RuntimeHostCoordinator.JsonOptions));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<RuntimeHostDiscoveryRecord?> discovery = RuntimeHostCoordinator.ReadAsync(
            database, _root, timeout.Token);
        try
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(timeout.Token);
            byte[] request = new byte[1024];
            (await connection.GetStream().ReadAsync(request, timeout.Token)).Should().BePositive();
            discovery.IsCompleted.Should().BeFalse();

            // On Windows this fails if discovery retains its read handle during the health probe.
            using FileStream exclusive = new(recordPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally
        {
            await timeout.CancelAsync();
            try
            {
                await discovery;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // End the deliberately stalled health probe before disposing the fixture.
            }
        }
    }

    [Fact]
    public async Task Incompatible_protocol_record_is_not_discoverable()
    {
        string database = Path.Combine(_root, "protocol.sqlite");
        await using RuntimeHostLease lease = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Desktop, _root)!;
        RuntimeHostDiscoveryRecord published = await lease.PublishAsync(
            Guid.NewGuid().ToString("D"), "http://localhost:4536/mcp");
        RuntimeHostDiscoveryRecord incompatible = published with { ProtocolVersion = "1900-01-01" };
        string recordPath = Path.Combine(_root, lease.PathKey + ".json");
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(incompatible));

        (await RuntimeHostCoordinator.ReadAsync(database, _root)).Should().BeNull();
    }

    [Fact]
    public async Task Authenticated_headless_takeover_releases_before_desktop_acquires()
    {
        string database = Path.Combine(_root, "takeover.sqlite");
        RuntimeHostLease headless = RuntimeHostCoordinator.TryAcquire(
            database, RuntimeHostKind.Headless, _root)!;
        await headless.PublishAsync(Guid.NewGuid().ToString("D"), "http://localhost:4536/mcp");
        Task release = Task.Run(async () =>
        {
            await headless.WaitForTakeoverRequestAsync();
            await headless.DisposeAsync();
        });

        await using RuntimeHostLease desktop = await RuntimeHostCoordinator.AcquireDesktopAsync(
            database, TimeSpan.FromSeconds(3), _root);

        desktop.HostKind.Should().Be(RuntimeHostKind.Desktop);
        await release;
    }

    [Fact]
    public async Task Releasing_owner_removes_only_its_discovery_record()
    {
        string database = Path.Combine(_root, "release.sqlite");
        RuntimeHostLease lease = RuntimeHostCoordinator.TryAcquire(database, RuntimeHostKind.Desktop, _root)!;
        await lease.PublishAsync(Guid.NewGuid().ToString("D"), "http://localhost:4536/mcp");

        await lease.DisposeAsync();

        (await RuntimeHostCoordinator.ReadAsync(database, _root)).Should().BeNull();
        RuntimeHostLease? replacement = RuntimeHostCoordinator.TryAcquire(database, RuntimeHostKind.Headless, _root);
        replacement.Should().NotBeNull();
        await replacement!.DisposeAsync();
    }

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }
}
