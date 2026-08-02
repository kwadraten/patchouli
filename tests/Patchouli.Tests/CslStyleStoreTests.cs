using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Core.Library;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;

namespace Patchouli.Tests;

public sealed class CslStyleStoreTests
{
    [Fact]
    public async Task Install_update_disable_remove_and_settings_round_trip()
    {
        await using TestContext context = await CreateContextAsync();
        Result<CslStyle> first = await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "American Psychological Association", "en-US"));
        Result<CslStyle> second = await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA 7th", "en-US"));
        Result<IReadOnlyList<CslStyle>> listed = await context.Store.ListInstalledStylesAsync();
        Result<string> content = await context.Store.GetStyleContentAsync("apa");
        Result<CslSettings> settings = await context.Store.SaveSettingsAsync("apa", "zh-CN");
        Result<CslStyle> disabled = await context.Store.DisableStyleAsync("apa");
        Result remove = await context.Store.RemoveStyleAsync("apa");
        Result<IReadOnlyList<CslStyle>> afterRemove = await context.Store.ListInstalledStylesAsync();

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.DisplayName.Should().Be("APA 7th");
        listed.Value.Should().ContainSingle();
        content.Value.Should().Contain("APA 7th");
        settings.Value.DefaultStyleId.Should().Be("apa");
        settings.Value.Locale.Should().Be("zh-CN");
        disabled.Value.Enabled.Should().BeFalse();
        remove.IsSuccess.Should().BeTrue();
        afterRemove.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_style_cannot_become_default()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA", "en-US"));
        await context.Store.DisableStyleAsync("apa");

        Result<CslSettings> result = await context.Store.SaveSettingsAsync("apa", "en-US");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Style_writes_increment_and_publish_the_library_revision_only_after_commit()
    {
        await using TestContext context = await CreateContextAsync();
        List<LibraryChangeSet> changes = [];
        context.Revisions.ChangeCommitted += (_, args) => changes.Add(args.ChangeSet);
        long revisionBeforeWrites = (await context.Revisions.GetCurrentRevisionAsync()).Value;

        CslStyle installed = (await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA", "en-US"))).Value;
        Result<CslStyle> stale = await context.Store.ReplaceStyleAsync("apa",
            StyleXml("apa", "Stale", "en-US"), "style:not-the-current-hash");
        CslStyle replaced = (await context.Store.ReplaceStyleAsync("apa",
            StyleXml("apa", "APA updated", "en-US"), $"style:{installed.ContentHash}")).Value;
        await context.Store.DisableStyleAsync("apa");
        await context.Store.RemoveStyleAsync("apa");

        stale.IsFailure.Should().BeTrue();
        replaced.DisplayName.Should().Be("APA updated");
        changes.Should().HaveCount(4);
        changes.Should().OnlyContain(change => change.StyleIds.Single() == "apa");
        (await context.Revisions.GetCurrentRevisionAsync()).Value.Should().Be(revisionBeforeWrites + 4);
    }

    [Fact]
    public async Task Default_storage_root_isolated_per_database()
    {
        await using TemporarySqliteDatabase firstDatabase = TemporarySqliteDatabase.Create();
        await using TemporarySqliteDatabase secondDatabase = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(firstDatabase.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await new MigrationRunner(secondDatabase.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        LibraryIdentityService firstLibrary = new(firstDatabase.ConnectionFactory, clock);
        LibraryIdentityService secondLibrary = new(secondDatabase.ConnectionFactory, clock);
        await firstLibrary.CreateLibraryAsync("First CSL Styles");
        await secondLibrary.CreateLibraryAsync("Second CSL Styles");

        CslStyleStore firstStore = new(firstDatabase.ConnectionFactory, clock);
        CslStyleStore secondStore = new(secondDatabase.ConnectionFactory, clock);
        await firstStore.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA From First", "en-US"));
        await secondStore.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA From Second", "zh-CN"));

        Result<string> firstContent = await firstStore.GetStyleContentAsync("apa");
        Result<string> secondContent = await secondStore.GetStyleContentAsync("apa");

        firstContent.IsSuccess.Should().BeTrue();
        secondContent.IsSuccess.Should().BeTrue();
        firstContent.Value.Should().Contain("APA From First").And.NotContain("APA From Second");
        secondContent.Value.Should().Contain("APA From Second").And.NotContain("APA From First");
    }

    [Fact]
    public async Task Invalid_install_records_failed_blocking_operation()
    {
        await using TestContext context = await CreateContextAsync();

        Result<CslStyle> result = await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            "");
        Result<IReadOnlyList<BlockingOperation>> operations = await context.BlockingOperations.ListAsync(
            BlockingOperationStatus.Failed,
            BlockingOperationTypes.CslStyleInstall,
            BlockingOperationScopeTypes.CslStyle,
            "apa");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("validation_failed");
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().FailureMessage.Should().Be("CSL style content is required.");
        operations.Value.Single().ProgressLabel.Should().Be("CSL style installation failed.");
    }

    [Fact]
    public async Task Successful_install_records_completed_blocking_operation()
    {
        await using TestContext context = await CreateContextAsync();

        Result<CslStyle> installed = await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA", "en-US"));
        Result<IReadOnlyList<BlockingOperation>> operations = await context.BlockingOperations.ListAsync(
            BlockingOperationStatus.Completed,
            BlockingOperationTypes.CslStyleInstall,
            BlockingOperationScopeTypes.CslStyle,
            "apa");

        installed.IsSuccess.Should().BeTrue();
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().ProgressLabel.Should().Be("Installed CSL style 'apa'.");
        operations.Value.Single().NextActions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/style")]
    [InlineData("nested\\style")]
    public async Task Style_ids_cannot_escape_the_installed_directory(string styleId)
    {
        await using TestContext context = await CreateContextAsync();

        Result<CslStyle> installed = await context.Store.InstallStyleAsync(
            new CslCatalogStyle(styleId, "Bad Style", "https://example.test/bad.csl", "test"),
            StyleXml("bad-style", "Bad Style", "en-US"));

        installed.IsFailure.Should().BeTrue();
        installed.ErrorCode.Should().Be("validation_failed");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL Styles");
        BlockingOperationService blockingOperations = new(database.ConnectionFactory, clock);
        LibraryRevisionService revisions = new(database.ConnectionFactory);
        return new TestContext(
            database,
            new CslStyleStore(database.ConnectionFactory, clock, blockingOperations: blockingOperations,
                revisions: revisions),
            blockingOperations,
            revisions);
    }

    private static string StyleXml(string id, string title, string locale)
    {
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="{locale}">
                  <info>
                    <title>{title}</title>
                    <id>https://example.test/styles/{id}</id>
                  </info>
                  <citation>
                    <layout prefix="(" suffix=")" delimiter="; ">
                      <names variable="author">
                        <name and="text" delimiter=", " initialize-with=". "/>
                      </names>
                    </layout>
                  </citation>
                  <bibliography>
                    <layout suffix=".">
                      <group delimiter=" ">
                        <names variable="author">
                          <name and="text" delimiter=", " sort-separator=", " initialize-with=". "/>
                        </names>
                        <date variable="issued" prefix=" (" suffix=")">
                          <date-part name="year"/>
                        </date>
                        <text variable="title" font-style="italic"/>
                      </group>
                    </layout>
                  </bibliography>
                </style>
                """;
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            TemporarySqliteDatabase database,
            CslStyleStore store,
            IBlockingOperationService blockingOperations,
            ILibraryRevisionService revisions)
        {
            Database = database;
            Store = store;
            BlockingOperations = blockingOperations;
            Revisions = revisions;
        }

        public TemporarySqliteDatabase Database { get; }
        public CslStyleStore Store { get; }
        public IBlockingOperationService BlockingOperations { get; }
        public ILibraryRevisionService Revisions { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
