using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerULayoutNodeMapperTests
{
    [Fact]
    public void MapDocument_creates_stable_page_block_ordering()
    {
        var document = new MinerUContentListDocument([
            new MinerUContentListPage(1, 595, 842, [
                new MinerUContentBlock("text", [0, 0, 100, 50], "First", null, null)
            ]),
            new MinerUContentListPage(2, 595, 842, [
                new MinerUContentBlock("text", [0, 0, 100, 50], "Second", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        var pages = new[]
        {
            new Patchouli.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new Patchouli.Core.Layout.Page(PageId.New(), docId, 1, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var mapper = new MinerULayoutNodeMapper();
        var layout = mapper.MapDocument(document, pages);
        var blocks = layout.Pages.SelectMany(page => page.Blocks).ToArray();

        blocks.Should().HaveCount(2);
        blocks[0].ReadingOrder.Should().BeLessThan(blocks[1].ReadingOrder);
        blocks[0].Text.Should().Be("First");
        blocks[1].Text.Should().Be("Second");
    }

    [Fact]
    public void MapDocument_skips_discarded_blocks()
    {
        var document = new MinerUContentListDocument([
            new MinerUContentListPage(1, 595, 842, [
                new MinerUContentBlock("discarded", null, null, null, null),
                new MinerUContentBlock("text", [0, 0, 100, 50], "Visible", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        Patchouli.Core.Layout.Page[] pages =
        [
            new Patchouli.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ];

        var mapper = new MinerULayoutNodeMapper();
        var layout = mapper.MapDocument(document, pages);

        layout.Pages.Should().ContainSingle();
        layout.Pages[0].Blocks.Should().ContainSingle();
        layout.Pages[0].Blocks[0].Text.Should().Be("Visible");
    }

    [Fact]
    public void MapDocument_normalizes_bbox()
    {
        var document = new MinerUContentListDocument([
            new MinerUContentListPage(1, 595, 842, [
                new MinerUContentBlock("text", [0, 0, 297.5, 421], "Half page", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        Patchouli.Core.Layout.Page[] pages =
        [
            new Patchouli.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ];

        var mapper = new MinerULayoutNodeMapper();
        var layout = mapper.MapDocument(document, pages);

        var bbox = layout.Pages[0].Blocks[0].BBox;
        bbox.Should().NotBeNull();
        bbox!.Value.X.Should().Be(0);
        bbox.Value.Y.Should().Be(0);
        bbox.Value.Width.Should().BeApproximately(0.5, 0.01);
        bbox.Value.Height.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void MapDocument_skips_images_without_text()
    {
        var document = new MinerUContentListDocument([
            new MinerUContentListPage(1, 1000, 1000, [
                new MinerUContentBlock("image", [0, 0, 1000, 1000], null, null, null),
                new MinerUContentBlock("text", [0, 0, 1000, 100], "Visible", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        Patchouli.Core.Layout.Page[] pages =
        [
            new Patchouli.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ];

        var mapper = new MinerULayoutNodeMapper();
        var layout = mapper.MapDocument(document, pages);

        layout.Pages[0].Blocks.Should().ContainSingle();
        layout.Pages[0].Blocks[0].Text.Should().Be("Visible");
    }

    [Fact]
    public void MapDocument_converts_tables_into_row_and_cell_blocks()
    {
        var document = new MinerUContentListDocument([
            new MinerUContentListPage(1, 600, 800, [
                new MinerUContentBlock("table", [50, 100, 550, 260], null, null, null, [
                    new MinerUTableCell(0, 0, 1, 1, true, "Name", [50, 100, 300, 140]),
                    new MinerUTableCell(0, 1, 1, 1, true, "Value", [300, 100, 550, 140]),
                    new MinerUTableCell(1, 0, 1, 1, false, "Pages", [50, 140, 300, 180]),
                    new MinerUTableCell(1, 1, 1, 1, false, "12", [300, 140, 550, 180])
                ])
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        Patchouli.Core.Layout.Page[] pages =
        [
            new Patchouli.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ];

        var mapper = new MinerULayoutNodeMapper();
        var layout = mapper.MapDocument(document, pages);
        var table = layout.Pages[0].Blocks.Single();

        table.NodeType.Should().Be(LayoutNodeType.Table);
        table.TextPolicy.Should().Be(TextPolicy.AggregateChildren);
        table.Children.Should().HaveCount(2);
        table.Children!.SelectMany(row => row.Children ?? []).Select(cell => cell.Text).Should().Equal("Name", "Value", "Pages", "12");
        table.Children!.SelectMany(row => row.Children ?? []).Where(cell => cell.TableCell!.RowIndex == 0).Should().OnlyContain(cell => cell.TableCell!.IsHeader);
    }
}
