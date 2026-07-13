using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Settings;

namespace Patchouli.Tests;

public sealed class LibrarySettingStoreTests
{
    [Fact]
    public async Task Opted_in_setting_round_trips_as_a_library_record_and_device_override_wins_effective_value()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibrarySettingStore store = new(database.ConnectionFactory);
        DateTimeOffset updatedAt = DateTimeOffset.Parse("2026-07-13T00:00:00Z");
        SettingRecord record = new("metadata_lookup", 1, "{\"sources\":[]}", 1, updatedAt, "device-a",
            SettingsMergePolicies.ScalarReplace);

        (await store.SaveAsync(record)).IsSuccess.Should().BeTrue();
        Result<SettingRecord?> loaded = await store.GetAsync("metadata_lookup");

        loaded.Value.Should().Be(record);
        new SettingsResolver().Resolve(loaded.Value,
                new DeviceOverride("metadata_lookup", "{\"sources\":[\"local\"]}", 2))
            .Should().Be(new EffectiveSetting("metadata_lookup", "{\"sources\":[\"local\"]}",
                "device_override", 2));
    }
}
