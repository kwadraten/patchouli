using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.UI.Controls;

namespace Patchouli.Tests;

public sealed class DocumentReadingSceneTests
{
    [Fact]
    public void Returns_empty_scene_for_an_empty_model()
    {
        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(new MarkdownDocumentModel([]), [], []);

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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(model, [unrelated], []);

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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(model, sourceMap, []);

        scene.Blocks.Select(block => block.BoxId)
            .Should().Equal(null, first, first, null, second);
    }

    [Fact]
    public void Ignores_source_map_entries_without_a_preview_range()
    {
        DocumentBoxId empty = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "文本")]);

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(empty, 0, 0)], []);

        scene.Blocks.Should().ContainSingle().Which.BoxId.Should().BeNull();
    }

    [Fact]
    public void Falls_back_to_the_block_kind_when_the_box_is_missing_from_the_page()
    {
        DocumentBoxId missing = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "文本")]);

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(missing, 0, 1)], []);

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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, boxType)]);

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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, sourceMap, [Box(title, DocumentBoxType.Title, 4), Box(plain, DocumentBoxType.Text)]);

        scene.Blocks.Select(block => block.Level).Should().Equal(4, 5);
    }

    [Fact]
    public void Parses_a_gfm_pipe_table_with_a_header()
    {
        string text = "| 名称 | 数量 |\n| --- | ---: |\n| 苹果 | 3 |\n| 梨 | 5 |";
        DocumentReadingScene scene = BuildTable(text);

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
        DocumentReadingScene scene = BuildTable("| 甲 | 乙 |\n| 1 | 2 |");

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
        DocumentReadingScene scene = BuildTable("甲 | 乙\n--- | ---\n1 | 2");

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
        DocumentReadingScene scene = BuildTable("| a \\| b | c |\n| --- | --- |\n| 1 | 2 |");

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
        DocumentReadingScene scene = BuildTable(text);

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Kind.Should().Be("table");
        block.Table.Should().BeNull();
        block.Text.Should().Be(text);
    }

    [Fact]
    public void Falls_back_to_raw_text_for_the_table_placeholder()
    {
        DocumentReadingScene scene = BuildTable("[Table]");

        ReadingBlock block = scene.Blocks.Should().ContainSingle().Subject;
        block.Kind.Should().Be("table");
        block.Table.Should().BeNull();
        block.Text.Should().Be("[Table]");
        block.Inlines.Should().BeNull();
    }

    [Fact]
    public void Renders_tables_from_the_grid_not_from_inlines()
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        MarkdownBlock block = new(
            "table", "| a |\n| --- |\n| 1 |", 0, 20, 0, [new MarkdownInlineModel("text", "a")]);
        MarkdownDocumentModel model = new([block]);

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Table)]);

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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, sourceMap, [Box(image, DocumentBoxType.Image), Box(chart, DocumentBoxType.Chart)]);

        scene.Blocks.Select(block => block.Kind).Should().Equal("media", "media");
        scene.Blocks.Select(block => block.MediaLabel).Should().Equal("图像", "图表");
        scene.Blocks.Select(block => block.Text).Should().Equal("一只猫", "销售趋势");
    }

    [Fact]
    public void Leaves_media_label_empty_for_non_media_blocks()
    {
        DocumentBoxId paragraph = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "正文")]);

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(paragraph, 0, 1)], [Box(paragraph, DocumentBoxType.Text)]);

        scene.Blocks.Should().ContainSingle().Which.MediaLabel.Should().BeNull();
    }

    [Fact]
    public void Fills_the_media_asset_id_from_the_box_payload()
    {
        DocumentBoxId image = DocumentBoxId.New();
        DocumentBoxId chart = DocumentBoxId.New();
        MarkdownDocumentModel model = new([
            Block("paragraph", "一只猫"),
            Block("paragraph", "销售趋势")
        ]);
        MarkdownSourceMapEntry[] sourceMap = [SourceMap(image, 0, 1), SourceMap(chart, 1, 1)];

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model,
            sourceMap,
            [
                Box(image, DocumentBoxType.Image, payload: new MediaBoxPayload("asset-image", "猫")),
                Box(chart, DocumentBoxType.Chart, payload: new MediaBoxPayload("asset-chart", "趋势"))
            ]);

        scene.Blocks.Select(block => block.MediaAssetId).Should().Equal("asset-image", "asset-chart");
    }

    [Fact]
    public void Leaves_media_asset_id_empty_when_the_payload_has_no_asset()
    {
        DocumentBoxId image = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "一只猫")]);

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(image, 0, 1)],
            [Box(image, DocumentBoxType.Image, payload: new MediaBoxPayload(null, "猫"))]);

        scene.Blocks.Should().ContainSingle().Which.MediaAssetId.Should().BeNull();
    }

    [Fact]
    public void Ignores_a_media_payload_on_a_non_media_block()
    {
        DocumentBoxId paragraph = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("paragraph", "正文")]);

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(paragraph, 0, 1)],
            [Box(paragraph, DocumentBoxType.Text, payload: new MediaBoxPayload("asset-image", null))]);

        scene.Blocks.Should().ContainSingle().Which.MediaAssetId.Should().BeNull();
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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model,
            sourceMap,
            [Box(code, DocumentBoxType.Code, codeLanguage: "fsharp"), Box(paragraph, DocumentBoxType.Text)]);

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

        DocumentReadingScene scene = DocumentReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Text)]);

        scene.Blocks.Should().ContainSingle().Which.Inlines.Should().BeSameAs(inlines);
    }

    private static DocumentReadingScene BuildTable(string text)
    {
        DocumentBoxId boxId = DocumentBoxId.New();
        MarkdownDocumentModel model = new([Block("table", text)]);
        return DocumentReadingSceneBuilder.Build(
            model, [SourceMap(boxId, 0, 1)], [Box(boxId, DocumentBoxType.Table)]);
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
        DocumentBoxPayload? payload = null)
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
            new NormalizedBBox(.1, .1, .8, .1),
            payload,
            headingLevel,
            codeLanguage,
            null,
            false);
    }
}
