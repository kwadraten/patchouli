using System.Text.RegularExpressions;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class CslWholeListLocalizationTests
{
    private static readonly IReadOnlyDictionary<string, string> LocaleMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zh"] = "zh-CN",
            ["ja"] = "ja-JP",
            ["ko"] = "ko-KR",
            ["en"] = "en-US"
        };

    [Fact]
    public async Task Numeric_bibliography_numbers_remain_continuous_across_language_layouts()
    {
        const string styleId = "whole-list-numeric";
        await using TestContext context = await CreateContextAsync(styleId, BuildStyle(
            "<sort><key variable=\"citation-number\"/></sort>",
            "<citation><layout><text variable=\"title\"/></layout></citation>",
            """
            <layout locale="zh-CN"><text value="ZH "/><text variable="citation-number" prefix="[" suffix="] "/><text variable="title"/></layout>
            <layout locale="ja-JP"><text value="JA "/><text variable="citation-number" prefix="[" suffix="] "/><text variable="title"/></layout>
            <layout locale="ko-KR"><text value="KO "/><text variable="citation-number" prefix="[" suffix="] "/><text variable="title"/></layout>
            <layout locale="en-US"><text value="EN "/><text variable="citation-number" prefix="[" suffix="] "/><text variable="title"/></layout>
            """));

        ItemMetadata chinese = await CreateItemAsync(context, "Chinese entry", "zh");
        ItemMetadata japanese = await CreateItemAsync(context, "Japanese entry", "ja");
        ItemMetadata korean = await CreateItemAsync(context, "Korean entry", "ko");
        ItemMetadata english = await CreateItemAsync(context, "English entry", "en");

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([chinese.ItemId, japanese.ItemId, korean.ItemId, english.ItemId], styleId));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        AssertContinuousNumbers(rendered.Value.RenderedText);
        AssertContinuousNumbers(rendered.Value.RenderedHtml);
        foreach (string marker in new[] { "ZH", "JA", "KO", "EN" })
        {
            rendered.Value.RenderedText.Should().Contain(marker);
            rendered.Value.RenderedHtml.Should().Contain(marker);
        }

        foreach (string title in new[] { chinese.Title, japanese.Title, korean.Title, english.Title })
        {
            rendered.Value.RenderedText.Should().Contain(title);
            rendered.Value.RenderedHtml.Should().Contain(title);
        }
    }

    [Fact]
    public async Task Year_suffix_disambiguation_is_assigned_across_localized_layouts()
    {
        const string styleId = "whole-list-year-suffix";
        const string dateAndSuffix = """
                                     <names variable="author"><name/></names>
                                     <date variable="issued" prefix=" "><date-part name="year"/></date>
                                     <text variable="year-suffix"/>
                                     """;
        await using TestContext context = await CreateContextAsync(styleId, BuildStyle(
            "<sort><key variable=\"author\"/><key variable=\"issued\"/><key variable=\"title\"/></sort>",
            $"<citation disambiguate-add-year-suffix=\"true\"><layout>{dateAndSuffix}</layout></citation>",
            $"""
             <layout locale="zh-CN"><text value="ZH "/>{dateAndSuffix}<text variable="title" prefix=" "/></layout>
             <layout locale="en-US"><text value="EN "/>{dateAndSuffix}<text variable="title" prefix=" "/></layout>
             """));

        ItemMetadata chinese = await CreateItemAsync(context, "Alpha title", "zh", "Shared Author", 2024);
        ItemMetadata english = await CreateItemAsync(context, "Beta title", "en", "Shared Author", 2024);

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([chinese.ItemId, english.ItemId], styleId));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("ZH Shared Author 2024a Alpha title");
        rendered.Value.RenderedText.Should().Contain("EN Shared Author 2024b Beta title");
        rendered.Value.RenderedHtml.Should().Contain("ZH Shared Author 2024a Alpha title");
        rendered.Value.RenderedHtml.Should().Contain("EN Shared Author 2024b Beta title");
    }

    [Fact]
    public async Task Subsequent_author_substitute_spans_language_layouts_in_one_bibliography()
    {
        const string styleId = "whole-list-author-substitute";
        await using TestContext context = await CreateContextAsync(styleId, BuildStyle(
            "<sort><key variable=\"author\"/><key variable=\"issued\"/></sort>",
            "<citation><layout><text variable=\"title\"/></layout></citation>",
            """
            <layout locale="zh-CN"><text value="ZH "/><names variable="author"><name/></names><date variable="issued" prefix=", "><date-part name="year"/></date><text variable="title" prefix=": "/></layout>
            <layout locale="en-US"><text value="EN "/><names variable="author"><name/></names><date variable="issued" prefix=", "><date-part name="year"/></date><text variable="title" prefix=": "/></layout>
            """,
            "subsequent-author-substitute=\"SAME AUTHOR\" subsequent-author-substitute-rule=\"complete-all\""));

        ItemMetadata chinese = await CreateItemAsync(context, "First title", "zh", "Shared Author", 2020);
        ItemMetadata english = await CreateItemAsync(context, "Second title", "en", "Shared Author", 2021);

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([chinese.ItemId, english.ItemId], styleId));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("ZH Shared Author, 2020: First title");
        rendered.Value.RenderedText.Should().Contain("EN SAME AUTHOR, 2021: Second title");
        rendered.Value.RenderedHtml.Should().Contain("ZH Shared Author, 2020: First title");
        rendered.Value.RenderedHtml.Should().Contain("EN SAME AUTHOR, 2021: Second title");
    }

    private static void AssertContinuousNumbers(string output)
    {
        MatchCollection matches = Regex.Matches(output, @"\[(\d+)\]");
        matches.Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Order()
            .Should()
            .Equal(1, 2, 3, 4);
    }

    private static async Task<ItemMetadata> CreateItemAsync(TestContext context, string title, string language,
        string family = "Author", int? year = null)
    {
        ItemDateInput[]? dates = year is null
            ? null
            : [new ItemDateInput(ItemDateRoles.Issued, $"[[{year.Value}]]")];
        Result<ItemMetadata> created = await context.Items.CreateItemAsync(new CreateItemRequest(
            "book",
            title,
            Language: language,
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, family)],
            Dates: dates));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        return created.Value;
    }

    private static string BuildStyle(string sort, string citation, string layouts,
        string bibliographyAttributes = "")
    {
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="en-US">
                  <info><title>Whole list localization probe</title><id>https://example.test/whole-list</id></info>
                  {citation}
                  <bibliography {bibliographyAttributes}>{sort}{layouts}<layout><text value="FALLBACK "/><text variable="title"/></layout></bibliography>
                </style>
                """;
    }

    private static async Task<TestContext> CreateContextAsync(string styleId, string styleXml)
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        try
        {
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            await library.CreateLibraryAsync("CSL whole-list localization");
            ItemService items = new(database.ConnectionFactory, library, clock);
            CslStyleStore store = new(database.ConnectionFactory, clock);
            CslRenderer renderer = new(items, store, new CslItemMapper());
            Result<CslStyle> installed = await store.InstallStyleAsync(
                new CslCatalogStyle(styleId, "Whole list localization probe", "https://example.test/whole-list.csl",
                    "test"),
                styleXml);
            installed.IsSuccess.Should().BeTrue(installed.ErrorMessage);
            Result<CslSettings> mappingsSaved = await store.SaveLanguageLocaleMappingsAsync(LocaleMappings);
            mappingsSaved.IsSuccess.Should().BeTrue(mappingsSaved.ErrorMessage);
            return new TestContext(database, items, store, renderer);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private sealed class TestContext(
        TemporarySqliteDatabase database,
        ItemService items,
        CslStyleStore store,
        CslRenderer renderer) : IAsyncDisposable
    {
        public ItemService Items { get; } = items;
        public CslStyleStore Store { get; } = store;
        public CslRenderer Renderer { get; } = renderer;

        public ValueTask DisposeAsync()
        {
            return database.DisposeAsync();
        }
    }
}
