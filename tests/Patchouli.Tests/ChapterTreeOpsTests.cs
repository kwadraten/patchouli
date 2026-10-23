using FluentAssertions;
using Patchouli.Core.Pagination;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class ChapterTreeOpsTests
{
    [Fact]
    public void Find_and_FindParent_traverse_nested_trees()
    {
        IReadOnlyList<ChapterNode> tree = Tree();

        ChapterTreeOps.Find(tree, "b1").Should().NotBeNull();
        ChapterTreeOps.Find(tree, "b1")!.Title.Should().Be("B1");
        ChapterTreeOps.Find(tree, "missing").Should().BeNull();
        ChapterTreeOps.Find(tree, null).Should().BeNull();
        ChapterTreeOps.FindParent(tree, "b1").Should().Be("b");
        ChapterTreeOps.FindParent(tree, "a").Should().BeNull();
    }

    [Fact]
    public void Transform_and_Remove_return_new_trees()
    {
        IReadOnlyList<ChapterNode> tree = Tree();

        IReadOnlyList<ChapterNode> renamed = ChapterTreeOps.Transform(tree, "b1",
            node => node with { Title = "Renamed" });
        ChapterTreeOps.Find(renamed, "b1")!.Title.Should().Be("Renamed");
        ChapterTreeOps.Find(tree, "b1")!.Title.Should().Be("B1", "the input tree is untouched");

        IReadOnlyList<ChapterNode> removed = ChapterTreeOps.Remove(tree, "b1");
        ChapterTreeOps.Find(removed, "b1").Should().BeNull();
        ChapterTreeOps.Find(removed, "b")!.Children.Should().BeEmpty();
    }

    [Fact]
    public void Insert_places_root_and_nested_nodes_after_the_anchor()
    {
        IReadOnlyList<ChapterNode> tree = Tree();
        ChapterNode fresh = ChapterNode.Leaf("new", "New", Target(9));

        IReadOnlyList<ChapterNode> rootAfter = ChapterTreeOps.Insert(tree, null, "a", fresh);
        rootAfter.Select(node => node.ChapterId).Should().Equal("a", "new", "b");

        IReadOnlyList<ChapterNode> nested = ChapterTreeOps.Insert(tree, "b", "b1", fresh);
        ChapterTreeOps.Find(nested, "b")!.Children.Select(node => node.ChapterId)
            .Should().Equal("b1", "new");
    }

    [Fact]
    public void AppendUnique_drops_duplicate_ids_and_promotes_their_children()
    {
        IReadOnlyList<ChapterNode> existing = Tree();
        ChapterNode duplicate = ChapterNode.Leaf("b", "Dup B", Target(1)) with
        {
            Children = [ChapterNode.Leaf("b2", "B2", Target(3))]
        };

        IReadOnlyList<ChapterNode> merged = ChapterTreeOps.AppendUnique(existing, [duplicate]);

        ChapterTreeOps.Find(merged, "b")!.Title.Should().Be("B",
            "the existing node wins over the incoming duplicate");
        ChapterTreeOps.Find(merged, "b2").Should().NotBeNull(
            "unique children of a dropped duplicate are promoted");
    }

    [Fact]
    public void FlattenWithDepth_reports_document_order_and_nesting()
    {
        IReadOnlyList<(ChapterNode Node, int Depth)> flat =
            ChapterTreeOps.FlattenWithDepth(Tree(), 0).ToArray();

        flat.Select(entry => entry.Node.ChapterId).Should().Equal("a", "b", "b1");
        flat.Select(entry => entry.Depth).Should().Equal(0, 0, 1);
    }

    private static IReadOnlyList<ChapterNode> Tree()
    {
        return
        [
            ChapterNode.Leaf("a", "A", Target(1)),
            ChapterNode.Leaf("b", "B", Target(2)) with
            {
                Children = [ChapterNode.Leaf("b1", "B1", Target(3))]
            }
        ];
    }

    private static NavigationPosition Target(int pdfPage)
    {
        return new NavigationPosition(pdfPage);
    }
}
