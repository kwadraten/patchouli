using System.Text.Json;
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
        RuntimeHostDiscoveryRecord stale = published with
        {
            ProcessStartedUtc = published.ProcessStartedUtc.AddSeconds(-1)
        };
        string recordPath = Path.Combine(_root, lease.PathKey + ".json");
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(stale));

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
        Directory.Delete(_root, true);
    }
}
