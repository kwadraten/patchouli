using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class CslStyleStoreTests
{
    [Fact]
    public async Task Install_update_disable_remove_and_settings_round_trip()
    {
        await using var context = await CreateContextAsync();
        var first = await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "American Psychological Association", "en-US"));
        var second = await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA 7th", "en-US"));
        var listed = await context.Store.ListInstalledStylesAsync();
        var content = await context.Store.GetStyleContentAsync("apa");
        var settings = await context.Store.SaveSettingsAsync("apa", "zh-CN");
        var disabled = await context.Store.DisableStyleAsync("apa");
        var remove = await context.Store.RemoveStyleAsync("apa");
        var afterRemove = await context.Store.ListInstalledStylesAsync();

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
        await using var context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA", "en-US"));
        await context.Store.DisableStyleAsync("apa");

        var result = await context.Store.SaveSettingsAsync("apa", "en-US");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Default_storage_root_isolated_per_database()
    {
        await using var firstDatabase = TemporarySqliteDatabase.Create();
        await using var secondDatabase = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(firstDatabase.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await new MigrationRunner(secondDatabase.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        var firstLibrary = new LibraryIdentityService(firstDatabase.ConnectionFactory, clock);
        var secondLibrary = new LibraryIdentityService(secondDatabase.ConnectionFactory, clock);
        await firstLibrary.CreateLibraryAsync("First CSL Styles");
        await secondLibrary.CreateLibraryAsync("Second CSL Styles");

        var firstStore = new CslStyleStore(firstDatabase.ConnectionFactory, clock);
        var secondStore = new CslStyleStore(secondDatabase.ConnectionFactory, clock);
        await firstStore.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA From First", "en-US"));
        await secondStore.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            StyleXml("apa", "APA From Second", "zh-CN"));

        var firstContent = await firstStore.GetStyleContentAsync("apa");
        var secondContent = await secondStore.GetStyleContentAsync("apa");

        firstContent.IsSuccess.Should().BeTrue();
        secondContent.IsSuccess.Should().BeTrue();
        firstContent.Value.Should().Contain("APA From First").And.NotContain("APA From Second");
        secondContent.Value.Should().Contain("APA From Second").And.NotContain("APA From First");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL Styles");
        return new TestContext(database, new CslStyleStore(database.ConnectionFactory, clock));
    }

    private static string StyleXml(string id, string title, string locale)
        => $"""
           <style xmlns="http://purl.org/net/xbiblio/csl" version="1.0" default-locale="{locale}">
             <info>
               <title>{title}</title>
               <id>https://example.test/styles/{id}</id>
             </info>
           </style>
           """;

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, CslStyleStore store)
        {
            Database = database;
            Store = store;
        }

        public TemporarySqliteDatabase Database { get; }
        public CslStyleStore Store { get; }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
