using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: device-local persistence of the auto-update check settings.
/// The <c>AutoCheckEnabled</c> switch must round-trip through save/load, and
/// recording a check must stamp the clock time without touching the switch.
/// </summary>
public sealed class UpdateCheckSettingsServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-update-settings-{Guid.NewGuid():N}")).FullName;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 20, 0, 42, 0, TimeSpan.Zero));

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    private string SettingsPath => Path.Combine(_root, "settings.json");

    private UpdateCheckSettingsService Service()
    {
        return new UpdateCheckSettingsService(SettingsPath, _clock);
    }

    [Fact]
    public void Default_has_auto_check_enabled_and_no_last_check()
    {
        UpdateCheckSettings.Default().Should()
            .Be(new UpdateCheckSettings(AutoCheckEnabled: true, LastCheckUtc: null));
    }

    [Fact]
    public async Task Missing_file_loads_defaults()
    {
        Result<UpdateCheckSettings> result = await Service().GetSettingsAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(UpdateCheckSettings.Default());
    }

    [Fact]
    public async Task AutoCheckEnabled_round_trips_through_save_and_load()
    {
        var service = Service();

        Result<UpdateCheckSettings> saved = await service.SaveSettingsAsync(
            new UpdateCheckSettings(AutoCheckEnabled: false, LastCheckUtc: null));
        Result<UpdateCheckSettings> loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.AutoCheckEnabled.Should().BeFalse();
        loaded.Value.LastCheckUtc.Should().BeNull();
    }

    [Fact]
    public async Task LastCheckUtc_round_trips_through_save_and_load()
    {
        var service = Service();
        var stamp = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        await service.SaveSettingsAsync(new UpdateCheckSettings(true, stamp));
        Result<UpdateCheckSettings> loaded = await service.GetSettingsAsync();

        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.LastCheckUtc.Should().Be(stamp);
    }

    [Fact]
    public async Task RecordCheck_stamps_clock_time_and_preserves_the_switch()
    {
        var service = Service();
        await service.SaveSettingsAsync(new UpdateCheckSettings(AutoCheckEnabled: false, LastCheckUtc: null));

        Result<UpdateCheckSettings> recorded = await service.RecordCheckAsync();
        Result<UpdateCheckSettings> loaded = await service.GetSettingsAsync();

        recorded.IsSuccess.Should().BeTrue();
        recorded.Value.LastCheckUtc.Should().Be(_clock.UtcNow);
        loaded.Value.Should().Be(recorded.Value);
        // The enabled/disabled switch is untouched by recording a check.
        loaded.Value.AutoCheckEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Save_preserves_other_sections_in_shared_device_file()
    {
        await File.WriteAllTextAsync(SettingsPath, """{"McpServer": {"Port": 1234}}""");
        var service = Service();

        await service.SaveSettingsAsync(new UpdateCheckSettings(AutoCheckEnabled: false, LastCheckUtc: null));

        string json = await File.ReadAllTextAsync(SettingsPath);
        json.Should().Contain("1234");
        json.Should().Contain("AutoCheckEnabled");
    }

    [Fact]
    public async Task Save_rejects_null_settings()
    {
        await FluentActions.Invoking(() => Service().SaveSettingsAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAutoCheckAsync_toggles_flag_and_preserves_last_check()
    {
        var service = Service();
        var stamp = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        await service.SaveSettingsAsync(new UpdateCheckSettings(AutoCheckEnabled: true, LastCheckUtc: stamp));

        Result<UpdateCheckSettings> saved = await service.SaveAutoCheckAsync(false);
        Result<UpdateCheckSettings> loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(new UpdateCheckSettings(AutoCheckEnabled: false, LastCheckUtc: stamp));
        loaded.Value.Should().Be(saved.Value);
    }

    [Fact]
    public async Task SaveAutoCheckAsync_restores_flag_without_a_last_check()
    {
        var service = Service();
        await service.SaveSettingsAsync(new UpdateCheckSettings(AutoCheckEnabled: false, LastCheckUtc: null));

        Result<UpdateCheckSettings> saved = await service.SaveAutoCheckAsync(true);

        saved.IsSuccess.Should().BeTrue();
        saved.Value.Should().Be(new UpdateCheckSettings(AutoCheckEnabled: true, LastCheckUtc: null));
    }

    [Fact]
    public async Task Concurrent_record_and_flag_save_stay_coherent_under_the_write_gate()
    {
        var service = Service();
        await service.SaveSettingsAsync(new UpdateCheckSettings(AutoCheckEnabled: true, LastCheckUtc: null));
        // From here on the flag is false and every writer preserves it; this
        // keeps the final assertion deterministic under any interleaving.
        await service.SaveAutoCheckAsync(false);

        // Interleave the two writers; the write gate must keep the file
        // coherent (no deadlock, no exception, no torn read-modify-write).
        await Task.WhenAll(Enumerable.Range(0, 20).SelectMany(_ => new Task[]
        {
            service.RecordCheckAsync(),
            service.SaveAutoCheckAsync(false),
        }));
        Result<UpdateCheckSettings> recorded = await service.RecordCheckAsync();
        Result<UpdateCheckSettings> loaded = await service.GetSettingsAsync();

        recorded.IsSuccess.Should().BeTrue();
        loaded.Value.Should().Be(new UpdateCheckSettings(
            AutoCheckEnabled: false, LastCheckUtc: _clock.UtcNow));
    }
}
