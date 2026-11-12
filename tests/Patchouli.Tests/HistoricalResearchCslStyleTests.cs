using System.Text.Json;
using FluentAssertions;
using Fsharp.Citeproc;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Host.Composition;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class HistoricalResearchCslStyleTests
{
    [Fact]
    public void Style_has_the_required_identity_and_valid_xml()
    {
        string xml = BuiltInCslStyles.GetContent(BuiltInCslStyles.HistoricalResearchId);

        Result<CslLocalStyleInfo> parsed = CslStyleXmlParser.Parse(xml);

        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        parsed.Value.StyleId.Should().Be(BuiltInCslStyles.HistoricalResearchId);
        parsed.Value.Title.Should().Be("历史研究杂志社规定样式");
        parsed.Value.DefaultLocale.Should().Be("zh-CN");
        parsed.Value.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public async Task Built_in_style_install_is_idempotent()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-11-11T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await new LibraryIdentityService(database.ConnectionFactory, clock).CreateLibraryAsync("Built-in CSL");
        CslStyleStore store = new(database.ConnectionFactory, clock);

        (await store.EnsureBuiltInStylesAsync()).IsSuccess.Should().BeTrue();
        CslStyle first = (await store.GetStyleAsync(BuiltInCslStyles.HistoricalResearchId)).Value;
        (await store.EnsureBuiltInStylesAsync()).IsSuccess.Should().BeTrue();
        CslStyle second = (await store.GetStyleAsync(BuiltInCslStyles.HistoricalResearchId)).Value;

        first.DisplayName.Should().Be("历史研究杂志社规定样式");
        first.SourceKind.Should().Be("built-in");
        second.ContentHash.Should().Be(first.ContentHash);
        second.UpdatedAt.Should().Be(first.UpdatedAt);
    }

    [Fact]
    public async Task Host_startup_exposes_the_built_in_style()
    {
        using TemporaryAppSettingsFile settings = new();
        PatchouliAppSettings options = PatchouliAppSettings.Load(settings.Path);
        HostServices services =
            await HostServices.CreateAsync(options.Runtime.RuntimeDatabasePath, options, settings.Path);
        try
        {
            Result<CslStyle> style = await services.CslStore.GetStyleAsync(BuiltInCslStyles.HistoricalResearchId);

            style.IsSuccess.Should().BeTrue(style.ErrorMessage);
            style.Value.DisplayName.Should().Be("历史研究杂志社规定样式");
            style.Value.Enabled.Should().BeTrue();
        }
        finally
        {
            await services.ShutdownAsync();
        }
    }

    [Fact]
    public void Chinese_books_support_translators_missing_dates_volumes_and_original_dates()
    {
        Render(Item("book", "中国人留学日本史", "zh",
                Literals("author", "实藤惠秀"), Literals("translator", "谭汝谦", "林启彦"),
                ("publisher-place", "香港"), ("publisher", "中文大学出版社"), ("issued", Date(1982))), "11-12")
            .Should().Be("实藤惠秀：《中国人留学日本史》，谭汝谦、林启彦译，香港：中文大学出版社，1982年，第11—12页。");

        Render(Item("book", "平等阁笔记", "zh", Literals("author", "狄葆贤"),
                ("publisher-place", "上海"), ("publisher", "有正书局")), "8")
            .Should().Be("狄葆贤：《平等阁笔记》，上海：有正书局，出版时间不详，第8页。");

        Render(Item("book", "马克思恩格斯全集", "zh", ("volume", "31"),
                ("publisher-place", "北京"), ("publisher", "人民出版社"), ("issued", Date(1998))), "46")
            .Should().Be("《马克思恩格斯全集》第31卷，北京：人民出版社，1998年，第46页。");

        Render(Item("book", "余绍宋日记", "zh", ("volume", "2"),
                ("original-date", Date(1920, 12, 10)), ("publisher-place", "北京"),
                ("publisher", "国家图书馆出版社"), ("issued", Date(2003)), ("medium", "影印版")), "101")
            .Should().Be("《余绍宋日记》第2卷，1920年12月10日，北京：国家图书馆出版社，2003年影印版，第101页。");
    }

    [Fact]
    public void Chinese_chapters_and_classics_render_container_and_series_data()
    {
        Render(Item("chapter", "走向新世界主义", "zh", Literals("author", "杜威·佛克马"),
                Literals("editor", "王宁", "薛晓源"), ("container-title", "全球化与后殖民批评"),
                ("publisher-place", "北京"), ("publisher", "中央编译出版社"), ("issued", Date(1999))), "247-266")
            .Should().Be("杜威·佛克马：《走向新世界主义》，王宁、薛晓源编：《全球化与后殖民批评》，北京：中央编译出版社，1999年，第247—266页。");

        Render(Item("chapter", "续问辨牍", "zh", Literals("author", "管志道"), ("chapter-number", "2"),
                ("container-title", "四库全书存目丛书"), ("collection-title", "子部"),
                ("collection-number", "第88册"), ("publisher-place", "济南"), ("publisher", "齐鲁书社"),
                ("issued", Date(1997)), ("medium", "影印本")), "73")
            .Should().Be("管志道：《续问辨牍》卷2，《四库全书存目丛书》《子部》第88册，济南：齐鲁书社，1997年影印本，第73页。");
    }

    [Fact]
    public void Chinese_periodicals_and_newspapers_cover_year_volume_issue_place_and_edition()
    {
        Render(Item("article-journal", "读顾诚《南明史》", "zh", Literals("author", "何龄修"),
                ("container-title", "中国史研究"), ("issued", Date(1998)), ("issue", "3")))
            .Should().Be("何龄修：《读顾诚《南明史》》，《中国史研究》1998年第3期。");

        Render(Item("article-journal", "阴阳五行说之来历", "zh", Literals("author", "梁启超"),
                ("container-title", "东方杂志"), ("volume", "20"), ("issue", "10"), ("issued", Date(1923))))
            .Should().Be("梁启超：《阴阳五行说之来历》，《东方杂志》第20卷第10号，1923年。");

        Render(Item("article-journal", "英国议会圈地及其影响", "zh", Literals("author", "叶明勇"),
                ("container-title", "武汉大学学报"), ("edition", "人文科学版"), ("issued", Date(2001)), ("issue", "2")))
            .Should().Be("叶明勇：《英国议会圈地及其影响》，《武汉大学学报》（人文科学版）2001年第2期。");

        Render(Item("article-newspaper", "李劼人轶事", "zh", Literals("author", "李眉"),
                ("container-title", "四川工人日报"), ("issued", Date(1986, 8, 22)), ("edition", "2")))
            .Should().Be("李眉：《李劼人轶事》，《四川工人日报》1986年8月22日，第2版。");

        Render(Item("article-newspaper", "上海各路商界总联合会致外交部电", "zh",
                ("container-title", "民国日报"), ("publisher-place", "上海"),
                ("issued", Date(1925, 8, 14)), ("edition", "4")))
            .Should().Be("《上海各路商界总联合会致外交部电》，《民国日报》（上海）1925年8月14日，第4版。");
    }

    [Fact]
    public void Chinese_unpublished_electronic_and_indirect_sources_are_supported()
    {
        Render(Item("thesis", "罗隆基政治思想研究（1913—1949）", "zh", Literals("author", "方明东"),
                ("genre", "博士学位论文"), ("publisher", "北京师范大学历史系"), ("issued", Date(2000))), "67")
            .Should().Be("方明东：《罗隆基政治思想研究（1913—1949）》，博士学位论文，北京师范大学历史系，2000年，第67页。");

        Render(Item("paper-conference", "对国际体制和国际制度的理解和翻译", "zh", Literals("author", "任东来"),
                ("genre", "全球化与亚太区域化国际研讨会论文"), ("publisher-place", "天津"),
                ("issued", Date(2000, 6))), "9")
            .Should().Be("任东来：《对国际体制和国际制度的理解和翻译》，全球化与亚太区域化国际研讨会论文，天津，2000年6月，第9页。");

        Render(Item("manuscript", "陆军大臣荫昌奏为遵议两江总督张人骏等电奏各省督练公所官制纲要拟酌量变通一折事", "zh",
                ("medium", "朱批奏折"), ("archive_location", "档号04—01—30—0146—023"),
                ("archive", "中国第一历史档案馆")))
            .Should().Be("《陆军大臣荫昌奏为遵议两江总督张人骏等电奏各省督练公所官制纲要拟酌量变通一折事》，朱批奏折，档号04—01—30—0146—023，中国第一历史档案馆藏。");

        Render(Item("webpage", "关于中国学术期刊标准化数据库系统工程的进展", "zh", Literals("author", "王明亮"),
                ("URL", "http://www.cajcd.cn/pub/wml.txt/980810-2.html"), ("accessed", Date(2025, 10, 4))))
            .Should().Be("王明亮：《关于中国学术期刊标准化数据库系统工程的进展》，http://www.cajcd.cn/pub/wml.txt/980810-2.html，访问日期：2025年10月4日。");

        Render(Item("book", "在长沙晨光学校演说", "zh", Literals("author", "章太炎"),
                ("issued", Date(1925, 10)), ("note", "转引自汤志钧：《章太炎年谱长编》下册，北京：中华书局，1979年")), "823")
            .Should().Contain("转引自汤志钧：《章太炎年谱长编》下册").And.EndWith("第823页。");
    }

    [Fact]
    public void English_books_follow_name_title_editor_translator_and_locator_rules()
    {
        Render(Item("book", "Troubling Confessions: Speaking Guilt in Law and Literature", "en",
                Names("author", ("Brooks", "Peter")), ("publisher-place", "Chicago"),
                ("publisher", "University of Chicago Press"), ("issued", Date(2000))), "48")
            .Should().Be(
                "Peter Brooks, Troubling Confessions: Speaking Guilt in Law and Literature, Chicago: University of Chicago Press, 2000, p. 48.");

        Render(Item("book", "Equal Subjects, Unequal Right: Indigenous Peoples in British Settler Societies", "en",
                Names("author", ("Evans", "Julie"), ("Smith", "A"), ("Jones", "B"), ("Brown", "C")),
                ("publisher-place", "Manchester"), ("publisher", "Manchester University Press"),
                ("issued", Date(2003))), "25")
            .Should().StartWith("Julie Evans et al., Equal Subjects").And.EndWith("2003, p. 25.");

        Render(Item("book", "The Travels of Marco Polo", "en", Names("author", ("Polo", "M.")),
                Names("translator", ("Marsden", "William")), ("publisher-place", "Hertfordshire"),
                ("publisher", "Cumberland House"), ("issued", Date(1997))), "55, 88")
            .Should().Be(
                "M. Polo, The Travels of Marco Polo, trans. William Marsden, Hertfordshire: Cumberland House, 1997, pp. 55, 88.");
    }

    [Fact]
    public void English_articles_chapters_theses_and_archives_render_their_own_patterns()
    {
        Render(Item("article-journal", "On the Search for Civil Society in China", "en",
                Names("author", ("Chamberlain", "Heath B.")), ("container-title", "Modern China"),
                ("volume", "19"), ("issue", "2"), ("issued", Date(1993))), "199-215")
            .Should().Be(
                "Heath B. Chamberlain, “On the Search for Civil Society in China,” Modern China, Vol. 19, No. 2, 1993, pp. 199-215.");

        Render(Item("chapter", "The Impact of Scarcity and Plenty on Population Change in England", "en",
                Names("author", ("Schofield", "R. S.")), Names("editor", ("Rotberg", "R. I."), ("Rabb", "T. K.")),
                ("container-title",
                    "Hunger and History: The Impact of Changing Food Production and Consumption Pattern on Society"),
                ("publisher-place", "Cambridge, MA"), ("publisher", "Cambridge University Press"),
                ("issued", Date(1983))), "79")
            .Should().StartWith(
                "R. S. Schofield, “The Impact of Scarcity and Plenty on Population Change in England,” in R. I. Rotberg and T. K. Rabb, eds., Hunger and History")
            .And.EndWith("1983, p. 79.");

        Render(Item("thesis", "Selling Democracy: The USIA’s Portrayal of American Race Relations, 1953–1976", "en",
                Names("author", ("Schwenk-Borrell", "Melinda")), ("publisher", "University of Pennsylvania"),
                ("issued", Date(2004))))
            .Should().Be(
                "Melinda Schwenk-Borrell, “Selling Democracy: The USIA’s Portrayal of American Race Relations, 1953–1976,” Ph.D. dissertation, University of Pennsylvania, 2004.");

        Render(Item("manuscript", "Nixon to Kissinger", "en", ("issued", Date(1969, 2, 1)),
                ("archive_location", "Box 1032, NSC Files"),
                ("archive", "Nixon Presidential Material Project (NPMP), National Archives II"),
                ("archive-place", "College Park, MD")))
            .Should().Be(
                "Nixon to Kissinger, Feb. 1, 1969, Box 1032, NSC Files, Nixon Presidential Material Project (NPMP), National Archives II, College Park, MD.");
    }

    [Fact]
    public void Japanese_books_chapters_journals_archives_and_webpages_use_japanese_punctuation()
    {
        Render(Item("book", "現代日本の産業政策", "ja", Literals("author", "小野五郎"),
                ("publisher-place", "東京"), ("publisher", "日本経済新聞社"), ("issued", Date(1999))), "27")
            .Should().Be("小野五郎：『現代日本の産業政策』、東京：日本経済新聞社、1999年、第27頁。");

        Render(Item("chapter", "言語情報の特質", "ja", Literals("author", "郡司隆男"),
                Literals("editor", "藤本和貴夫", "木村健治"), ("container-title", "言語文化学概論"),
                ("publisher-place", "大阪"), ("publisher", "大阪大学出版会"), ("issued", Date(1997))), "20")
            .Should().Be("郡司隆男：「言語情報の特質」、藤本和貴夫·木村健治編：『言語文化学概論』、大阪：大阪大学出版会、1997年、第20頁。");

        Render(Item("article-journal", "キャンパスの文章—卒業論文", "ja", Literals("author", "梅津彰人"),
                ("container-title", "国文学 解釈と教材の研究"), ("volume", "40"), ("issue", "2"),
                ("issued", Date(1955))), "71-74")
            .Should().Be("梅津彰人：「キャンパスの文章—卒業論文」、『国文学 解釈と教材の研究』第40巻第2号、1955年、第71—74頁。");

        Render(Item("manuscript", "参謀次長宛町田少将電報", "ja", ("issued", Date(1916, 2, 17)),
                ("container-title", "袁世凱帝制計画一件（極秘）／反袁動乱及各地状況"), ("volume", "4"),
                ("archive", "JACAR（アジア歴史資料センター）"), ("archive_location", "Ref. B03050721400")), "72")
            .Should().Be(
                "「参謀次長宛町田少将電報」（1916年2月17日）、『袁世凱帝制計画一件（極秘）／反袁動乱及各地状況』第4巻、第72頁、JACAR（アジア歴史資料センター）、Ref. B03050721400。");

        Render(Item("webpage", "経済産業省産業技術政策", "ja",
                Literals("author", "経済産業省産業技術環境局産業技術政策課"),
                ("URL", "http://www.meti.go.jp/policy/economy/gijutsu_kakushin/pamphlet.pdf"),
                ("accessed", Date(2025, 8, 6))))
            .Should().Be(
                "経済産業省産業技術環境局産業技術政策課：「経済産業省産業技術政策」、http://www.meti.go.jp/policy/economy/gijutsu_kakushin/pamphlet.pdf、访问日期：2025年8月6日。");
    }

    [Fact]
    public void Special_responsibilities_prefaces_and_doi_remain_expressible()
    {
        Render(Item("book", "水经注校证", "zh", Literals("author", "郦道元著，陈桥驿校证"),
                ("publisher-place", "北京"), ("publisher", "中华书局"), ("issued", Date(2007))), "1")
            .Should().Be("郦道元著，陈桥驿校证：《水经注校证》，北京：中华书局，2007年，第1页。");

        Render(Item("book", "当代文化哲学沉思", "zh", Literals("author", "李鹏程"),
                ("publisher-place", "北京"), ("publisher", "人民出版社"), ("issued", Date(1994)),
                ("note", "“序言”")), "1")
            .Should().Be("李鹏程：《当代文化哲学沉思》，北京：人民出版社，1994年，“序言”，第1页。");

        Render(Item("webpage", "Electronic Resource", "zh", ("DOI", "10.1000/example"),
                ("URL", "https://example.test/fulltext"), ("accessed", Date(2025, 1, 1))))
            .Should().Be("《Electronic Resource》，DOI：10.1000/example。");
    }

    [Fact]
    public void English_name_count_editor_and_edition_rules_match_the_examples()
    {
        Render(Item("book", "The Arts of Power: Three Halls of State in Italy, 1300-1600", "en",
                Names("author", ("Starn", "Randolph"), ("Partridge", "Loren")),
                ("publisher-place", "Berkeley"), ("publisher", "California University Press"),
                ("issued", Date(1992))), "19-28")
            .Should().StartWith("Randolph Starn and Loren Partridge,").And.EndWith("1992, pp. 19-28.");

        Render(Item("book", "Three Authors", "en",
                Names("author", ("One", "A"), ("Two", "B"), ("Three", "C"))))
            .Should().StartWith("A One, B Two and C Three,");

        Render(Item("book", "New Perspectives on Historical Writing", "en",
                Names("editor", ("Burke", "Peter")), ("publisher-place", "University Park"),
                ("publisher", "Pennsylvania State University Press"), ("issued", Date(2001))))
            .Should().Be(
                "Peter Burke, ed., New Perspectives on Historical Writing, University Park: Pennsylvania State University Press, 2001.");

        Render(Item("book", "The Bonds of Womanhood", "en", Names("author", ("Cott", "Nancy F.")),
                ("edition", "2"), ("publisher-place", "New Haven"), ("publisher", "Yale University Press"),
                ("issued", Date(1997))))
            .Should().Contain("The Bonds of Womanhood, 2nd ed., New Haven:");

        Render(Item("webpage", "Online Resource", "en", Names("author", ("Brooks", "Peter")),
                ("URL", "https://example.test"), ("accessed", Date(2025, 10, 4))))
            .Should().Be("Peter Brooks, “Online Resource,” https://example.test, accessed October 4, 2025.");
    }

    [Fact]
    public void Japanese_citations_without_authors_omit_colons_in_first_and_subsequent_citations()
    {
        Dictionary<string, object?> item = Item("book", "無署名著作", "ja",
            ("publisher-place", "東京"), ("publisher", "出版社"), ("issued", Date(2000)));
        object payload = new
        {
            style = BuiltInCslStyles.GetContent(BuiltInCslStyles.HistoricalResearchId),
            items = new[] { item },
            citations = new[]
            {
                new[] { new { id = "item", locator = "10", label = "page" } },
                new[] { new { id = "item", locator = "20", label = "page" } }
            },
            format = "plain-text"
        };

        FSharpResult<string, FSharpList<CiteprocError>> result =
            Citeproc.renderJson(JsonSerializer.Serialize(payload));

        result.IsError.Should().BeFalse();
        using JsonDocument document = JsonDocument.Parse(result.ResultValue);
        JsonElement citations = document.RootElement.GetProperty("citations");
        citations.GetArrayLength().Should().Be(2);
        citations[0].GetString().Should().Be("『無署名著作』、東京：出版社、2000年、第10頁。");
        citations[1].GetString().Should().Be("『無署名著作』、第20頁。");
    }

    [Fact]
    public void Japanese_multiple_contributors_use_hoka()
    {
        Render(Item("chapter", "東アジアの社会と儒教", "ja", Literals("author", "伊藤亜人"),
                Literals("editor", "溝口雄三", "木村健治", "藤本和貴夫"),
                ("container-title", "アジアから考える"), ("volume", "1"),
                ("publisher-place", "東京"), ("publisher", "東京大学出版会"), ("issued", Date(1993))))
            .Should().Be("伊藤亜人：「東アジアの社会と儒教」、溝口雄三ほか編：『アジアから考える』第1巻、東京：東京大学出版会、1993年。");
    }

    [Fact]
    public void Subsequent_citation_uses_short_form_when_citation_history_is_available()
    {
        Dictionary<string, object?> item = Item("book", "文坛忆旧", "zh", Literals("author", "赵景深"),
            ("publisher-place", "上海"), ("publisher", "北新书局"), ("issued", Date(1948)));
        object payload = new
        {
            style = BuiltInCslStyles.GetContent(BuiltInCslStyles.HistoricalResearchId),
            items = new[] { item },
            citations = new[]
            {
                new[] { new { id = "item", locator = "43", label = "page" } },
                new[] { new { id = "item", locator = "24", label = "page" } }
            },
            format = "plain-text"
        };

        FSharpResult<string, FSharpList<CiteprocError>> result =
            Citeproc.renderJson(JsonSerializer.Serialize(payload));

        result.IsError.Should().BeFalse();
        using JsonDocument document = JsonDocument.Parse(result.ResultValue);
        JsonElement citations = document.RootElement.GetProperty("citations");
        citations.GetArrayLength().Should().Be(2);
        citations[0].GetString().Should().Be("赵景深：《文坛忆旧》，上海：北新书局，1948年，第43页。");
        citations[1].GetString().Should().Be("赵景深：《文坛忆旧》，第24页。");
    }

    [Fact]
    public void Bibliography_preserves_all_three_language_branches()
    {
        Dictionary<string, object?> chinese = Item("book", "文坛忆旧", "zh", Literals("author", "赵景深"));
        Dictionary<string, object?> english = Item("book", "Troubling Confessions", "en",
            Names("author", ("Brooks", "Peter")));
        Dictionary<string, object?> japanese = Item("book", "現代日本の産業政策", "ja",
            Literals("author", "小野五郎"));
        chinese["id"] = "zh";
        english["id"] = "en";
        japanese["id"] = "ja";

        Result<FsharpCiteprocRenderResponse> result = new FsharpCiteprocProcessor().Render(
            new FsharpCiteprocRenderRequest(BuiltInCslStyles.HistoricalResearchId,
                BuiltInCslStyles.GetContent(BuiltInCslStyles.HistoricalResearchId), null,
                CslSettings.DefaultLanguageLocaleMappings, [chinese, english, japanese]));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.RenderedText.Should().Contain("赵景深：《文坛忆旧》")
            .And.Contain("Peter Brooks, Troubling Confessions")
            .And.Contain("小野五郎：『現代日本の産業政策』");
        result.Value.RenderedHtml.Should().Contain("<i>Troubling Confessions</i>");
    }

    private static string Render(Dictionary<string, object?> item, string? locator = null)
    {
        FsharpCiteprocProcessor processor = new();
        Result<FsharpCiteprocClusterResponse> result = processor.RenderCluster(
            new FsharpCiteprocClusterRequest(BuiltInCslStyles.HistoricalResearchId,
                BuiltInCslStyles.GetContent(BuiltInCslStyles.HistoricalResearchId), null,
                CslSettings.DefaultLanguageLocaleMappings, [item],
                [new FsharpCiteprocCitationItem("item", locator, locator is null ? null : "page")]));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value.CitationText;
    }

    private static Dictionary<string, object?> Item(string type, string title, string language,
        params (string Key, object? Value)[] fields)
    {
        Dictionary<string, object?> item = new(StringComparer.Ordinal)
        {
            ["id"] = "item", ["type"] = type, ["title"] = title, ["language"] = language
        };
        foreach ((string key, object? value) in fields)
        {
            item[key] = value;
        }

        return item;
    }

    private static Dictionary<string, object?> Date(int year, int? month = null, int? day = null)
    {
        return new Dictionary<string, object?>
        {
            ["date-parts"] = new[] { new[] { (object?)year, month, day }.Where(value => value is not null).ToArray() }
        };
    }

    private static (string Key, object? Value) Literals(string role, params string[] names)
    {
        return (role, names.Select(name => new Dictionary<string, object?> { ["literal"] = name }).ToArray());
    }

    private static (string Key, object? Value) Names(string role, params (string Family, string Given)[] names)
    {
        return (role, names.Select(name => new Dictionary<string, object?>
        {
            ["family"] = name.Family, ["given"] = name.Given
        }).ToArray());
    }
}
