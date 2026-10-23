using Patchouli.Core.Pagination;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Pure chapter-tree operations: lookup, structural transforms, flattening and the
/// merge/dedup rules used by the chapter sidebar and scan preview. ChapterNode trees are
/// immutable records, so every operation returns a new tree; the view model owns
/// persistence and notification. Kept internal and tested directly.
/// </summary>
internal static class ChapterTreeOps
{
    internal static IEnumerable<ChapterNode> Flatten(IEnumerable<ChapterNode> nodes)
    {
        return nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    }

    internal static IEnumerable<(ChapterNode Node, int Depth)> FlattenWithDepth(
        IEnumerable<ChapterNode> nodes, int depth)
    {
        return nodes.SelectMany(n => new[] { (n, depth) }.Concat(FlattenWithDepth(n.Children, depth + 1)));
    }

    internal static int Count(IEnumerable<ChapterNode> nodes)
    {
        return Flatten(nodes).Count();
    }

    internal static ChapterNode? Find(IEnumerable<ChapterNode> nodes, string? id)
    {
        return id is null ? null : Flatten(nodes).FirstOrDefault(n => n.ChapterId == id);
    }

    internal static string? FindParent(IEnumerable<ChapterNode> nodes, string? id)
    {
        return Flatten(nodes).FirstOrDefault(n => n.Children.Any(c => c.ChapterId == id))?.ChapterId;
    }

    internal static IReadOnlyList<ChapterNode> Transform(IEnumerable<ChapterNode> nodes, string id,
        Func<ChapterNode, ChapterNode> transform)
    {
        return nodes.Select(n =>
                n.ChapterId == id ? transform(n) : n with { Children = Transform(n.Children, id, transform) })
            .ToArray();
    }

    internal static IReadOnlyList<ChapterNode> Remove(IEnumerable<ChapterNode> nodes, string id)
    {
        return nodes.Where(n => n.ChapterId != id).Select(n => n with { Children = Remove(n.Children, id) })
            .ToArray();
    }

    internal static IReadOnlyList<ChapterNode> Insert(IReadOnlyList<ChapterNode> nodes, string? parent,
        string? after, ChapterNode node)
    {
        if (parent is not null)
        {
            return Transform(nodes, parent, n => n with { Children = Insert(n.Children, null, after, node) });
        }

        List<ChapterNode> result = nodes.ToList();
        int index = after is null ? result.Count : result.FindIndex(n => n.ChapterId == after) + 1;
        result.Insert(index, node);
        return result;
    }

    /// <summary>Incoming chapters whose ids already exist are dropped while their children promote.</summary>
    internal static IReadOnlyList<ChapterNode> AppendUnique(IReadOnlyList<ChapterNode> existing,
        IReadOnlyList<ChapterNode> incoming)
    {
        HashSet<string> ids = Flatten(existing).Select(n => n.ChapterId).ToHashSet(StringComparer.Ordinal);
        return existing.Concat(PruneDuplicateNodes(incoming, ids)).ToArray();
    }

    internal static IEnumerable<ChapterNode> PruneDuplicateNodes(IEnumerable<ChapterNode> nodes,
        HashSet<string> ids)
    {
        foreach (ChapterNode node in nodes)
        {
            ChapterNode[] children = PruneDuplicateNodes(node.Children, ids).ToArray();
            if (ids.Add(node.ChapterId))
            {
                yield return node with { Children = children };
            }
            else
            {
                foreach (ChapterNode child in children)
                {
                    yield return child;
                }
            }
        }
    }

    internal static IReadOnlyList<ChapterNode> BuildPreviewLevel(
        IReadOnlyList<(ChapterNode Node, int Depth)> rows,
        ref int cursor, int parentDepth)
    {
        List<ChapterNode> result = new();
        while (cursor < rows.Count && rows[cursor].Depth > parentDepth)
        {
            (ChapterNode node, int depth) = rows[cursor++];
            result.Add(node with { Children = BuildPreviewLevel(rows, ref cursor, depth) });
        }

        return result;
    }
}
