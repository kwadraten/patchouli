using System.Text;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;

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
    IPageTranslationService translations,
    IMarkdownEngine? markdownEngine = null)
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

        List<(int PageIndex, string Markdown)> pageSources = [];
        List<(int PageIndex, string Markdown)> pageTranslations = [];
        List<DocumentBox> documentBoxes = [];
        bool hasCrossPageContinuation = false;
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

            Result<IReadOnlyList<DocumentBox>> listedBoxes = await trees
                .ListBoxesAsync(revision.Value.TreeRevisionId, cancellationToken).ConfigureAwait(false);
            if (listedBoxes.IsFailure)
            {
                return Result<string>.Failure(listedBoxes.ErrorCode!, listedBoxes.ErrorMessage!);
            }

            documentBoxes.AddRange(listedBoxes.Value);
            hasCrossPageContinuation |= source.Value.Continuations.Any(continuation =>
                listedBoxes.Value.FirstOrDefault(box => box.BoxId == continuation.BoxId) is { } child &&
                listedBoxes.Value.FirstOrDefault(box => box.BoxId == continuation.FromBoxId) is null);
            pageSources.Add((page.PageIndex, source.Value.Markdown));

            if (mode is TranslationExportMode.Translation or TranslationExportMode.Bilingual)
            {
                StringBuilder pageTranslation = new();
                pageTranslation.Append("## 第 ").Append(page.PageIndex + 1).AppendLine(" 页").AppendLine();
                TranslatedPageMarkdown? translated = await translations
                    .GetPageTranslationAsync(document, page.PageId, cancellationToken)
                    .ConfigureAwait(false);
                if (mode == TranslationExportMode.Bilingual)
                {
                    pageTranslation.AppendLine("### 译文").AppendLine();
                }

                if (translated is null)
                {
                    pageTranslation.AppendLine("> 此页尚无译文。").AppendLine();
                }
                else
                {
                    if (!translated.Status.IsCurrent || translated.Status.StaleBoxIds.Count > 0)
                    {
                        pageTranslation.AppendLine("> 此页译文不完整或已过期；未翻译部分保留原文。").AppendLine();
                    }

                    pageTranslation.AppendLine(translated.Markdown).AppendLine();
                }

                pageTranslations.Add((page.PageIndex, pageTranslation.ToString()));
            }
        }

        if (mode is TranslationExportMode.Source or TranslationExportMode.Bilingual)
        {
            StringBuilder sourceOutput = new();
            if (hasCrossPageContinuation)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompiledMarkdown projected = new ContinuationDocumentMarkdownProjection(
                        markdownEngine ?? new MarkdigMarkdownEngine())
                    .Render(documentBoxes);
                cancellationToken.ThrowIfCancellationRequested();
                string pageSourcesNote = string.Join("、", listed.Value.OrderBy(page => page.PageIndex)
                    .Select(page => (page.PageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                sourceOutput.AppendLine("## 文档原文").AppendLine().AppendLine(projected.Markdown).AppendLine()
                    .Append("> 原文来源页：").Append(pageSourcesNote).AppendLine().AppendLine();
            }
            else if (mode == TranslationExportMode.Source)
            {
                foreach ((int pageIndex, string pageMarkdown) in pageSources)
                {
                    sourceOutput.Append("## 第 ").Append(pageIndex + 1).AppendLine(" 页").AppendLine();
                    sourceOutput.AppendLine(pageMarkdown).AppendLine();
                }
            }

            if (mode == TranslationExportMode.Source)
            {
                return Result<string>.Success(sourceOutput.ToString());
            }

            if (!hasCrossPageContinuation)
            {
                sourceOutput.Clear();
                foreach ((int pageIndex, string pageMarkdown) in pageSources)
                {
                    sourceOutput.Append("## 第 ").Append(pageIndex + 1).AppendLine(" 页").AppendLine()
                        .AppendLine("### 原文").AppendLine().AppendLine(pageMarkdown).AppendLine();
                    string translated = pageTranslations.First(item => item.PageIndex == pageIndex).Markdown;
                    int translationHeading = translated.IndexOf("### 译文", StringComparison.Ordinal);
                    if (translationHeading >= 0)
                    {
                        sourceOutput.AppendLine(translated[translationHeading..]);
                    }
                }

                return Result<string>.Success(sourceOutput.ToString());
            }

            sourceOutput.AppendLine("> 此文档含跨页接续；译文按页保留，未尝试合并接续内容。").AppendLine();
            sourceOutput.Append(string.Concat(pageTranslations.Select(item => item.Markdown)));
            return Result<string>.Success(sourceOutput.ToString());
        }

        if (hasCrossPageContinuation)
        {
            return Result<string>.Success("> 此文档含跨页接续；译文按页保留，未尝试合并接续内容。\n\n" +
                                          string.Concat(pageTranslations.Select(item => item.Markdown)));
        }

        return Result<string>.Success(string.Concat(pageTranslations.Select(item => item.Markdown)));
    }
}
