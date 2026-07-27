using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed class MinerUResultParser
{
    public Result<string> ParsePlainText(MinerUPreparedResult prepared)
    {
        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(prepared.ZipPath);
            string? contentListJson = reader.ReadFileContent("_content_list.json");
            if (string.IsNullOrWhiteSpace(contentListJson))
            {
                return Result<string>.Failure(MinerUProviderStatus.Failed,
                    "MinerU result zip did not contain a content list.");
            }

            MinerUContentListDocument? document = new MinerUContentListParser().Parse(contentListJson);
            if (document is null)
            {
                return Result<string>.Failure(MinerUProviderStatus.Failed,
                    "MinerU content list could not be parsed.");
            }

            string[] lines = document.Pages
                .SelectMany(page => page.Blocks)
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToArray();
            return lines.Length == 0
                ? Result<string>.Failure(MinerUProviderStatus.Failed,
                    "MinerU image extraction returned no text.")
                : Result<string>.Success(string.Join("\n", lines));
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.mineru-result-parser"))
        {
            return Result<string>.Failure(MinerUProviderStatus.Failed,
                $"MinerU result parsing failed: {ex.Message}");
        }
    }

    public Result<OcrDocumentTreeCandidate> ParseStructuredTree(
        MinerUPreparedResult prepared,
        IReadOnlyList<Page> pages)
    {
        Result<MinerUContentListDocument> contentList = ReadContentList(prepared.ZipPath);
        return contentList.IsFailure
            ? Result<OcrDocumentTreeCandidate>.Failure(contentList.ErrorCode!, contentList.ErrorMessage!)
            : Result<OcrDocumentTreeCandidate>.Success(
                new MinerUDocumentTreeCandidateMapper().MapDocument(contentList.Value, pages));
    }

    public Result<OcrDocumentTreeCandidate> ParseImageStructuredTree(
        MinerUPreparedResult prepared,
        Page page)
    {
        if (prepared.ImageContext is null)
        {
            return Result<OcrDocumentTreeCandidate>.Failure("invalid_state",
                "MinerU image-sourced results require an image context.");
        }

        Result<MinerUContentListDocument> contentList = ReadContentList(prepared.ZipPath);
        if (contentList.IsFailure)
        {
            return Result<OcrDocumentTreeCandidate>.Failure(contentList.ErrorCode!, contentList.ErrorMessage!);
        }

        MinerUContentListPage contentPage = contentList.Value.Pages.Count > 0
            ? contentList.Value.Pages[0]
            : new MinerUContentListPage(1, 1000, 1000, []);
        (OcrPageCandidate pageCandidate, IReadOnlyList<OcrDiagnostic> diagnostics) =
            new MinerUDocumentTreeCandidateMapper().MapImagePage(
                contentPage, page, prepared.ImageContext.RegionBBox);
        return Result<OcrDocumentTreeCandidate>.Success(
            new OcrDocumentTreeCandidate([pageCandidate], diagnostics));
    }

    private static Result<MinerUContentListDocument> ReadContentList(string zipPath)
    {
        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(zipPath);
            string? contentListJson = reader.ReadFileContent("_content_list_v2.json")
                                      ?? reader.ReadFileContent("_content_list.json");
            if (contentListJson is null)
            {
                return Result<MinerUContentListDocument>.Failure(
                    "tree_artifact_required",
                    "MinerU result has no verifiable content-list tree artifact; full.md cannot be imported as a pseudo box.");
            }

            MinerUContentListDocument? contentList = new MinerUContentListParser().Parse(contentListJson);
            return contentList is null || contentList.Pages.Count == 0
                ? Result<MinerUContentListDocument>.Failure("no_content", "MinerU content list is empty.")
                : Result<MinerUContentListDocument>.Success(contentList);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.mineru-result-parser"))
        {
            return Result<MinerUContentListDocument>.Failure(
                "zip_read_error",
                $"Failed to read result zip: {ex.Message}");
        }
    }
}
