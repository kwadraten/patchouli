using FluentAssertions;
using LiteratureApp.Core.Ids;
using LiteratureApp.Infrastructure.Ocr.MinerU;

namespace LiteratureApp.Tests;

public sealed class MinerULayoutNodeMapperTests
{
    [Fact]
    public void MapDocument_creates_stable_page_node_ordering()
    {
        var doc = new MinerUContentListDocument([
            new MinerUContentListPage(1, 595, 842, [
                new MinerUContentBlock("text", [0, 0, 100, 50], "First", null, null)
            ]),
            new MinerUContentListPage(2, 595, 842, [
                new MinerUContentBlock("text", [0, 0, 100, 50], "Second", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        var revId = new LayoutRevisionId(Guid.NewGuid());
        var pages = new[]
        {
            new LiteratureApp.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new LiteratureApp.Core.Layout.Page(PageId.New(), docId, 1, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var mapper = new MinerULayoutNodeMapper();
        var nodes = mapper.MapDocument(doc, docId, revId, pages);

        nodes.Should().HaveCount(2);
        nodes[0].ReadingOrder.Should().BeLessThan(nodes[1].ReadingOrder);
        nodes[0].OwnText.Should().Be("First");
        nodes[1].OwnText.Should().Be("Second");
    }

    [Fact]
    public void MapDocument_skips_discarded_blocks()
    {
        var doc = new MinerUContentListDocument([
            new MinerUContentListPage(1, 595, 842, [
                new MinerUContentBlock("discarded", null, null, null, null),
                new MinerUContentBlock("text", [0, 0, 100, 50], "Visible", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        var revId = new LayoutRevisionId(Guid.NewGuid());
        var pages = new[]
        {
            new LiteratureApp.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var mapper = new MinerULayoutNodeMapper();
        var nodes = mapper.MapDocument(doc, docId, revId, pages);

        nodes.Should().HaveCount(1);
        nodes[0].OwnText.Should().Be("Visible");
    }

    [Fact]
    public void MapDocument_normalizes_bbox()
    {
        var doc = new MinerUContentListDocument([
            new MinerUContentListPage(1, 595, 842, [
                new MinerUContentBlock("text", [0, 0, 297.5, 421], "Half page", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        var revId = new LayoutRevisionId(Guid.NewGuid());
        var pages = new[]
        {
            new LiteratureApp.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var mapper = new MinerULayoutNodeMapper();
        var nodes = mapper.MapDocument(doc, docId, revId, pages);

        nodes.Should().HaveCount(1);
        var bbox = nodes[0].BBox;
        bbox.Should().NotBeNull();
        bbox!.Value.X.Should().Be(0);
        bbox.Value.Y.Should().Be(0);
        bbox.Value.Width.Should().BeApproximately(0.5, 0.01);
        bbox.Value.Height.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void MapDocument_skips_images_without_text()
    {
        var doc = new MinerUContentListDocument([
            new MinerUContentListPage(1, 1000, 1000, [
                new MinerUContentBlock("image", [0, 0, 1000, 1000], null, null, null),
                new MinerUContentBlock("text", [0, 0, 1000, 100], "Visible", null, null)
            ])
        ]);
        var docId = new DocumentInstanceId(Guid.NewGuid());
        var revId = new LayoutRevisionId(Guid.NewGuid());
        var pages = new[]
        {
            new LiteratureApp.Core.Layout.Page(PageId.New(), docId, 0, null, null, null, 0, "normalized", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var mapper = new MinerULayoutNodeMapper();
        var nodes = mapper.MapDocument(doc, docId, revId, pages);

        nodes.Should().ContainSingle();
        nodes[0].OwnText.Should().Be("Visible");
    }
}
