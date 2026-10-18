using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class ChapterSidebarIndependentTests
{
    [Fact]
    public void Excluded_root_does_not_attach_its_retained_children_to_the_previous_root()
    {
        using ChapterSidebarViewModel sidebar = Sidebar();
        ChapterScanRowViewModel[] rows =
        [
            Row(sidebar, "a", 0, true), Row(sidebar, "b", 1, true),
            Row(sidebar, "excluded", 0, false), Row(sidebar, "e", 1, true)
        ];

        IReadOnlyList<ChapterNode> selected = ChapterSidebarViewModel.BuildSelectedPreview(rows);

        selected.Select(node => node.ChapterId).Should().Equal("a", "e");
        selected[0].Children.Should().ContainSingle().Which.ChapterId.Should().Be("b");
    }

    [Fact]
    public void Excluded_sibling_parent_promotes_children_to_their_retained_grandparent()
    {
        using ChapterSidebarViewModel sidebar = Sidebar();
        ChapterScanRowViewModel[] rows =
        [
            Row(sidebar, "root", 0, true), Row(sidebar, "first", 1, true),
            Row(sidebar, "first-child", 2, true), Row(sidebar, "excluded", 1, false),
            Row(sidebar, "promoted", 2, true)
        ];

        IReadOnlyList<ChapterNode> selected = ChapterSidebarViewModel.BuildSelectedPreview(rows);

        selected.Should().ContainSingle();
        selected[0].Children.Select(node => node.ChapterId).Should().Equal("first", "promoted");
        selected[0].Children[0].Children.Should().ContainSingle().Which.ChapterId.Should().Be("first-child");
    }

    private static ChapterSidebarViewModel Sidebar()
    {
        return new ChapterSidebarViewModel(() => throw new InvalidOperationException("Pure tree test"),
            () => DocumentInstanceId.New(), () => 1, _ => Task.CompletedTask, () => Task.CompletedTask);
    }

    private static ChapterScanRowViewModel Row(ChapterSidebarViewModel sidebar, string id, int depth, bool included)
    {
        return new ChapterScanRowViewModel(sidebar, ChapterNode.Leaf(id, id, new NavigationPosition(1)),
            depth, depth + 1, included, string.Empty);
    }
}
