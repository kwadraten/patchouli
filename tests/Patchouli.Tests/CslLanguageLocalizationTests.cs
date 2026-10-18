using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class CslLanguageLocalizationTests
{
    [Theory]
    [InlineData("English", "en")]
    [InlineData("eng", "en")]
    [InlineData("chi", "zh")]
    [InlineData("zho", "zh")]
    [InlineData("中文", "zh")]
    [InlineData("ZH-CN", "zh-CN")]
    [InlineData("EN_us", "en-US")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("ngerman", "de-DE")]
    [InlineData("american", "en-US")]
    [InlineData("Arabic", "ar")]
    [InlineData("ara", "ar")]
    [InlineData("Czech", "cs")]
    [InlineData("ces", "cs")]
    [InlineData("cze", "cs")]
    [InlineData("Danish", "da")]
    [InlineData("dan", "da")]
    [InlineData("Greek", "el")]
    [InlineData("ell", "el")]
    [InlineData("gre", "el")]
    [InlineData("Finnish", "fi")]
    [InlineData("fin", "fi")]
    [InlineData("Hebrew", "he")]
    [InlineData("heb", "he")]
    [InlineData("Hindi", "hi")]
    [InlineData("hin", "hi")]
    [InlineData("Hungarian", "hu")]
    [InlineData("hun", "hu")]
    [InlineData("Indonesian", "id")]
    [InlineData("ind", "id")]
    [InlineData("Italian", "it")]
    [InlineData("ita", "it")]
    [InlineData("Dutch", "nl")]
    [InlineData("nld", "nl")]
    [InlineData("dut", "nl")]
    [InlineData("Norwegian", "no")]
    [InlineData("nor", "no")]
    [InlineData("Polish", "pl")]
    [InlineData("pol", "pl")]
    [InlineData("Portuguese", "pt")]
    [InlineData("por", "pt")]
    [InlineData("Romanian", "ro")]
    [InlineData("ron", "ro")]
    [InlineData("rum", "ro")]
    [InlineData("Swedish", "sv")]
    [InlineData("swe", "sv")]
    [InlineData("Thai", "th")]
    [InlineData("tha", "th")]
    [InlineData("Turkish", "tr")]
    [InlineData("tur", "tr")]
    [InlineData("Ukrainian", "uk")]
    [InlineData("ukr", "uk")]
    [InlineData("Vietnamese", "vi")]
    [InlineData("vie", "vi")]
    [InlineData("unrecognized-value", "unrecognized-value")]
    [InlineData("AB-not A Known Language", "AB-not A Known Language")]
    [InlineData("zh--TW", "zh--TW")]
    public void Normalize_language_aliases_and_preserve_tags(string? input, string? expected)
    {
        ItemLanguage.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_empty_language_is_null(string? input)
    {
        ItemLanguage.Normalize(input).Should().BeNull();
    }

    [Fact]
    public async Task Matching_language_precedes_mapping_and_unmapped_items_use_the_style_default()
    {
        await using TestContext context = await CreateContextAsync();
        string style = ProbeStyle("""
                                  <layout locale="zh"><text value="CHINESE "/><text variable="title"/></layout>
                                  <layout locale="en"><text value="ENGLISH "/><text variable="title"/></layout>
                                  <layout><text value="STYLE-DEFAULT "/><text term="edition"/><text variable="title"/></layout>
                                  """).Replace("default-locale=\"zh-CN\"", "default-locale=\"fr-FR\"")
            .Replace("<citation>",
                "<locale xml:lang=\"fr-FR\"><terms><term name=\"edition\">FRENCH-DEFAULT</term></terms></locale><citation>");
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("native", "Native", "https://example.test/native.csl", "test"), style);
        ItemMetadata chinese = await CreateItemAsync(context, "匹配语言", "zh");
        ItemMetadata korean = await CreateItemAsync(context, "韩语默认规则", "ko");
        ItemMetadata japanese = await CreateItemAsync(context, "日语默认规则", "ja");
        CslSettings.DefaultLanguageLocaleMappings.Keys.Should().BeEquivalentTo(new[] { "ja", "ko" });
        Result<CslRenderResult> defaults = await context.Renderer.RenderAsync(new CslRenderRequest(
            [chinese.ItemId, korean.ItemId, japanese.ItemId], "native"));
        defaults.IsSuccess.Should().BeTrue(defaults.ErrorMessage);
        defaults.Value.RenderedText.Should().NotContain("STYLE-DEFAULT").And.NotContain("ENGLISH");
        defaults.Value.RenderedText.Should().Contain("CHINESE 匹配语言")
            .And.Contain("CHINESE 韩语默认规则").And.Contain("CHINESE 日语默认规则");

        await context.Store.SaveLanguageLocaleMappingsAsync(new Dictionary<string, string>());
        ItemMetadata unknown = await CreateItemAsync(context, "未配置语言", "ar");
        Result<CslRenderResult> fallback =
            await context.Renderer.RenderAsync(new CslRenderRequest([chinese.ItemId, unknown.ItemId], "native"));
        fallback.IsSuccess.Should().BeTrue(fallback.ErrorMessage);
        fallback.Value.RenderedText.Should().Contain("CHINESE 匹配语言").And.Contain("STYLE-DEFAULT FRENCH-DEFAULT")
            .And.NotContain("ENGLISH");
        await context.Store.SaveLanguageLocaleMappingsAsync(new Dictionary<string, string> { ["zh"] = "en-US" });
        Result<CslRenderResult> overridden =
            await context.Renderer.RenderAsync(new CslRenderRequest([chinese.ItemId], "native"));
        overridden.IsSuccess.Should().BeTrue(overridden.ErrorMessage);
        overridden.Value.RenderedText.Should().Contain("CHINESE 匹配语言").And.NotContain("ENGLISH");
        await context.Store.SaveLanguageLocaleMappingsAsync(new Dictionary<string, string> { ["ar"] = "ko-KR" });
        Result<CslRenderResult> unavailable =
            await context.Renderer.RenderAsync(new CslRenderRequest([unknown.ItemId], "native"));
        unavailable.IsSuccess.Should().BeTrue(unavailable.ErrorMessage);
        unavailable.Value.RenderedText.Should().Contain("STYLE-DEFAULT FRENCH-DEFAULT").And.NotContain("ENGLISH");
    }

    [Fact]
    public async Task Plain_single_layout_maps_each_item_and_preserves_language_variable_and_sorting()
    {
        await using TestContext context = await CreateContextAsync();
        string plainStyle = ProbeStyle("""
                                       <sort><key variable="language"/></sort>
                                       <layout>
                                         <text variable="language" prefix="[" suffix="] "/>
                                         <names variable="author"><name and="text" delimiter=", "/></names>
                                         <text variable="title" prefix=": "/>
                                       </layout>
                                       """).Replace("<bibliography>",
            "<bibliography et-al-min=\"2\" et-al-use-first=\"1\">");
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("probe", "Probe", "https://example.test/probe.csl", "test"), plainStyle);
        await context.Store.SaveSettingsAsync("probe", "zh-CN");
        await context.Store.SaveLanguageLocaleMappingsAsync(
            new Dictionary<string, string>(CslSettings.DefaultLanguageLocaleMappings)
            {
                ["fr"] = "en-US"
            });
        ItemMetadata japanese = await CreateItemAsync(context, "Japanese source", "ja");
        ItemMetadata english = await CreateItemAsync(context, "French source mapped to English", "fr");
        ItemMetadata korean = await CreateItemAsync(context, "Korean source", "ko");

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([japanese.ItemId, english.ItemId, korean.ItemId], "probe"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("[ja]").And.Contain("[fr]").And.Contain("[ko]")
            .And.Contain("等").And.Contain("et al.");
        rendered.Value.RenderedHtml.Should().Contain("等").And.Contain("et al.").And.Contain("[ko]");
        rendered.Value.RenderedText.IndexOf(english.Title, StringComparison.Ordinal)
            .Should().BeLessThan(rendered.Value.RenderedText.IndexOf("Japanese source", StringComparison.Ordinal));
        rendered.Value.RenderedHtml.Should().Contain("[ja]").And.Contain("[fr]").And.Contain("[ko]");
    }

    [Fact]
    public async Task Localized_author_displays_that_differ_are_not_substituted()
    {
        await using TestContext context = await CreateContextAsync();
        string styleXml = ProbeStyle("""
                                     <sort><key variable="title"/></sort>
                                     <layout locale="en-US"><names variable="author"><name name-as-sort-order="all"/></names><text term="et-al"/></layout>
                                     <layout><names variable="author"><name name-as-sort-order="all"/></names><text term="et-al"/></layout>
                                     """).Replace("<bibliography>",
            "<bibliography et-al-min=\"2\" et-al-use-first=\"1\" subsequent-author-substitute=\"—\">");
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("repeat", "Repeat author", "https://example.test/repeat.csl", "test"), styleXml);
        ItemMetadata japanese = await CreateItemAsync(context, "A Japanese title", "ja");
        ItemMetadata english = await CreateItemAsync(context, "B English title", "en");

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([japanese.ItemId, english.ItemId], "repeat"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("等").And.Contain("et al.")
            .And.Contain("Family1 Given1").And.Contain("Family1, Given1").And.NotContain("—");
        rendered.Value.ItemIds.Should().Equal(japanese.ItemId, english.ItemId);
    }

    [Fact]
    public async Task Explicit_csl_m_layout_and_reachable_locale_condition_keep_source_language_selectors()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("localized", "Localized", "https://example.test/localized.csl", "test"),
            ProbeStyle("""
                       <layout locale="en-US"><text value="EN "/><text macro="localized-term"/><text variable="title" prefix=" "/></layout>
                       <layout locale="ja-JP"><text value="JA "/><text macro="localized-term"/><text variable="title" prefix=" "/></layout>
                       <layout locale="zh-CN"><text value="ZH "/><text macro="localized-term"/><text variable="title" prefix=" "/></layout>
                       <layout><text value="DEFAULT "/><text macro="localized-term"/><text variable="title" prefix=" "/></layout>
                       """, """
                            <macro name="localized-term"><choose><if locale="en-US"><text value="EN-TERM"/></if><else-if locale="ja-JP"><text value="JA-TERM"/></else-if><else><text value="DEFAULT-TERM"/></else></choose></macro>
                            """));
        (await context.Store.SaveLanguageLocaleMappingsAsync(new Dictionary<string, string>
        {
            ["en"] = "zh-CN",
            ["ja"] = "zh-CN",
            ["zh"] = "en-US"
        })).IsSuccess.Should().BeTrue();
        ItemMetadata english = await CreateItemAsync(context, "English source", "eng");
        ItemMetadata japanese = await CreateItemAsync(context, "Japanese source", "ja");
        ItemMetadata chinese = await CreateItemAsync(context, "Chinese source", "zh");

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([english.ItemId, japanese.ItemId, chinese.ItemId], "localized"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("EN EN-TERM English source");
        rendered.Value.RenderedText.Should().Contain("JA JA-TERM Japanese source");
        rendered.Value.RenderedText.Should().Contain("ZH DEFAULT-TERM Chinese source");
    }

    [Fact]
    public async Task Greek_three_letter_alias_enters_matching_if_locale_branch()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("greek", "Greek", "https://example.test/greek.csl", "test"),
            ProbeStyle(
                extraMacro:
                "<macro name=\"localized-term\"><choose><if locale=\"el-GR\"><text value=\"GREEK-TERM\"/></if><else><text value=\"DEFAULT-TERM\"/></else></choose></macro>"));
        ItemMetadata greek = await CreateItemAsync(context, "Greek source", "ell");

        greek.Language.Should().Be("el");
        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([greek.ItemId], "greek"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("GREEK-TERM").And.NotContain("DEFAULT-TERM");
    }

    [Fact]
    public async Task Unreferenced_or_citation_only_locale_macro_does_not_suppress_bibliography_mapping()
    {
        await using TestContext context = await CreateContextAsync();
        string styleXml = ProbeStyle(
                "<layout><text term=\"et-al\"/><text variable=\"title\"/></layout>",
                "<macro name=\"unused-locale\"><choose><if locale=\"en-US\"><text value=\"CITATION-EN\"/></if></choose></macro>")
            .Replace("<citation><layout><text variable=\"title\"/></layout></citation>",
                "<citation><layout><text macro=\"unused-locale\"/></layout></citation>", StringComparison.Ordinal);
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("scope", "Scope", "https://example.test/scope.csl", "test"), styleXml);
        await context.Store.SaveLanguageLocaleMappingsAsync(new Dictionary<string, string> { ["en"] = "zh-CN" });
        ItemMetadata english = await CreateItemAsync(context, "English source", "en-US");

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([english.ItemId], "scope"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("等").And.NotContain("et al.");
        rendered.Value.RenderedText.Should().Contain("English source");
    }

    [Fact]
    public async Task Specific_region_layout_is_not_treated_as_a_different_region_match()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("regional", "Regional", "https://example.test/regional.csl", "test"),
            ProbeStyle("""
                       <layout locale="zh-CN"><text value="CN-TERM "/></layout>
                       <layout><text term="et-al"/><text variable="title"/></layout>
                       """));
        await context.Store.SaveLanguageLocaleMappingsAsync(new Dictionary<string, string> { ["zh"] = "zh-TW" });
        ItemMetadata traditional = await CreateItemAsync(context, "Traditional Chinese", "zh-Hant-TW");

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([traditional.ItemId], "regional"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("等").And.NotContain("CN-TERM");
    }

    [Fact]
    public async Task Missing_branches_and_missing_language_use_the_styles_english_default()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("fr-only", "French only", "https://example.test/fr.csl", "test"),
            ProbeStyle(
                    "<layout locale=\"fr-FR\"><text macro=\"localized-term\"/><text variable=\"title\"/></layout><layout><text macro=\"localized-term\"/><text variable=\"title\"/></layout>")
                .Replace("default-locale=\"zh-CN\"", "default-locale=\"en-US\""));
        ItemMetadata chinese = await CreateItemAsync(context, "中文题名 in English context", "zh");
        ItemMetadata unknown = await CreateItemAsync(context, "English title from unknown language", "Martian");
        ItemMetadata missing = await CreateItemAsync(context, "English title with no language", null);

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([chinese.ItemId, unknown.ItemId, missing.ItemId], "fr-only"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("EN-TERM").And.Contain(chinese.Title)
            .And.Contain(unknown.Title).And.Contain(missing.Title).And.NotContain("ZH-TERM");
    }

    [Fact]
    public async Task Real_gbt_bilingual_style_keeps_chinese_and_english_in_one_numbered_render()
    {
        await using TestContext context = await CreateContextAsync();
        string styleXml = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Csl", "GB-T-7714-2015-bilingual.csl"));
        await context.Store.InstallStyleAsync(
            new CslCatalogStyle("gbt-bilingual", "GB/T 7714-2015 bilingual", "https://example.test/gbt.csl", "fixture"),
            styleXml);
        await context.Store.SaveSettingsAsync("gbt-bilingual", "zh-CN");
        ItemMetadata chinese = await CreateItemAsync(context, "中文题名", "zh", 4);
        ItemMetadata english = await CreateItemAsync(context, "An English title", "English", 4);

        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([chinese.ItemId, english.ItemId], "gbt-bilingual"));

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("等").And.Contain("et al.").And.Contain("[1]").And.Contain("[2]");
        rendered.Value.RenderedHtml.Should().Contain("等").And.Contain("et al.");
        rendered.Value.ItemIds.Should().Equal(chinese.ItemId, english.ItemId);

        Result<CslRenderResult> explicitEnglish = await context.Renderer.RenderAsync(
            new CslRenderRequest([chinese.ItemId, english.ItemId], "gbt-bilingual", "en-US"));
        explicitEnglish.IsSuccess.Should().BeTrue(explicitEnglish.ErrorMessage);
        explicitEnglish.Value.RenderedText.Should().Contain("等").And.Contain("et al.")
            .And.Contain(chinese.Title).And.Contain(english.Title);
    }

    private static async Task<ItemMetadata> CreateItemAsync(TestContext context, string title, string? language,
        int creatorCount = 2)
    {
        ItemCreatorInput[] creators = Enumerable.Range(1, creatorCount)
            .Select(index => new ItemCreatorInput(ItemCreatorRoles.Author, $"Family{index}", $"Given{index}"))
            .ToArray();
        Result<ItemMetadata> created = await context.Items.CreateItemAsync(new CreateItemRequest(
            "book", title, Language: language, Edition: "2", Creators: creators));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        return created.Value;
    }

    private static string ProbeStyle(string? bibliography = null, string? extraMacro = null)
    {
        string output = bibliography ?? "<layout><text macro=\"localized-term\"/><text variable=\"title\"/></layout>";
        string macros = extraMacro ??
                        "<macro name=\"localized-term\"><choose><if locale=\"en-US\"><text value=\"EN-TERM\"/></if><else><text value=\"ZH-TERM\"/></else></choose></macro>";
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="zh-CN">
                  <info><title>Probe</title><id>https://example.test/styles/probe</id></info>
                  <locale xml:lang="en-US"><terms><term name="et-al">et al.</term><term name="edition" form="short">ed.</term></terms></locale>
                  <locale xml:lang="zh-CN"><terms><term name="et-al">等</term><term name="edition" form="short">版</term></terms></locale>
                  {macros}
                  <citation><layout><text variable="title"/></layout></citation>
                  <bibliography>{output}</bibliography>
                </style>
                """;
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL Language Localization");
        ItemService items = new(database.ConnectionFactory, library, clock);
        CslStyleStore store = new(database.ConnectionFactory, clock);
        CslRenderer renderer = new(items, store, new CslItemMapper());
        return new TestContext(database, items, store, renderer);
    }

    private sealed class TestContext(
        TemporarySqliteDatabase database,
        ItemService items,
        CslStyleStore store,
        CslRenderer renderer) : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; } = database;
        public ItemService Items { get; } = items;
        public CslStyleStore Store { get; } = store;
        public CslRenderer Renderer { get; } = renderer;

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
