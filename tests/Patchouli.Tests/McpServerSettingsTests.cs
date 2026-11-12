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
    public async Task Incomplete_endpoint_drafts_are_persisted_but_cannot_start_a_listener()
    {
        using TemporaryAppSettingsFile file = new();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        McpServerSettingsService service = new(file.Path, clock);
        McpServerSettings draft = McpServerSettingsService.DefaultSettings(clock.UtcNow) with
        {
            BindAddress = "", AuthRequired = true, Token = ""
        };
        Result<McpServerSettings> saved = await service.SaveDraftSettingsAsync(draft, 0);
        saved.IsSuccess.Should().BeTrue();
        (await service.GetSettingsAsync()).Value.BindAddress.Should().BeEmpty();
        (await service.ValidateSettingsAsync(saved.Value)).IsFailure.Should().BeTrue();
        (await service.SaveSettingsAsync(saved.Value)).IsFailure.Should().BeTrue();
    }

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
            clock.UtcNow)
        {
            DomainPermissions = [new McpDomainPermission("texts", "fetch", false)],
            ShellCommandTimeoutSeconds = 42
        });
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue();
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Port.Should().Be(4540);
        File.ReadAllText(settingsPath).Should().Contain("redacted-token");
        loaded.Value.ToolOverrides.Should().BeEquivalentTo(new[]
        {
            new McpToolOverride("search_library", false, "disabled for tests"),
            new McpToolOverride("patchouli.add", false, "Added by migration: explicitly enable to use this tool.")
        });
        loaded.Value.ShellCommandTimeoutSeconds.Should().Be(42);
        loaded.Value.DomainPermissions.Should().Equal(new McpDomainPermission("texts", "fetch", false),
            new McpDomainPermission("images", "fetch", false),
            new McpDomainPermission("runs/ocr", "send", false),
            new McpDomainPermission("runs/translation", "send", false),
            new McpDomainPermission("runs/metadata", "send", false));
    }

    [Fact]
    public async Task Load_defaults_missing_shell_command_timeout_for_existing_settings()
    {
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-legacy-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(settingsPath, """{"Mcp":{"Port":4540}}""");
        McpServerSettingsService service = new(settingsPath, clock);

        Result<McpServerSettings> loaded = await service.GetSettingsAsync();

        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.ShellCommandTimeoutSeconds.Should().Be(15);
        loaded.Value.ExposeLibraryTags.Should().BeTrue();
        loaded.Value.ExposeLibraryCollections.Should().BeTrue();
    }

    [Fact]
    public async Task Save_and_load_round_trip_preserves_library_exposure_flags()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-{Guid.NewGuid():N}.json");
        McpServerSettingsService service = new(settingsPath, clock);

        Result<McpServerSettings> saved = await service.SaveSettingsAsync(
            McpServerSettingsService.DefaultSettings(clock.UtcNow) with
            {
                ExposeLibraryTags = false,
                ExposeLibraryCollections = false
            });
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        loaded.Value.ExposeLibraryTags.Should().BeFalse();
        loaded.Value.ExposeLibraryCollections.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task Validate_rejects_shell_command_timeout_outside_supported_range(int timeoutSeconds)
    {
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        McpServerSettingsService service =
            new(Path.Combine(Path.GetTempPath(), $"patchouli-mcp-{Guid.NewGuid():N}.json"), clock);
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(clock.UtcNow) with
        {
            ShellCommandTimeoutSeconds = timeoutSeconds
        };

        Result result = await service.ValidateSettingsAsync(settings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Be("Shell command timeout must be between 1 and 60 seconds.");
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

    [Fact]
    public void Migration_denies_images_fetch_when_view_was_never_allowed()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow);

        McpServerSettings migrated = McpServerSettingsService.NormalizeLegacySettings(settings);

        migrated.ToolOverrides.Should().NotContain(value => value.ToolName == "patchouli.view");
        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("images", "fetch", false));
        McpPermissionPolicy.Allows(migrated, "fetch", "patchouli://images/").Should().BeFalse();
    }

    [Fact]
    public void Migration_keeps_images_fetch_allowed_when_view_was_explicitly_enabled()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            ToolOverrides = [new McpToolOverride("patchouli.view", true)]
        };

        McpServerSettings migrated = McpServerSettingsService.NormalizeLegacySettings(settings);

        migrated.ToolOverrides.Should().NotContain(value => value.ToolName == "patchouli.view");
        // The converted grant is explicit: default-allow would evaporate into a denial
        // on the next save+reload, once the removed view override is gone.
        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("images", "fetch", true));
        McpPermissionPolicy.Allows(migrated, "fetch", "patchouli://images/").Should().BeTrue();
    }

    [Fact]
    public async Task Migration_grant_for_explicitly_enabled_view_survives_save_and_reload()
    {
        using TemporaryAppSettingsFile file = new();
        await File.WriteAllTextAsync(file.Path,
            """{"Mcp":{"Port":4540,"BindAddress":"127.0.0.1","CorsEnabled":false,"AllowedOrigins":[],"AuthRequired":false,"Token":"","ToolOverrides":[{"ToolName":"patchouli.view","Enabled":true}],"DomainPermissions":[],"ShellCommandTimeoutSeconds":15,"ExposeLibraryTags":true,"ExposeLibraryCollections":true,"UpdatedAt":"2026-11-01T00:00:00Z","Revision":0}}""");
        McpServerSettingsService service = new(file.Path, new FixedClock(DateTimeOffset.UtcNow));
        Result<McpServerSettings> loaded = await service.GetSettingsAsync();
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        loaded.Value.DomainPermissions.Should().Contain(new McpDomainPermission("images", "fetch", true));
        McpPermissionPolicy.Allows(loaded.Value, "fetch", "patchouli://images/").Should().BeTrue();

        // The settings UI persists the migrated settings through the draft path.
        Result<McpServerSettings> saved =
            await service.SaveDraftSettingsAsync(loaded.Value, loaded.Value.Revision);
        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);

        Result<McpServerSettings> reloaded = await service.GetSettingsAsync();
        reloaded.IsSuccess.Should().BeTrue(reloaded.ErrorMessage);
        reloaded.Value.DomainPermissions.Should().Contain(new McpDomainPermission("images", "fetch", true));
        McpPermissionPolicy.Allows(reloaded.Value, "fetch", "patchouli://images/").Should().BeTrue();
    }

    [Fact]
    public void Migration_denies_images_fetch_when_texts_view_was_denied()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            ToolOverrides = [new McpToolOverride("patchouli.view", true)],
            DomainPermissions = [new McpDomainPermission("texts", "view", false)]
        };

        McpServerSettings migrated = McpServerSettingsService.NormalizeLegacySettings(settings);

        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("images", "fetch", false));
        migrated.DomainPermissions.Should().NotContain(value => value.Verb == "view");
        McpPermissionPolicy.Allows(migrated, "fetch", "patchouli://images/").Should().BeFalse();
    }

    [Fact]
    public void Migration_drops_view_verb_entries_and_the_view_tool_override()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            ToolOverrides = [new McpToolOverride("patchouli.view", false, "old deny")],
            DomainPermissions =
            [
                new McpDomainPermission("texts", "view", true),
                new McpDomainPermission("translations", "view", false)
            ]
        };

        McpServerSettings migrated = McpServerSettingsService.NormalizeLegacySettings(settings);

        migrated.ToolOverrides.Should().NotContain(value => value.ToolName == "patchouli.view");
        migrated.DomainPermissions.Should().NotContain(value => value.Verb == "view");
        // A user who never had view allowed must not gain image access through the
        // default-allow policy: the migration records an explicit denial.
        migrated.DomainPermissions.Should().Contain(new McpDomainPermission("images", "fetch", false));
        McpPermissionPolicy.Allows(migrated, "fetch", "patchouli://images/").Should().BeFalse();
    }
}
