using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
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
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Pride and Prejudice",
                Place: "London",
                Publisher: "Patchouli Press",
                Dates: [new ItemDateInput(ItemDateRoles.Issued, """[[1813]]""")],
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Austen", "Jane")]));

        Result<CslRenderResult> rendered =
            await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa", "en-US"));

        rendered.IsSuccess.Should().BeTrue();
        rendered.Value.RenderedText.Should().Contain("Austen").And.Contain("1813").And.Contain("Pride and Prejudice");
        rendered.Value.RenderedHtml.Should().Contain("<i>Pride and Prejudice</i>");
    }

    [Fact]
    public async Task Render_missing_year_still_returns_a_non_empty_bibliography()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Undated Book",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Literal: "Anonymous")]));

        Result<CslRenderResult> rendered =
            await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa"));

        rendered.IsSuccess.Should().BeTrue();
        rendered.Value.RenderedText.Should().Contain("Undated Book");
        rendered.Value.RenderedHtml.Should().Contain("<i>Undated Book</i>");
    }

    [Theory]
    [InlineData("legal_case")]
    [InlineData("motion_picture")]
    [InlineData("collection")]
    public async Task Expanded_item_types_render_with_default_style(string itemType)
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                itemType,
                $"Expanded {itemType} Title",
                Dates: [new ItemDateInput(ItemDateRoles.Issued, """[[2024]]""")],
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Doe", "Jane")]));

        Result<CslRenderResult> rendered =
            await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa", "en-US"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain($"Expanded {itemType} Title");
    }

    [Fact]
    public async Task Render_without_explicit_locale_uses_saved_settings_locale()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("locale-probe", "Locale Probe", "https://example.test/locale-probe.csl", "test"),
            LocaleProbeStyleXml());
        await context.Store.SaveSettingsAsync("locale-probe", "zh-CN");
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            new CreateItemRequest(
                "book",
                "Localized Book",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Literal: "Anonymous")]));

        Result<CslRenderResult> rendered =
            await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId]));

        rendered.IsSuccess.Should().BeTrue();
        rendered.Value.Locale.Should().Be("zh-CN");
        rendered.Value.RenderedText.Should().Contain("和");
    }

    [Fact]
    public async Task Render_general_item_is_blocked()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("general", "Not Ready");

        Result<CslRenderResult> rendered =
            await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "apa"));

        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("general_type_not_renderable");
    }

    [Fact]
    public async Task Render_invalid_style_returns_processor_diagnostics()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("broken", "Broken", "https://example.test/broken.csl", "test"),
            "<style>");
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Broken Style Item");

        Result<CslRenderResult> rendered =
            await context.Renderer.RenderAsync(new CslRenderRequest([item.Value.ItemId], "broken"));

        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be("csl_render_failed");
        rendered.ErrorMessage.Should().Contain("invalid-xml");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL Renderer");
        ItemService items = new(database.ConnectionFactory, library, clock);
        CslStyleStore store = new(database.ConnectionFactory, clock);
        await store.InstallStyleAsync(
            new CslCatalogStyle("apa", "APA", "https://example.test/apa.csl", "test"),
            ValidStyleXml("APA", "en-US"));
        return new TestContext(database, items, store, new CslRenderer(items, store, new CslItemMapper()));
    }

    private static string ValidStyleXml(string title, string locale)
    {
        return $"""
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
    }

    private static string LocaleProbeStyleXml()
    {
        return """
               <?xml version="1.0" encoding="utf-8"?>
               <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="en-US">
                 <info>
                   <title>Locale Probe</title>
                   <id>https://example.test/styles/locale-probe</id>
                 </info>
                 <citation>
                   <layout><text variable="title"/></layout>
                 </citation>
                 <bibliography>
                   <layout><text term="and"/></layout>
                 </bibliography>
               </style>
               """;
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, CslStyleStore store,
            CslRenderer renderer)
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

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
