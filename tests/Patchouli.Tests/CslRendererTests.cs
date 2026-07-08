using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class CslRendererTests
{
    [Fact]
    public async Task Render_success_produces_text_and_html()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Pride and Prejudice",
                Place: "London",
                Publisher: "Patchouli Press",
                Dates: [new ItemDateInput(ItemDateRoles.Issued, """[[1813]]""")],
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Family: "Austen", Given: "Jane")]));

        var rendered = await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa", "en-US"));

        rendered.IsSuccess.Should().BeTrue();
        rendered.Value.RenderedText.Should().Contain("Austen").And.Contain("1813").And.Contain("Pride and Prejudice");
        rendered.Value.RenderedHtml.Should().Contain("<i>Pride and Prejudice</i>");
    }

    [Fact]
    public async Task Render_missing_year_still_returns_a_non_empty_bibliography()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Undated Book",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Literal: "Anonymous")]));

        var rendered = await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa"));

        rendered.IsSuccess.Should().BeTrue();
        rendered.Value.RenderedText.Should().Contain("Undated Book");
        rendered.Value.RenderedHtml.Should().Contain("<i>Undated Book</i>");
    }

    [Fact]
    public async Task Render_without_explicit_locale_uses_saved_settings_locale()
    {
        await using var context = await CreateContextAsync();
        await context.Store.SaveSettingsAsync("apa", "zh-CN");
        var item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Localized Book",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Literal: "Anonymous")]));

        var rendered = await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa"));

        rendered.IsSuccess.Should().BeTrue();
        rendered.Value.Locale.Should().Be("zh-CN");
    }

    [Fact]
    public async Task Render_general_item_is_blocked()
    {
        await using var context = await CreateContextAsync();
        var item = await context.Items.CreateItemAsync("general", "Not Ready");

        var rendered = await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa"));

        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("general_type_not_renderable");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var database = TemporarySqliteDatabase.Create();
        var clock = new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        var library = new LibraryIdentityService(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL Renderer");
        var items = new ItemService(database.ConnectionFactory, library, clock);
        var store = new CslStyleStore(database.ConnectionFactory, clock);
        await store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            ValidStyleXml("APA", "en-US"));
        return new TestContext(database, items, store, new CslRenderer(items, store, new CslItemMapper()));
    }

    private static string ValidStyleXml(string title, string locale)
        => $"""
           <?xml version="1.0" encoding="utf-8"?>
           <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="{locale}">
             <info>
               <title>{title}</title>
               <id>https://example.test/styles/{title.ToLowerInvariant().Replace(' ', '-')}</id>
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

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, CslStyleStore store, CslRenderer renderer)
        {
            Database = database;
            Items = items;
            Store = store;
            Renderer = renderer;
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public CslStyleStore Store { get; }
        public CslRenderer Renderer { get; }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
