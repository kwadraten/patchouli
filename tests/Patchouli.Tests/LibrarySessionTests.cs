using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Library;
using Patchouli.Host.Composition;
using Patchouli.Host.Lifecycle;
using Patchouli.UI;

namespace Patchouli.Tests;

using SessionContract = ILibrarySession<
    HostServices,
    Patchouli.Host.Mcp.McpServerHost,
    Patchouli.Host.CitationInterop.CitationInteropHost,
    Host.Watching.FileSearchRootWatcherService,
    Host.Import.LibraryImportOrchestrator>;

/// <summary>
/// Issue #84 T2: the Host-owned <see cref="LibrarySession"/> owns the open → attach → dispose
/// sequence for one Library (lease, services, MCP host, citation interop host, root watcher,
/// import orchestrator, revision monitor) and both desktop and headless hosts share it.
/// </summary>
public sealed class LibrarySessionTests : IDisposable
{
    private readonly string _leaseRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-library-session-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Open_success_exposes_components_and_acquires_lease()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
        LibrarySession session = await OpenAsync(options, settings, database, LibrarySessionOwnership.Desktop);
        try
        {
            session.Services.Should().NotBeNull();
            session.Mcp.Should().NotBeNull();
            session.CitationInterop.Should().NotBeNull();
            session.Watcher.Should().NotBeNull();
            session.ImportOrchestrator.Should().NotBeNull();
            SessionContract contract = session;
            contract.Services.Should().BeSameAs(session.Services);
            contract.McpHost.Should().BeSameAs(session.Mcp);
            contract.CitationInteropHost.Should().BeSameAs(session.CitationInterop);
            contract.FileSearchRootWatcher.Should().BeSameAs(session.Watcher);
            contract.ImportOrchestrator.Should().BeSameAs(session.ImportOrchestrator);

            RuntimeHostCoordinator.TryAcquire(database.Path, RuntimeHostKind.Headless, _leaseRoot)
                .Should().BeNull("the open session holds the desktop lease");
        }
        finally
        {
            await session.DisposeAsync();
        }

        RuntimeHostLease? released = RuntimeHostCoordinator.TryAcquire(
            database.Path, RuntimeHostKind.Headless, _leaseRoot);
        released.Should().NotBeNull("disposing the session releases the lease");
        await released!.DisposeAsync();
    }

    [Fact]
    public async Task Gate_rejects_before_any_composition_and_releases_lease()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "create table library_metadata(schema_version integer, min_host_version text);" +
                "insert into library_metadata values(2, '99.0.0');");
        }

        List<object> stages = [];
        LibrarySessionOpenRequest request = BuildRequest(options, settings, database,
            LibrarySessionOwnership.Desktop, new CaptureProgress(stages.Add));
        Func<Task> open = () => LibrarySession.OpenAsync(request);
        await open.Should().ThrowAsync<LibraryRequiresNewerHostException>();

        // The #62 gate runs before any composition: no composing stage, no logger directory.
        stages.Should().NotContain(StartupStage.ComposingServices);
        Directory.Exists(options.Runtime.LogDirectory).Should().BeFalse();

        // The failed open released the lease it had just acquired.
        RuntimeHostLease? lease = RuntimeHostCoordinator.TryAcquire(
            database.Path, RuntimeHostKind.Headless, _leaseRoot);
        lease.Should().NotBeNull("a gate-rejected open releases the lease");
        await lease!.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_mid_open_releases_lease_and_shuts_partial_services()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
        try
        {
            using CancellationTokenSource cancellation = new();
            LibrarySessionOpenRequest request = BuildRequest(options, settings, database,
                LibrarySessionOwnership.Desktop,
                new CaptureProgress(update =>
                {
                    // HostServices.CreateAsync takes no token; cancel at the first stage
                    // report so the session observes cancellation AFTER HostServices was
                    // constructed, forcing the teardown path to run ShutdownAsync.
                    if (update is StartupStage)
                    {
                        // Safe: the progress callback only runs inside the awaited OpenAsync
                        // calls below, all of which complete before the using scope disposes it.
                        // ReSharper disable once AccessToDisposedClosure
                        cancellation.Cancel();
                    }
                }));

            // Safe: the delegate is awaited on the next line, inside the using scope.
            // ReSharper disable once AccessToDisposedClosure
            Func<Task> open = () => LibrarySession.OpenAsync(request, cancellation.Token);
            await open.Should().ThrowAsync<OperationCanceledException>();

            // The lease the open had acquired is released.
            RuntimeHostLease? lease = RuntimeHostCoordinator.TryAcquire(
                database.Path, RuntimeHostKind.Headless, _leaseRoot);
            lease.Should().NotBeNull("a cancelled open releases the lease");
            await lease!.DisposeAsync();

            // The partially opened services were shut down: the same database opens
            // cleanly afterwards (a still-open host would keep it locked).
            LibrarySession session = await LibrarySession.OpenAsync(request);
            await session.DisposeAsync();

            // ShutdownAsync of the cancelled open ran to completion: the fixture can
            // delete the database file and its sidecars (the fixture fails loudly when a
            // file is still locked).
            await database.DisposeAsync();
            database = null!;
        }
        finally
        {
            if (database is not null)
            {
                await database.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Dispose_stops_owned_resources_releases_lease_and_is_idempotent()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
        LibrarySession session = await OpenAsync(options, settings, database, LibrarySessionOwnership.Desktop);

        // Start the listener so disposal must stop admission and drain it (the ephemeral
        // fallback keeps this test free of fixed-port contention).
        bool running = await session.StartMcpFromPersistedSettingsAsync(true);
        running.Should().BeTrue("persisted MCP settings must leave the listener running");
        session.Mcp.IsRunning.Should().BeTrue();

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        session.Mcp.IsRunning.Should().BeFalse("dispose stops and drains the MCP listener");
        SessionContract contract = session;
        contract.McpConnectionCounts.Should().Be((0L, 0L));

        RuntimeHostLease? lease = RuntimeHostCoordinator.TryAcquire(
            database.Path, RuntimeHostKind.Headless, _leaseRoot);
        lease.Should().NotBeNull("dispose releases the lease");
        await lease!.DisposeAsync();

        // Idempotent: repeated disposal returns promptly and does not throw.
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Switch_disposes_first_library_fully_before_second_opens()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        await using TemporarySqliteDatabase first = await TemporarySqliteDatabase.CreateMigratedAsync();
        await using TemporarySqliteDatabase second = await TemporarySqliteDatabase.CreateMigratedAsync();

        LibrarySession firstSession = await OpenAsync(options, settings, first, LibrarySessionOwnership.Desktop);
        HostServices firstServices = firstSession.Services;
        await firstSession.DisposeAsync();

        // The first library's lease is observably released before the second session opens.
        RuntimeHostLease? probe = RuntimeHostCoordinator.TryAcquire(
            first.Path, RuntimeHostKind.Headless, _leaseRoot);
        probe.Should().NotBeNull();
        await probe!.DisposeAsync();

        LibrarySession secondSession = await OpenAsync(options, settings, second, LibrarySessionOwnership.Desktop);
        try
        {
            secondSession.Services.Should().NotBeSameAs(firstServices);
            RuntimeHostCoordinator.TryAcquire(second.Path, RuntimeHostKind.Headless, _leaseRoot)
                .Should().BeNull("the second session holds its lease");
            RuntimeHostLease? releasedFirst = RuntimeHostCoordinator.TryAcquire(
                first.Path, RuntimeHostKind.Headless, _leaseRoot);
            releasedFirst.Should().NotBeNull("the first library is fully released");
            await releasedFirst!.DisposeAsync();
        }
        finally
        {
            await secondSession.DisposeAsync();
        }
    }

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_leaseRoot);
    }

    private async Task<LibrarySession> OpenAsync(PatchouliAppSettings options,
        TemporaryAppSettingsFile settings, TemporarySqliteDatabase database,
        LibrarySessionOwnership ownership, IProgress<object>? startupProgress = null)
    {
        return await LibrarySession.OpenAsync(
            BuildRequest(options, settings, database, ownership, startupProgress));
    }

    private LibrarySessionOpenRequest BuildRequest(PatchouliAppSettings options,
        TemporaryAppSettingsFile settings, TemporarySqliteDatabase database,
        LibrarySessionOwnership ownership, IProgress<object>? startupProgress = null)
    {
        return new LibrarySessionOpenRequest(
            database.Path,
            ownership,
            options,
            settings.Path,
            StartupProgress: startupProgress,
            WatcherLogger: new SimpleFileLogger(options.Runtime.LogDirectory),
            CreateWatcherAndOrchestrator: true,
            LeaseDirectory: _leaseRoot);
    }

    private sealed class CaptureProgress(Action<object> capture) : IProgress<object>
    {
        public void Report(object value)
        {
            capture(value);
        }
    }
}
