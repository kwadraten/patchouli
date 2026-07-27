using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Documents;

internal static class DocumentBoxProjection
{
    public static IEnumerable<DocumentBox> ContentBoxes(IReadOnlyList<DocumentBox> boxes)
    {
        foreach (DocumentBox root in Siblings(boxes, null))
        {
            foreach (DocumentBox box in Descendants(boxes, root))
            {
                if (box.BoxType != DocumentBoxType.LogicalPage || box.Payload is not null)
                {
                    yield return box;
                }
            }
        }
    }

    private static IEnumerable<DocumentBox> Descendants(IReadOnlyList<DocumentBox> boxes, DocumentBox box)
    {
        yield return box;
        foreach (DocumentBox child in Siblings(boxes, box.BoxId))
        {
            foreach (DocumentBox descendant in Descendants(boxes, child))
            {
                yield return descendant;
            }
        }
    }

    public static IEnumerable<DocumentBox> Siblings(
        IReadOnlyList<DocumentBox> boxes,
        DocumentBoxId? parentId)
    {
        DocumentBox[] siblings = boxes.Where(box => box.ParentBoxId == parentId).ToArray();
        HashSet<DocumentBoxId> referenced = siblings
            .Where(box => box.NextSiblingBoxId is not null)
            .Select(box => box.NextSiblingBoxId!.Value)
            .ToHashSet();
        DocumentBox? current = siblings.SingleOrDefault(box => !referenced.Contains(box.BoxId));
        HashSet<DocumentBoxId> visited = [];
        while (current is not null && visited.Add(current.BoxId))
        {
            yield return current;
            current = current.NextSiblingBoxId is null
                ? null
                : siblings.SingleOrDefault(box => box.BoxId == current.NextSiblingBoxId.Value);
        }
    }

    public static string PlainText(DocumentBox box, IMarkdownEngine markdown)
    {
        return box.Payload switch
        {
            TextBoxPayload text => markdown.ToPlainText(text.Markdown),
            EquationBoxPayload equation => equation.Latex,
            ListBoxPayload list => markdown.ToPlainText(list.Markdown),
            TableBoxPayload table => markdown.ToPlainText(table.Markdown),
            CodeBoxPayload code => code.Code,
            MediaBoxPayload media => media.Description ??
                                     (box.BoxType == DocumentBoxType.Chart ? "[Chart]" : "[Image]"),
            _ => string.Empty
        };
    }
}
