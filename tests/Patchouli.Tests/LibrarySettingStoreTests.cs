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

    [Fact]
    public async Task Disabled_or_local_only_settings_never_reach_the_library_record_store()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibrarySettingStore store = new(database.ConnectionFactory);
        LibrarySettingRecordService service = new(store,
            new FixedClock(DateTimeOffset.Parse("2026-07-13T00:00:00Z")));

        Result<SettingRecord> disabled = await service.SaveAsync(
            LibrarySettingKeys.MetadataLookup,
            new { sources = Array.Empty<string>() },
            "device-a",
            false);
        Result<SettingRecord> localOnly = await service.SaveAsync(
            "mcp",
            new { port = 4536 },
            "device-a",
            true);

        disabled.ErrorCode.Should().Be(AppErrorCodes.UnsupportedOperation);
        localOnly.ErrorCode.Should().Be(AppErrorCodes.UnsupportedOperation);
        (await store.ListAsync()).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Catalog_rejects_schema_and_merge_policy_drift()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibrarySettingStore store = new(database.ConnectionFactory);
        SettingRecord invalid = new(
            LibrarySettingKeys.MetadataLookup,
            2,
            "{}",
            1,
            DateTimeOffset.Parse("2026-07-13T00:00:00Z"),
            "device-a",
            SettingsMergePolicies.MapByKey);

        Result result = await store.SaveAsync(invalid);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }
}
