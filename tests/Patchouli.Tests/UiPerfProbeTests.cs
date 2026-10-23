using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Host.Composition;
using Patchouli.Performance;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class UiPerfProbeTests
{
    [Fact]
    public async Task Probe_measures_real_ui_framework_rows_and_heartbeat_on_the_dispatcher()
    {
        PerfOptions options = PerfOptions.Parse(
            ["--profile", "smoke", "--ui", "--items", "120", "--pages-per-item", "1", "--boxes-per-page", "25"]);

        UiPerfResult? result = await UiPerfProbe.RunAsync(options, TestPaths.MigrationsDirectory,
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Measured.Should().BeTrue();
        result.InteractiveFrameworkColdMs.Should().BeGreaterThan(0);
        result.InteractiveFrameworkHotMs.Should().BeGreaterThan(0);
        result.FirstLibraryRowsColdMs.Should().BeGreaterThan(0);
        result.FirstLibraryRowsHotMs.Should().BeGreaterThan(0);
        result.LibraryFirstBatchReadyColdMs.Should().BeGreaterThan(0);
        result.LibraryFirstBatchReadyHotMs.Should().BeGreaterThan(0);
        result.LibraryFirstBatchReadyColdMs.Should().BeGreaterThanOrEqualTo(result.FirstLibraryRowsColdMs,
            "the first published batch cannot precede the startup/first-run boundary");
        result.LibraryFirstBatchReadyHotMs.Should().BeGreaterThanOrEqualTo(result.FirstLibraryRowsHotMs,
            "the first published batch cannot precede the startup/first-run boundary");
        result.LibraryAllPagesDrainedColdMs.Should().BeGreaterThanOrEqualTo(
            result.LibraryFirstBatchReadyColdMs,
            "the full drain boundary includes the first-batch wait");
        result.LibraryAllPagesDrainedHotMs.Should().BeGreaterThanOrEqualTo(
            result.LibraryFirstBatchReadyHotMs,
            "the full drain boundary includes the first-batch wait");
        result.FirstLibraryRowCount.Should().Be(120,
            "the UI fixture must not silently cap the requested list size at 100 rows");
        result.HeartbeatSamples.Should().BeGreaterThanOrEqualTo(2,
            "the adoption window must span at least two 100 ms heartbeats");
        result.HeartbeatMaxGapMs.Should().BeGreaterThanOrEqualTo(0);
        result.UiThreadDatabaseCommands.Should().Be(0,
            "database work must never execute on the UI dispatcher thread (AC3)");
    }

    [Fact]
    public async Task Probe_report_is_privacy_safe()
    {
        PerfOptions options = PerfOptions.Parse(
            ["--profile", "smoke", "--ui", "--items", "5", "--pages-per-item", "2", "--boxes-per-page", "3"]);

        UiPerfResult? result = await UiPerfProbe.RunAsync(options, TestPaths.MigrationsDirectory,
            CancellationToken.None);

        result.Should().NotBeNull();
        string json = System.Text.Json.JsonSerializer.Serialize(result!.ToReportUi());
        ReportPrivacy.IsSafe(json).Should().BeTrue();
    }

    [Fact]
    public async Task Probe_returns_null_when_ui_disabled()
    {
        PerfOptions options = PerfOptions.Parse(["--profile", "smoke"]);

        UiPerfResult? result = await UiPerfProbe.RunAsync(options, TestPaths.MigrationsDirectory,
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Probe_propagates_cancellation_and_releases_the_fixture_root()
    {
        int before = Directory.GetDirectories(Path.GetTempPath(), "patchouli-uiperf-*").Length;
        PerfOptions options = PerfOptions.Parse(
            ["--profile", "smoke", "--ui", "--items", "5", "--pages-per-item", "1", "--boxes-per-page", "2"]);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UiPerfProbe.RunAsync(options, TestPaths.MigrationsDirectory, cancelled.Token));

        Directory.GetDirectories(Path.GetTempPath(), "patchouli-uiperf-*").Should().HaveCount(before,
            "a cancelled probe must still delete its temporary fixture root");
    }

    [Fact]
    public async Task Probe_paging_waits_propagate_caller_cancellation()
    {
        using TemporaryAppSettingsFile settings = new();
        MainWindowViewModel? main = null;
        try
        {
            main = new MainWindowViewModel(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("uiperf-cancel")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);

            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UiPerfProbe.WaitForInitialLibraryPageAsync(
                    main.Shell, TimeSpan.FromSeconds(30), cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UiPerfProbe.DrainLibraryPagesAsync(
                    main.Shell, TimeSpan.FromSeconds(30), cancelled.Token));
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }

            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task Probe_waits_for_an_in_flight_refresh_before_accepting_the_first_page()
    {
        using TemporaryAppSettingsFile settings = new();
        MainWindowViewModel? main = null;
        try
        {
            main = new MainWindowViewModel(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("uiperf-delayed")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);
            HostServices services = await main.ServicesAsync();
            for (int index = 0; index < 120; index++)
            {
                await services.Items.CreateItemAsync("book", $"Delayed Item {index:D3}");
            }

            // A refresh that is already in flight stands in for a delayed initial load: the
            // wait must ride it out (not fake readiness) and return only after it publishes.
            Task refresh = main.Shell.RefreshItemsAsync();
            main.Shell.IsRefreshingItems.Should().BeTrue();

            await UiPerfProbe.WaitForInitialLibraryPageAsync(
                main.Shell, TimeSpan.FromSeconds(60), CancellationToken.None);
            await refresh;

            LibraryPagingStatus ready = main.Shell.GetPagingStatus();
            ready.HasPageQuery.Should().BeTrue();
            ready.IsRefreshingItems.Should().BeFalse();
            ready.HasMoreItems.Should().BeTrue();
            ready.HasNextPageCursor.Should().BeTrue();
            ready.RowCount.Should().Be(LibraryShellViewModel.LibraryPageSize);

            await UiPerfProbe.DrainLibraryPagesAsync(
                main.Shell, TimeSpan.FromSeconds(60), CancellationToken.None);

            main.Shell.Items.Should().HaveCount(120);
            main.Shell.HasMoreItems.Should().BeFalse();
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }

            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task Probe_initial_page_wait_fails_bounded_with_paging_diagnostics()
    {
        using TemporaryAppSettingsFile settings = new();
        MainWindowViewModel? main = null;
        try
        {
            main = new MainWindowViewModel(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("uiperf-stuck")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            main.Shell.ObserveLibraryRevisions(null);
            HostServices services = await main.ServicesAsync();

            // Break the refresh path after services start so no generation can ever publish a
            // page; the wait must fail inside its budget instead of looping forever.
            await using (SqliteConnection connection = services.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    "alter table collections rename to unavailable_collections;");
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => main.Shell.RefreshItemsAsync());
            main.Shell.IsRefreshingItems.Should().BeFalse();

            long start = Stopwatch.GetTimestamp();
            TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                UiPerfProbe.WaitForInitialLibraryPageAsync(
                    main.Shell, TimeSpan.FromMilliseconds(400), CancellationToken.None));

            Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromSeconds(15),
                "the no-progress wait must fail inside its budget, not spin or hang");
            failure.Message.Should().Contain("initial library page readiness");
            failure.Message.Should().Contain("generation=");
            failure.Message.Should().Contain("refreshing=False");
            failure.Message.Should().Contain("hasPageQuery=False");
        }
        finally
        {
            if (main is not null)
            {
                await main.ShutdownAsync();
            }

            SqliteConnection.ClearAllPools();
        }
    }
}
