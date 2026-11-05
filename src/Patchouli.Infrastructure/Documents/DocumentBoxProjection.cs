using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Documents;

internal static class DocumentBoxProjection
{
    private const string SqlWhitespace =
        "char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)";

    // MinerU v3 merged continuation text into the preceding box. Empty followers are
    // retained as source evidence, but must not be submitted or counted as missing translations.
    public static bool IsEmptyContinuation(DocumentBox box)
    {
        if (box.ContinuesFromBoxId is null && box.ContinuationState is null)
        {
            return false;
        }

        return box.Payload switch
        {
            TextBoxPayload text => string.IsNullOrWhiteSpace(text.Markdown),
            ListBoxPayload list => string.IsNullOrWhiteSpace(list.Markdown),
            TableBoxPayload table => string.IsNullOrWhiteSpace(table.Markdown) &&
                                     string.IsNullOrWhiteSpace(table.Html),
            null => true,
            _ => false
        };
    }

    public static IEnumerable<DocumentBox> TranslationContentBoxes(IReadOnlyList<DocumentBox> boxes)
    {
        return ContentBoxes(boxes).Where(box => !IsEmptyContinuation(box));
    }

    // SQL counterpart for the b alias used by translation directory projections.
    // Match .NET whitespace, including historical Unicode spaces, rather than SQLite's space-only trim.
    public static string TranslationContentSqlPredicate { get; } =
        "(b.box_type <> 'logical_page' or b.payload_json is not null) and not (" +
        "(b.continues_from_box_id is not null or b.continuation_state is not null) and " +
        "b.box_type in ('text', 'ref_text', 'list', 'table') and " +
        $"trim(coalesce(json_extract(b.payload_json, '$.markdown'), ''), {SqlWhitespace}) = '' and " +
        $"trim(coalesce(json_extract(b.payload_json, '$.html'), ''), {SqlWhitespace}) = '')";

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
        string plainText = box.Payload switch
        {
            TextBoxPayload text => markdown.ToPlainText(text.Markdown),
            AlgorithmBoxPayload algorithm => markdown.ToPlainText(algorithm.Markdown),
            EquationBoxPayload equation => equation.Latex,
            ListBoxPayload list => markdown.ToPlainText(list.Markdown),
            TableBoxPayload table => TableContentAdapter.TryGetHtml(table, out _)
                ? TableContentAdapter.GetPlainText(table)
                : markdown.ToPlainText(table.Markdown),
            CodeBoxPayload code => code.Code,
            MediaBoxPayload media => media.Description ??
                                     (box.BoxType == DocumentBoxType.Chart ? "[Chart]" : "[Image]"),
            _ => string.Empty
        };
        return string.Join("\n", new[] { plainText }.Concat((box.Payload?.Annotations ?? [])
                .Select(annotation => markdown.ToPlainText(annotation.Markdown)))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
