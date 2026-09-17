using System.Text;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.Infrastructure.Documents.Translations;

/// <summary>
/// Outcome of validating one submitted page translation. <see cref="BoxFragments"/> maps each
/// content box to the storage form persisted in <c>translation_boxes</c>; <see cref="Errors"/>
/// lists every structural mismatch and is empty on success.
/// </summary>
public sealed record TranslationValidationResult(
    IReadOnlyDictionary<DocumentBoxId, string> BoxFragments,
    IReadOnlyList<TranslationStructureError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks that a submitted translation is structurally identical to the current page markdown
/// and maps every translated block back to the box that produced its source block. The
/// comparison is positional: heading levels, block kinds, table shapes and separator positions
/// must line up one-to-one, while the text itself is free to change.
/// </summary>
public sealed class TranslationStructureValidator
{
    private readonly IMarkdownEngine _markdown;

    public TranslationStructureValidator(IMarkdownEngine markdown)
    {
        _markdown = markdown;
    }

    public TranslationValidationResult Validate(
        CompiledMarkdown original,
        string translatedMarkdown,
        IReadOnlyList<DocumentBox> boxes)
    {
        IReadOnlyList<MarkdownBlock> originalBlocks = original.Document?.Blocks ?? Array.Empty<MarkdownBlock>();
        string translatedSource = translatedMarkdown ?? string.Empty;
        IReadOnlyList<MarkdownBlock> translatedBlocks = _markdown.Parse(translatedSource).Blocks;

        List<TranslationStructureError> errors = [];
        int blockCount = Math.Max(originalBlocks.Count, translatedBlocks.Count);
        for (int index = 0; index < blockCount; index++)
        {
            if (index >= originalBlocks.Count)
            {
                errors.Add(new TranslationStructureError(
                    index, "end of markdown", Describe(translatedBlocks[index], translatedSource)));
                continue;
            }

            if (index >= translatedBlocks.Count)
            {
                errors.Add(new TranslationStructureError(
                    index, Describe(originalBlocks[index], original.Markdown), "end of markdown"));
                continue;
            }

            string expected = Describe(originalBlocks[index], original.Markdown);
            string actual = Describe(translatedBlocks[index], translatedSource);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                errors.Add(new TranslationStructureError(index, expected, actual));
            }
        }

        if (errors.Count > 0)
        {
            return new TranslationValidationResult(new Dictionary<DocumentBoxId, string>(), errors);
        }

        Dictionary<DocumentBoxId, DocumentBox> boxesById = boxes.ToDictionary(box => box.BoxId);
        Dictionary<DocumentBoxId, StringBuilder> rawFragments = new();
        for (int index = 0; index < originalBlocks.Count; index++)
        {
            DocumentBoxId? owner = FindOwner(original, originalBlocks[index]);
            if (owner is null)
            {
                continue;
            }

            string slice = Slice(translatedSource, translatedBlocks[index]).Trim();
            if (slice.Length == 0)
            {
                continue;
            }

            if (!rawFragments.TryGetValue(owner.Value, out StringBuilder? builder))
            {
                builder = new StringBuilder();
                rawFragments[owner.Value] = builder;
            }
            else
            {
                builder.Append("\n\n");
            }

            builder.Append(slice);
        }

        Dictionary<DocumentBoxId, string> storage = new();
        foreach ((DocumentBoxId boxId, StringBuilder builder) in rawFragments)
        {
            if (!boxesById.TryGetValue(boxId, out DocumentBox? box))
            {
                continue;
            }

            string? stored = TranslationBoxCodec.ToStorage(box, builder.ToString());
            if (!string.IsNullOrWhiteSpace(stored))
            {
                storage[boxId] = stored;
            }
        }

        return new TranslationValidationResult(storage, errors);
    }

    private static string Describe(MarkdownBlock block, string source)
    {
        return block.Kind switch
        {
            "heading" => $"heading level {block.Level}",
            "table" => DescribeTable(Slice(source, block)),
            _ => block.Kind
        };
    }

    private static string DescribeTable(string markdown)
    {
        (int rows, int columns) = TableShape(markdown);
        return $"table {rows}x{columns}";
    }

    private static (int Rows, int Columns) TableShape(string markdown)
    {
        string[] lines = markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            return (0, 0);
        }

        bool hasSeparator = lines.Any(IsSeparatorRow);
        int rows = hasSeparator ? lines.Length - 1 : lines.Length;
        return (rows, CountCells(lines[0]));
    }

    private static bool IsSeparatorRow(string line)
    {
        string trimmed = line.Trim().Trim('|').Trim();
        return trimmed.Length > 0 && trimmed.All(character => character is '-' or ':' or ' ');
    }

    private static int CountCells(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Length;
    }

    private static DocumentBoxId? FindOwner(CompiledMarkdown original, MarkdownBlock block)
    {
        foreach (MarkdownSourceMapEntry entry in original.SourceMap)
        {
            if (block.Start < entry.Start + entry.Length && block.Start + block.Length > entry.Start)
            {
                return entry.BoxId;
            }
        }

        return null;
    }

    private static string Slice(string source, MarkdownBlock block)
    {
        if (string.IsNullOrEmpty(source) || block.Length <= 0 || block.Start < 0 || block.Start >= source.Length)
        {
            return string.Empty;
        }

        return source.Substring(block.Start, Math.Min(block.Length, source.Length - block.Start));
    }
}
