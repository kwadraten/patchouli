using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Documents.Translations;

/// <summary>
/// Compiles a whole-page translated markdown from the persisted per-box rows. The page is
/// rendered with the same traversal and rendering rules as <see cref="DocumentMarkdownCompiler"/>
/// so the translated document is structurally identical to the source; boxes without a
/// translation row (or with an unknown payload) fall back to their source markdown.
/// </summary>
public sealed class PageTranslationCompiler : IPageTranslationCompiler
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IDocumentTreeService _trees;
    private readonly IMarkdownEngine _markdown;

    public PageTranslationCompiler(
        SqliteConnectionFactory connectionFactory,
        IDocumentTreeService trees,
        IMarkdownEngine markdown)
    {
        _connectionFactory = connectionFactory;
        _trees = trees;
        _markdown = markdown;
    }

    public async Task<Result<TranslatedPageMarkdown>> CompilePageTranslationAsync(
        PageId pageId,
        DocumentTreeRevisionId treeRevisionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
                treeRevisionId, cancellationToken);
            if (boxesResult.IsFailure)
            {
                return Result<TranslatedPageMarkdown>.Failure(
                    boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
            }

            IReadOnlyList<DocumentBox> boxes = boxesResult.Value;

            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            TranslationBoxRow[] rows = (await connection.QueryAsync<TranslationBoxRow>(
                """
                select box_id as BoxId, translated_md as TranslatedMd
                from translation_boxes
                where page_id = @PageId
                order by ordinal;
                """,
                new { PageId = pageId.ToString() })).ToArray();

            Dictionary<DocumentBoxId, string> translations = new(rows.Length);
            foreach (TranslationBoxRow row in rows)
            {
                translations[DocumentBoxId.Parse(row.BoxId)] = row.TranslatedMd;
            }

            DocumentBox[] contentBoxes = DocumentBoxProjection.ContentBoxes(boxes).ToArray();
            List<DocumentBoxId> staleBoxIds = [];
            int translatedBoxCount = 0;
            foreach (DocumentBox box in contentBoxes)
            {
                if (translations.ContainsKey(box.BoxId))
                {
                    translatedBoxCount++;
                }
                else
                {
                    staleBoxIds.Add(box.BoxId);
                }
            }

            PageTranslationStatus status = new(
                translatedBoxCount,
                contentBoxes.Length,
                staleBoxIds,
                treeRevisionId,
                true);

            string? FragmentOverride(DocumentBox box)
            {
                return translations.TryGetValue(box.BoxId, out string? stored)
                    ? TranslationBoxCodec.ToMarkdown(box, stored)
                    : null;
            }

            CompiledMarkdown compiled = DocumentMarkdownRenderer.Render(
                boxes, _markdown, false, false, FragmentOverride);
            return Result<TranslatedPageMarkdown>.Success(
                new TranslatedPageMarkdown(compiled.Markdown, compiled.SourceMap, status));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.page-translation-compiler"))
        {
            return Result<TranslatedPageMarkdown>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private sealed class TranslationBoxRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string TranslatedMd { get; set; } = string.Empty;
    }
}

/// <summary>
/// Converts a box's translated page fragment into the storage form kept in
/// <c>translation_boxes</c> and back. Titles store plain text, equations store LaTeX and code
/// boxes store raw code; every other payload stores its markdown fragment verbatim.
/// </summary>
internal static class TranslationBoxCodec
{
    public static string? ToStorage(DocumentBox box, string fragment)
    {
        string trimmed = fragment?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        return box.Payload switch
        {
            TextBoxPayload when box.BoxType == DocumentBoxType.Title => StripHeadingMarker(trimmed),
            TextBoxPayload => trimmed,
            EquationBoxPayload => StripMathFence(trimmed),
            ListBoxPayload => trimmed,
            TableBoxPayload => trimmed,
            CodeBoxPayload => StripCodeFence(trimmed),
            MediaBoxPayload => trimmed,
            _ => null
        };
    }

    public static string? ToMarkdown(DocumentBox box, string stored)
    {
        string value = stored ?? string.Empty;
        return box.Payload switch
        {
            TextBoxPayload when box.BoxType == DocumentBoxType.Title =>
                $"{new string('#', Math.Clamp(box.HeadingLevel ?? 1, 1, 6))} {value.Trim()}",
            TextBoxPayload => value,
            EquationBoxPayload => $"$$\n{value.Trim()}\n$$",
            ListBoxPayload => value,
            TableBoxPayload => value,
            CodeBoxPayload => DocumentMarkdownRenderer.CompileCode(value, box.CodeLanguage),
            MediaBoxPayload => value,
            _ => null
        };
    }

    private static string StripHeadingMarker(string value)
    {
        int index = 0;
        while (index < value.Length && value[index] == '#')
        {
            index++;
        }

        return value[index..].TrimStart();
    }

    private static string StripMathFence(string value)
    {
        return value.StartsWith("$$", StringComparison.Ordinal) &&
               value.EndsWith("$$", StringComparison.Ordinal) &&
               value.Length >= 4
            ? value[2..^2].Trim()
            : value;
    }

    private static string StripCodeFence(string fragment)
    {
        string[] lines = fragment.Replace("\r\n", "\n").Split('\n');
        int start = 0;
        int end = lines.Length;
        if (end > start && IsFence(lines[start]))
        {
            start++;
        }

        if (end > start && IsFence(lines[end - 1]))
        {
            end--;
        }

        return string.Join("\n", lines[start..end]).TrimEnd();
    }

    private static bool IsFence(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
               trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }
}
