using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Reading;

namespace Patchouli.Tests;

public sealed class ReadingSceneBuilderTests
{
    [Fact]
    public void Returns_empty_scene_for_an_empty_model()
    {
        ReadingScene scene = ReadingSceneBuilder.Build(new MarkdownDocumentModel([]), [], [], "page");

        scene.Blocks.Should().BeEmpty();
    }

    [Fact]
    public void Keeps_block_order_and_kind_when_no_source_map_entry_covers_the_block()
    {
        MarkdownDocumentModel model = new([
            Block("paragraph", "第一段"),
            Block("heading", "标题", 2),
            Block("code", "let x = 1;")
        ]);
        MarkdownSourceMapEntry unrelated = SourceMap(DocumentBoxId.New(), 7, 2);

        ReadingScene scene = ReadingSceneBuilder.Build(model, [unrelated], [], "page");

        scene.Blocks.Select(block => block.Kind).Should().Equal("paragraph", "heading", "code");
        scene.Blocks.Select(block => block.Text).Should().Equal("第一段", "标题", "let x = 1;");
        scene.Blocks.Select(block => block.Level).Should().Equal(0, 2, 0);
        scene.Blocks.Should().OnlyContain(block => block.BoxId == null);
    }

    [Fact]
    public void Resolves_box_ids_from_the_source_map_range_that_contains_the_block()
    {
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        MarkdownDocumentModel model = new([
            Block("paragraph", "零"),
            Block("paragraph", "一"),
            Block("paragraph", "二"),
            Block("paragraph", "三"),
            Block("paragraph", "四")
        ]);
        MarkdownSourceMapEntry[] sourceMap =
        [
            SourceMap(first, 1, 2),
            SourceMap(second, 4, 1)
        ];

        ReadingScene scene = ReadingSceneBuilder.Build(model, sourceMap, [], "page");

        scene.Blocks.Select(block => block.BoxId)
            .Should().Equal(null, first, first, null, second);
    }

    [Fact]
    public void Ignores_source_map_entries_without_a_preview_range()
    {
        DocumentBoxId empty = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "文本")]);

        ReadingScene scene = ReadingSceneBuilder.Build(model, [SourceMap(empty, 0, 0)], [], "page");

        scene.Blocks.Should().ContainSingle().Which.BoxId.Should().BeNull();
    }

    [Fact]
    public void Falls_back_to_the_block_kind_when_the_box_is_missing_from_the_page()
    {
        DocumentBoxId missing = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "文本")]);

        ReadingScene scene = ReadingSceneBuilder.Build(model, [SourceMap(missing, 0, 1)], [], "page");

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.BoxId.Should().Be(missing);
        block.Kind.Should().Be("paragraph");
    }

    [Theory]
    [InlineData(DocumentBoxType.Text, "paragraph")]
    [InlineData(DocumentBoxType.RefText, "paragraph")]
    [InlineData(DocumentBoxType.Title, "heading")]
    [InlineData(DocumentBoxType.Image, "media")]
    [InlineData(DocumentBoxType.Chart, "media")]
    [InlineData(DocumentBoxType.List, "list")]
    [InlineData(DocumentBoxType.Code, "code")]
    [InlineData(DocumentBoxType.Algorithm, "code")]
    [InlineData(DocumentBoxType.Equation, "equation")]
    [InlineData(DocumentBoxType.ImageCaption, "paragraph")]
    [InlineData(DocumentBoxType.TableFootnote, "paragraph")]
    [InlineData(DocumentBoxType.Header, "paragraph")]
    [InlineData(DocumentBoxType.Footer, "paragraph")]
    public void Maps_box_type_to_reading_kind(string boxType, string expectedKind)
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "内容")]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, boxType)], "page");

        scene.Blocks.Should().ContainSingle().Which.Kind.Should().Be(expectedKind);
    }

    [Fact]
    public void Prefers_the_box_heading_level_over_the_block_level()
    {
        DocumentBoxId title = DocumentBoxId.New();
        DocumentBoxId plain = DocumentBoxId.New();
        MarkdownDocumentModel model = new([
            Block("heading", "标题", 2),
            Block("paragraph", "正文", 5)
        ]);
        MarkdownSourceMapEntry[] sourceMap = [SourceMap(title, 0, 1), SourceMap(plain, 1, 1)];

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, sourceMap, [Box(title, DocumentBoxType.Title, 4), Box(plain, DocumentBoxType.Text)],
            "page");

        scene.Blocks.Select(block => block.Level).Should().Equal(4, 5);
    }

    [Fact]
    public void Parses_a_gfm_pipe_table_with_a_header()
    {
        string text = "| 名称 | 数量 |\n| --- | ---: |\n| 苹果 | 3 |\n| 梨 | 5 |";
        ReadingScene scene = BuildTable(text);

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Kind.Should().Be("table");
        block.Text.Should().Be(text);
        block.Table.Should().NotBeNull();
        ReadingTable table = block.Table!;
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Should().Equal("名称", "数量");
        table.Rows[1].Should().Equal("苹果", "3");
        table.Rows[2].Should().Equal("梨", "5");
    }

    [Fact]
    public void Parses_a_gfm_pipe_table_without_a_header()
    {
        ReadingScene scene = BuildTable("| 甲 | 乙 |\n| 1 | 2 |");

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Table.Should().NotBeNull();
        ReadingTable table = block.Table!;
        table.HasHeader.Should().BeFalse();
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("甲", "乙");
        table.Rows[1].Should().Equal("1", "2");
    }

    [Fact]
    public void Accepts_table_rows_without_outer_pipes()
    {
        ReadingScene scene = BuildTable("甲 | 乙\n--- | ---\n1 | 2");

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Table.Should().NotBeNull();
        ReadingTable table = block.Table!;
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("甲", "乙");
        table.Rows[1].Should().Equal("1", "2");
    }

    [Fact]
    public void Keeps_escaped_pipes_inside_a_cell()
    {
        ReadingScene scene = BuildTable("| a \\| b | c |\n| --- | --- |\n| 1 | 2 |");

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Table.Should().NotBeNull();
        ReadingTable table = block.Table!;
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("a | b", "c");
        table.Rows[1].Should().Equal("1", "2");
    }

    [Theory]
    [InlineData("| --- | --- |")]
    [InlineData("   ")]
    public void Falls_back_to_raw_text_when_the_table_yields_no_rows(string text)
    {
        ReadingScene scene = BuildTable(text);

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Kind.Should().Be("table");
        block.Table.Should().BeNull();
        block.Text.Should().Be(text);
    }

    [Fact]
    public void Falls_back_to_raw_text_for_the_table_placeholder()
    {
        ReadingScene scene = BuildTable("[Table]");

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Kind.Should().Be("table");
        block.Table.Should().BeNull();
        block.Text.Should().Be("[Table]");
        block.Inlines.Should().BeNull();
    }

    [Fact]
    public void Parses_the_table_from_the_markdown_source_not_the_flattened_plain_text()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        const string markdown = "| 名称 | 数量 |\n| --- | ---: |\n| 苹果 | 3 |\n| 梨 | 5 |";
        // Markdig's plain text for a pipe table drops the pipes and joins the cells with spaces,
        // so parsing MarkdownBlock.Text alone would produce a one-column grid.
        MarkdownBlock block = new("table", "名称 数量 苹果 3 梨 5", 0, markdown.Length);
        MarkdownDocumentModel model = new([block]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Table)], "page",
            markdownSource: markdown);

        ReadingBlock parsed = scene.Blocks.Should().ContainSingle().Subject;
        ReadingTable table = parsed.Table!;
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Should().Equal("名称", "数量");
        table.Rows[1].Should().Equal("苹果", "3");
        table.Rows[2].Should().Equal("梨", "5");
    }

    [Fact]
    public void Renders_a_complex_table_placeholder_from_its_stored_html()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        const string html =
            "<table><tr><th>名称</th><th>数量</th></tr><tr><td>苹果</td><td>3</td></tr></table>";
        MarkdownBlock block = new("table", "[Table]", 0, 7);
        MarkdownDocumentModel model = new([block]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)],
            [Box(boxId, DocumentBoxType.Table, payload: new TableBoxPayload("[Table]", html))], "page");

        ReadingBlock parsed = scene.Blocks.Should().ContainSingle().Subject;
        ReadingTable table = parsed.Table!;
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("名称", "数量");
        table.Rows[1].Should().Equal("苹果", "3");
    }

    [Fact]
    public void Html_table_colspan_keeps_the_row_aligned_with_the_widest_columns()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        const string html = "<table><tr><td colspan=\"2\">合计</td><td>3</td></tr></table>";
        MarkdownBlock block = new("table", "[Table]", 0, 7);
        MarkdownDocumentModel model = new([block]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)],
            [Box(boxId, DocumentBoxType.Table, payload: new TableBoxPayload("[Table]", html))], "page");

        ReadingBlock parsed = scene.Blocks.Should().ContainSingle().Subject;
        IReadOnlyList<string> row = parsed.Table!.Rows.Should().ContainSingle().Subject;
        row.Should().Equal("合计", "", "3");
    }

    [Fact]
    public void Renders_a_complex_table_from_the_inline_html_of_its_markdown_slice()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        const string markdown =
            "<table><tr><th>名称</th><th>数量</th></tr><tr><td>苹果</td><td>3</td></tr></table>";
        // Whole-book reading compiles complex tables with their HTML kept inline, so the block's
        // slice is the table element itself instead of the [Table] placeholder the preview uses.
        MarkdownBlock block = new("paragraph", markdown, 0, markdown.Length);
        MarkdownDocumentModel model = new([block]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Table)], "page",
            markdownSource: markdown);

        ReadingBlock parsed = scene.Blocks.Should().ContainSingle().Subject;
        parsed.Kind.Should().Be("table");
        ReadingTable table = parsed.Table!;
        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("名称", "数量");
        table.Rows[1].Should().Equal("苹果", "3");
    }

    [Fact]
    public void A_complex_table_claims_its_payload_grid_once_across_blocks()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        const string html = "<table><tr><td>苹果</td><td>3</td></tr></table>";
        // The grid rides on the box payload, so a complex table whose placeholder markdown arrives
        // as several blocks must still render one grid instead of repeating it per fragment.
        MarkdownDocumentModel model = new(
        [
            new MarkdownBlock("table", "[Table]", 0, 7),
            new MarkdownBlock("table", "[Table]", 0, 7)
        ]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 2)],
            [Box(boxId, DocumentBoxType.Table, payload: new TableBoxPayload("[Table]", html))], "page");

        scene.Blocks.Should().HaveCount(2);
        scene.Blocks[0].Table.Should().NotBeNull();
        scene.Blocks[1].Table.Should().BeNull();
    }

    [Fact]
    public void Renders_tables_from_the_grid_not_from_inlines()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        MarkdownBlock block = new(
            "table", "| a |\n| --- |\n| 1 |", 0, 20, 0, [new MarkdownInlineModel("text", "a")]);
        MarkdownDocumentModel model = new([block]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Table)], "page");

        ReadingBlock reading = scene.Blocks.Should().ContainSingle().Subject;
        reading.Table.Should().NotBeNull();
        reading.Inlines.Should().BeNull();
    }

    [Fact]
    public void Labels_image_and_chart_media_blocks_in_chinese()
    {
        DocumentBoxId image = DocumentBoxId.New();
        DocumentBoxId chart = DocumentBoxId.New();
        MarkdownDocumentModel model = new([
            Block("paragraph", "一只猫"),
            Block("paragraph", "销售趋势")
        ]);
        MarkdownSourceMapEntry[] sourceMap = [SourceMap(image, 0, 1), SourceMap(chart, 1, 1)];

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, sourceMap, [Box(image, DocumentBoxType.Image), Box(chart, DocumentBoxType.Chart)],
            "page");

        scene.Blocks.Select(block => block.Kind).Should().Equal("media", "media");
        scene.Blocks.Select(block => block.MediaLabel).Should().Equal("图像", "图表");
        scene.Blocks.Select(block => block.Text).Should().Equal("一只猫", "销售趋势");
    }

    [Fact]
    public void Leaves_media_label_empty_for_non_media_blocks()
    {
        DocumentBoxId paragraph = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "正文")]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(paragraph, 0, 1)], [Box(paragraph, DocumentBoxType.Text)], "page");

        scene.Blocks.Should().ContainSingle().Which.MediaLabel.Should().BeNull();
    }

    [Fact]
    public void Carries_the_normalized_bbox_region_even_without_a_media_asset_id()
    {
        DocumentBoxId image = DocumentBoxId.New();
        DocumentBoxId chart = DocumentBoxId.New();
        MarkdownDocumentModel model = new([
            Block("paragraph", "一只猫"),
            Block("paragraph", "销售趋势")
        ]);
        MarkdownSourceMapEntry[] sourceMap = [SourceMap(image, 0, 1), SourceMap(chart, 1, 1)];

        ReadingScene scene = ReadingSceneBuilder.Build(
            model,
            sourceMap,
            [
                // Both boxes have a MediaBoxPayload with no AssetId: the region must still flow
                // through so the renderer can crop from the page bitmap.
                Box(image, DocumentBoxType.Image, payload: new MediaBoxPayload(null, "猫"),
                    bbox: new NormalizedBBox(.1, .2, .3, .4)),
                Box(chart, DocumentBoxType.Chart, payload: new MediaBoxPayload(null, "趋势"),
                    bbox: new NormalizedBBox(.0, .0, 1.0, 1.0))
            ],
            "page");

        ReadingImageRegion? firstRegion = scene.Blocks[0].Image;
        firstRegion.Should().NotBeNull();
        firstRegion!.ImageKey.Should().Be("page");
        firstRegion.Region.Should().Be(new NormalizedBBox(.1, .2, .3, .4));
        scene.Blocks[1].Image.Should().NotBeNull();
        scene.Blocks[1].Image!.Region.Should().Be(new NormalizedBBox(0, 0, 1, 1));
    }

    [Fact]
    public void Keys_media_regions_by_the_supplied_page_image_key()
    {
        DocumentBoxId image = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "一只猫")]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(image, 0, 1)], [Box(image, DocumentBoxType.Image)], "page-12");

        scene.Blocks.Should().ContainSingle().Which.Image!.ImageKey.Should().Be("page-12");
    }

    [Fact]
    public void Ignores_a_media_payload_on_a_non_media_block()
    {
        DocumentBoxId paragraph = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "正文")]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(paragraph, 0, 1)],
            [Box(paragraph, DocumentBoxType.Text, payload: new MediaBoxPayload("asset-image", null))],
            "page");

        scene.Blocks.Should().ContainSingle().Which.Image.Should().BeNull();
    }

    [Fact]
    public void Passes_the_box_code_language_through()
    {
        DocumentBoxId code = DocumentBoxId.New();
        DocumentBoxId paragraph = DocumentBoxId.New();
        MarkdownDocumentModel model = new([
            Block("code", "let x = 1;"),
            Block("paragraph", "正文")
        ]);
        MarkdownSourceMapEntry[] sourceMap = [SourceMap(code, 0, 1), SourceMap(paragraph, 1, 1)];

        ReadingScene scene = ReadingSceneBuilder.Build(
            model,
            sourceMap,
            [Box(code, DocumentBoxType.Code, codeLanguage: "fsharp"), Box(paragraph, DocumentBoxType.Text)],
            "page");

        scene.Blocks.Select(block => block.CodeLanguage).Should().Equal("fsharp", null);
    }

    [Fact]
    public void Passes_the_block_inlines_through()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        IReadOnlyList<MarkdownInlineModel> inlines =
        [
            new("text", "加粗"),
            new("strong", string.Empty, [new MarkdownInlineModel("text", "强调")])
        ];
        MarkdownDocumentModel model = new([Block("paragraph", "加强调", inlines: inlines)]);

        ReadingScene scene = ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Text)], "page");

        scene.Blocks.Should().ContainSingle().Which.Inlines.Should().BeSameAs(inlines);
    }

    private static ReadingScene BuildTable(string text)
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("table", text)]);
        return ReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Table)], "page");
    }

    private static MarkdownBlock Block(
        string kind,
        string text,
        int level = 0,
        IReadOnlyList<MarkdownInlineModel>? inlines = null)
    {
        return new MarkdownBlock(kind, text, 0, text.Length, level, inlines);
    }

    private static MarkdownSourceMapEntry SourceMap(
        DocumentBoxId boxId,
        int previewNodeStart,
        int previewNodeCount)
    {
        return new MarkdownSourceMapEntry(boxId, 0, 1, previewNodeStart, previewNodeCount);
    }

    private static DocumentBox Box(
        DocumentBoxId boxId,
        string boxType,
        int? headingLevel = null,
        string? codeLanguage = null,
        DocumentBoxPayload? payload = null,
        NormalizedBBox? bbox = null)
    {
        return new DocumentBox(
            DocumentTreeRevisionId.New(),
            boxId,
            DocumentInstanceId.New(),
            PageId.New(),
            null,
            null,
            boxType,
            null,
            null,
            bbox ?? new NormalizedBBox(.1, .1, .8, .1),
            payload,
            headingLevel,
            codeLanguage,
            null,
            false);
    }
}
