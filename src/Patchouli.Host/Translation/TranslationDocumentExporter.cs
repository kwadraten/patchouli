using System.Text;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Host.Translation;

public enum TranslationExportMode
{
    Source,
    Translation,
    Bilingual
}

/// <summary>Exports committed content only. Partial translations stay explicitly identified.</summary>
public sealed class TranslationDocumentExporter(
    IPageService pages,
    IDocumentTreeService trees,
    IDocumentMarkdownCompiler markdown,
    IPageTranslationService translations)
{
    public async Task<Result<string>> ExportMarkdownAsync(DocumentInstanceId document,
        TranslationExportMode mode, CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<Page>> listed =
            await pages.ListPagesAsync(document, cancellationToken).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            return Result<string>.Failure(listed.ErrorCode!, listed.ErrorMessage!);
        }

        StringBuilder output = new();
        foreach (Page page in listed.Value.OrderBy(page => page.PageIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result<DocumentTreeRevision> revision = await trees
                .GetCurrentRevisionAsync(document, page.PageId, cancellationToken)
                .ConfigureAwait(false);
            if (revision.IsFailure)
            {
                return Result<string>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            Result<CompiledMarkdown> source = await markdown.CompilePageMarkdownAsync(revision.Value.TreeRevisionId,
                cancellationToken: cancellationToken, includeComplexTableHtml: true).ConfigureAwait(false);
            if (source.IsFailure)
            {
                return Result<string>.Failure(source.ErrorCode!, source.ErrorMessage!);
            }

            output.Append("## 第 ").Append(page.PageIndex + 1).AppendLine(" 页").AppendLine();
            if (mode is TranslationExportMode.Source or TranslationExportMode.Bilingual)
            {
                if (mode == TranslationExportMode.Bilingual)
                {
                    output.AppendLine("### 原文").AppendLine();
                }

                output.AppendLine(source.Value.Markdown).AppendLine();
            }

            if (mode is TranslationExportMode.Translation or TranslationExportMode.Bilingual)
            {
                TranslatedPageMarkdown? translated = await translations
                    .GetPageTranslationAsync(document, page.PageId, cancellationToken)
                    .ConfigureAwait(false);
                if (mode == TranslationExportMode.Bilingual)
                {
                    output.AppendLine("### 译文").AppendLine();
                }

                if (translated is null)
                {
                    output.AppendLine("> 此页尚无译文。").AppendLine();
                }
                else
                {
                    if (!translated.Status.IsCurrent || translated.Status.StaleBoxIds.Count > 0)
                    {
                        output.AppendLine("> 此页译文不完整或已过期；未翻译部分保留原文。").AppendLine();
                    }

                    output.AppendLine(translated.Markdown).AppendLine();
                }
            }
        }

        return Result<string>.Success(output.ToString());
    }
}
