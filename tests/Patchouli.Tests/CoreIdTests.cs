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
        DocumentTreeRevisionId.New().Should().NotBe(DocumentTreeRevisionId.New());
        DocumentBoxId.New().Should().NotBe(DocumentBoxId.New());
        PageEditSessionId.New().Should().NotBe(PageEditSessionId.New());
        SearchUnitId.New().Should().NotBe(SearchUnitId.New());
        DocumentCommitId.New().Should().NotBe(DocumentCommitId.New());
    }

    [Fact]
    public void Ids_can_round_trip_through_strings()
    {
        LibraryId libraryId = LibraryId.New();
        ItemId itemId = ItemId.New();
        FileAssetId fileAssetId = FileAssetId.New();
        DocumentInstanceId documentInstanceId = DocumentInstanceId.New();
        PageId pageId = PageId.New();
        DocumentTreeRevisionId treeRevisionId = DocumentTreeRevisionId.New();
        DocumentBoxId boxId = DocumentBoxId.New();
        PageEditSessionId editSessionId = PageEditSessionId.New();
        SearchUnitId searchUnitId = SearchUnitId.New();
        DocumentCommitId documentCommitId = DocumentCommitId.New();

        LibraryId.Parse(libraryId.ToString()).Should().Be(libraryId);
        ItemId.Parse(itemId.ToString()).Should().Be(itemId);
        FileAssetId.Parse(fileAssetId.ToString()).Should().Be(fileAssetId);
        DocumentInstanceId.Parse(documentInstanceId.ToString()).Should().Be(documentInstanceId);
        PageId.Parse(pageId.ToString()).Should().Be(pageId);
        DocumentTreeRevisionId.Parse(treeRevisionId.ToString()).Should().Be(treeRevisionId);
        DocumentBoxId.Parse(boxId.ToString()).Should().Be(boxId);
        PageEditSessionId.Parse(editSessionId.ToString()).Should().Be(editSessionId);
        SearchUnitId.Parse(searchUnitId.ToString()).Should().Be(searchUnitId);
        DocumentCommitId.Parse(documentCommitId.ToString()).Should().Be(documentCommitId);
    }
}
