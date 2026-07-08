using Dapper;
using FluentAssertions;
using Patchouli.Core.Mcp;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class McpServerSettingsTests
{
    [Fact]
    public async Task Save_and_load_round_trip_preserves_non_secret_fields_and_overrides()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var service = new McpServerSettingsService(database.ConnectionFactory, clock);

        var saved = await service.SaveSettingsAsync(new McpServerSettings(
            4540,
            "127.0.0.1",
            true,
            ["https://example.test"],
            true,
            "redacted-token",
            [new McpToolOverride("search_library", false, "disabled for tests")],
            clock.UtcNow));
        var loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Port.Should().Be(4540);
        loaded.Value.CorsEnabled.Should().BeTrue();
        loaded.Value.AllowedOrigins.Should().ContainSingle().Which.Should().Be("https://example.test");
        loaded.Value.ToolOverrides.Should().ContainSingle();
        loaded.Value.ToolOverrides.Single().Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_rejects_unsafe_bind_without_token()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var service = new McpServerSettingsService(database.ConnectionFactory, clock);

        var result = await service.ValidateSettingsAsync(new McpServerSettings(
            4536,
            "0.0.0.0",
            false,
            [],
            false,
            null,
            [],
            clock.UtcNow));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("unsafe_configuration");
    }

    [Fact]
    public async Task Snapshot_shard_excludes_mcp_settings_and_token()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Snapshot-safe MCP");
        var service = new McpServerSettingsService(database.ConnectionFactory, clock);
        await service.SaveSettingsAsync(new McpServerSettings(
            4540,
            "127.0.0.1",
            false,
            [],
            true,
            "super-secret-token",
            [],
            clock.UtcNow));

        var syncRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-mcp-settings-{Guid.NewGuid():N}")).FullName;
        try
        {
            var published = await new SnapshotPublisher(clock).PublishSnapshotAsync(new SnapshotPublishRequest(database.Path, syncRoot, "device-a"));
            published.IsSuccess.Should().BeTrue();
            var shardPath = Path.Combine(syncRoot, published.Value.Shards.Single().FileName);
            await using (var shard = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = shardPath, Pooling = false }.ToString()))
            {
                await shard.OpenAsync();
                (await shard.ExecuteScalarAsync<int>("select count(1) from mcp_server_settings;")).Should().Be(0);
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
}
