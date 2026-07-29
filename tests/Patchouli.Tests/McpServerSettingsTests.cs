using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class McpServerSettingsTests
{
    [Fact]
    public async Task Save_and_load_round_trip_preserves_non_secret_fields_and_overrides()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-{Guid.NewGuid():N}.json");
        McpServerSettingsService service = new(settingsPath, clock);

        Result<McpServerSettings> saved = await service.SaveSettingsAsync(new McpServerSettings(
            4540,
            "127.0.0.1",
            true,
            ["https://example.test"],
            true,
            "redacted-token",
            [new McpToolOverride("search_library", false, "disabled for tests")],
            clock.UtcNow));
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Port.Should().Be(4540);
        File.ReadAllText(settingsPath).Should().Contain("redacted-token");
        loaded.Value.ToolOverrides.Should().ContainSingle();
    }

    [Fact]
    public async Task Validate_rejects_unsafe_bind_without_token()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        BlockingOperationService blockingOperations = new(database.ConnectionFactory, clock);
        McpServerSettingsService service =
            new(Path.Combine(Path.GetTempPath(), $"patchouli-mcp-{Guid.NewGuid():N}.json"), clock, blockingOperations);

        Result result = await service.ValidateSettingsAsync(new McpServerSettings(
            4536,
            "0.0.0.0",
            false,
            [],
            false,
            null,
            [],
            clock.UtcNow));
        Result<IReadOnlyList<BlockingOperation>> operations = await blockingOperations.ListAsync(
            BlockingOperationStatus.Failed,
            BlockingOperationTypes.McpStartValidation,
            BlockingOperationScopeTypes.McpServerSettings,
            "default");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("unsafe_configuration");
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().FailureCode.Should().Be("unsafe_configuration");
        operations.Value.Single().FailureMessage.Should().Be("Binding MCP to 0.0.0.0 requires a bearer token.");
        operations.Value.Single().NextActions.Should().Equal("Bind to 127.0.0.1", "Configure a bearer token");
    }

    [Fact]
    public async Task Snapshot_shard_excludes_mcp_settings_and_token()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Snapshot-safe MCP");
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-{Guid.NewGuid():N}.json");
        McpServerSettingsService service = new(settingsPath, clock);
        await service.SaveSettingsAsync(new McpServerSettings(
            4540,
            "127.0.0.1",
            false,
            [],
            true,
            "super-secret-token",
            [],
            clock.UtcNow));

        string syncRoot = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-mcp-settings-{Guid.NewGuid():N}")).FullName;
        try
        {
            Result<SnapshotPublishResult> published =
                await new SnapshotPublisher(clock).PublishSnapshotAsync(
                    new SnapshotPublishRequest(database.Path, syncRoot, "device-a"));
            published.IsSuccess.Should().BeTrue();
            string shardPath = Path.Combine(syncRoot, published.Value.Shards.Single().FileName);
            await using (SqliteConnection shard = new(new SqliteConnectionStringBuilder
                             { DataSource = shardPath, Pooling = false }.ToString()))
            {
                await shard.OpenAsync();
            }

            (await File.ReadAllTextAsync(shardPath)).Should().NotContain("super-secret-token");
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, true);
            }
        }
    }

    [Fact]
    public async Task Stale_save_cannot_overwrite_a_newer_persisted_revision()
    {
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-cas-{Guid.NewGuid():N}.json");
        McpServerSettingsService service = new(settingsPath, clock);
        Result<McpServerSettings> first = await service.SaveSettingsAsync(
            McpServerSettingsService.DefaultSettings(clock.UtcNow) with { Port = 4540 },
            0);
        Result<McpServerSettings> second = await service.SaveSettingsAsync(
            first.Value with { Port = 4541 },
            first.Value.Revision);

        Result<McpServerSettings> stale = await service.SaveSettingsAsync(
            first.Value with { Port = 4999 },
            first.Value.Revision);
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();

        first.Value.Revision.Should().Be(1);
        second.Value.Revision.Should().Be(2);
        stale.ErrorCode.Should().Be(AppErrorCodes.StaleSettingsRevision);
        loaded.Value.Port.Should().Be(4541);
        loaded.Value.Revision.Should().Be(2);
    }

    [Fact]
    public async Task General_settings_save_preserves_a_newer_mcp_revision()
    {
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-settings-race-{Guid.NewGuid():N}.json");
        PatchouliAppSettings staleGeneralSettings = PatchouliAppSettings.Default();
        staleGeneralSettings.Save(settingsPath).IsSuccess.Should().BeTrue();
        McpServerSettingsService service = new(settingsPath, clock);
        Result<McpServerSettings> saved = await service.SaveSettingsAsync(
            staleGeneralSettings.Mcp with { Port = 4542 },
            0);

        (staleGeneralSettings with
        {
            Ui = staleGeneralSettings.Ui with { ShowLibraryLeftSidebar = false }
        }).Save(settingsPath).IsSuccess.Should().BeTrue();
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();

        loaded.Value.Port.Should().Be(4542);
        loaded.Value.Revision.Should().Be(saved.Value.Revision);
    }
}
