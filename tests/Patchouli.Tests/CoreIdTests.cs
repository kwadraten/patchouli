using FluentAssertions;
using Patchouli.Core.Ids;

namespace Patchouli.Tests;

public sealed class CoreIdTests
{
    [Fact]
    public void Generated_ids_are_not_equal()
    {
        LibraryId.New().Should().NotBe(LibraryId.New());
        ItemId.New().Should().NotBe(ItemId.New());
        FileAssetId.New().Should().NotBe(FileAssetId.New());
        DocumentInstanceId.New().Should().NotBe(DocumentInstanceId.New());
        PageId.New().Should().NotBe(PageId.New());
        LayoutRevisionId.New().Should().NotBe(LayoutRevisionId.New());
        LayoutNodeId.New().Should().NotBe(LayoutNodeId.New());
        SearchUnitId.New().Should().NotBe(SearchUnitId.New());
        EvidenceRefId.New().Should().NotBe(EvidenceRefId.New());
    }

    [Fact]
    public void Ids_can_round_trip_through_strings()
    {
        var libraryId = LibraryId.New();
        var itemId = ItemId.New();
        var fileAssetId = FileAssetId.New();
        var documentInstanceId = DocumentInstanceId.New();
        var pageId = PageId.New();
        var layoutRevisionId = LayoutRevisionId.New();
        var layoutNodeId = LayoutNodeId.New();
        var searchUnitId = SearchUnitId.New();
        var evidenceRefId = EvidenceRefId.New();

        LibraryId.Parse(libraryId.ToString()).Should().Be(libraryId);
        ItemId.Parse(itemId.ToString()).Should().Be(itemId);
        FileAssetId.Parse(fileAssetId.ToString()).Should().Be(fileAssetId);
        DocumentInstanceId.Parse(documentInstanceId.ToString()).Should().Be(documentInstanceId);
        PageId.Parse(pageId.ToString()).Should().Be(pageId);
        LayoutRevisionId.Parse(layoutRevisionId.ToString()).Should().Be(layoutRevisionId);
        LayoutNodeId.Parse(layoutNodeId.ToString()).Should().Be(layoutNodeId);
        SearchUnitId.Parse(searchUnitId.ToString()).Should().Be(searchUnitId);
        EvidenceRefId.Parse(evidenceRefId.ToString()).Should().Be(evidenceRefId);
    }
}
