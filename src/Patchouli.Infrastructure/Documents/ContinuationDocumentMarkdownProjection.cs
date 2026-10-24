using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Documents;

/// <summary>Renders committed boxes as one ordered document so explicit cross-page continuation
/// chains are resolved by the same renderer used for page compilation.</summary>
public sealed class ContinuationDocumentMarkdownProjection(IMarkdownEngine markdown)
{
    public CompiledMarkdown Render(IReadOnlyList<DocumentBox> boxes, bool includeComplexTableHtml = true)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        // Each page arrives as an independent sibling forest. Stitch only its root sibling chain
        // into the next page's root chain; IDs, parent/child links and within-page ordering remain
        // unchanged. The copied graph is private to this render and never reaches persistence.
        List<DocumentBox> orderedPages = [];
        foreach (IGrouping<PageId, DocumentBox> page in boxes.GroupBy(box => box.PageId))
        {
            orderedPages.AddRange(page);
        }

        List<DocumentBox> projected = [.. orderedPages];
        List<List<DocumentBox>> orderedRootGroups = [];
        foreach (IGrouping<PageId, DocumentBox> page in orderedPages.GroupBy(box => box.PageId))
        {
            DocumentBox[] roots = page.Where(box => box.ParentBoxId is null).ToArray();
            if (roots.Length == 0)
            {
                continue;
            }

            HashSet<DocumentBoxId> referenced = roots
                .Where(box => box.NextSiblingBoxId is not null).Select(box => box.NextSiblingBoxId!.Value).ToHashSet();
            HashSet<DocumentBoxId> visited = [];
            foreach (DocumentBox head in roots.Where(box => !referenced.Contains(box.BoxId)))
            {
                List<DocumentBox> chain = [];
                DocumentBox? current = head;
                while (current is not null && visited.Add(current.BoxId))
                {
                    chain.Add(current);
                    current = current.NextSiblingBoxId is { } next
                        ? roots.FirstOrDefault(box => box.BoxId == next)
                        : null;
                }

                if (chain.Count > 0)
                {
                    orderedRootGroups.Add(chain);
                }
            }

            // Malformed/cyclic sibling data must not make a root disappear from this read-only
            // projection; preserve any unvisited roots in their input order as separate fragments.
            foreach (DocumentBox root in roots.Where(box => !visited.Contains(box.BoxId)))
            {
                orderedRootGroups.Add([root]);
            }
        }

        List<DocumentBoxId> rootOrder = orderedRootGroups.SelectMany(group => group)
            .Select(box => box.BoxId).Distinct().ToList();
        Dictionary<DocumentBoxId, int> boxIndexById = projected
            .Select((box, index) => (box.BoxId, index)).ToDictionary(item => item.BoxId, item => item.index);
        for (int index = 0; index < rootOrder.Count; index++)
        {
            DocumentBoxId id = rootOrder[index];
            int boxIndex = boxIndexById[id];
            DocumentBoxId? next = index + 1 < rootOrder.Count ? rootOrder[index + 1] : null;
            projected[boxIndex] = projected[boxIndex] with { NextSiblingBoxId = next };
        }

        return DocumentMarkdownRenderer.Render(projected, markdown, false, includeComplexTableHtml, null);
    }
}
