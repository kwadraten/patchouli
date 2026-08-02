using FluentAssertions;
using Patchouli.Performance;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class UiPerfProbeTests
{
    [Fact]
    public async Task Probe_measures_real_ui_framework_rows_and_heartbeat_on_the_dispatcher()
    {
        PerfOptions options = PerfOptions.Parse(
            ["--profile", "smoke", "--ui", "--items", "10", "--pages-per-item", "6", "--boxes-per-page", "5"]);

        UiPerfResult? result = await UiPerfProbe.RunAsync(options, TestPaths.MigrationsDirectory,
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Measured.Should().BeTrue();
        result.InteractiveFrameworkColdMs.Should().BeGreaterThan(0);
        result.InteractiveFrameworkHotMs.Should().BeGreaterThan(0);
        result.FirstLibraryRowsColdMs.Should().BeGreaterThan(0);
        result.FirstLibraryRowsHotMs.Should().BeGreaterThan(0);
        result.FirstLibraryRowCount.Should().Be(10);
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
}
