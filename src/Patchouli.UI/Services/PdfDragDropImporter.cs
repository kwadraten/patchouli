using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Services;

/// <summary>One file dropped on the window that was not imported.</summary>
public sealed record DroppedFileRejection(string FileName, string Reason);

/// <summary>One dropped PDF whose import failed.</summary>
public sealed record DroppedFileFailure(string FileName, string ErrorMessage);

/// <summary>Outcome of a window PDF drag-drop import batch.</summary>
public sealed record DragDropImportOutcome(
    int Added,
    int Duplicates,
    IReadOnlyList<DroppedFileFailure> Failures,
    IReadOnlyList<DroppedFileRejection> Rejected,
    bool Cancelled) : IOperationOutcome
{
    public bool IsSuccess => true;
    public bool IsCancelled => Cancelled;
    public string? ErrorMessage => null;
}

/// <summary>
/// Issue #11: window PDF drag-drop import. Follows the "PDF drag" behavior setting
/// (independent from MCP add), reuses the shared <see cref="IFileImportService"/> core,
/// and uses the same blocking progress dialog + interaction spec as scan import:
/// current file, total progress, final added/duplicates/failures, with visible progress
/// during long fingerprinting and the scan import's cancellation semantics (per-file
/// atomic commits: completed files are kept, not-started files are skipped; cancellation
/// never leaves half a book). Non-PDF files, directories and inaccessible files get
/// explicit feedback instead of silent success.
/// </summary>
public sealed class PdfDragDropImporter
{
    private readonly IFileImportService _fileImport;
    private readonly IModalOperationRunner _modalOperations;
    private readonly IDialogService _dialogs;

    public PdfDragDropImporter(
        IFileImportService fileImport,
        IModalOperationRunner modalOperations,
        IDialogService dialogs)
    {
        _fileImport = fileImport;
        _modalOperations = modalOperations;
        _dialogs = dialogs;
    }

    /// <summary>True when at least one of the dropped paths looks like an importable PDF.</summary>
    public static bool LooksLikePdfDrop(IEnumerable<string> paths)
    {
        return paths.Any(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsPdfName(path));
    }

    public async Task ImportAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        List<string> pdfs = [];
        List<DroppedFileRejection> rejected = [];
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fileName = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                rejected.Add(new DroppedFileRejection(fileName, "不支持导入目录，请拖入单个 PDF 文件。"));
            }
            else if (!File.Exists(path))
            {
                rejected.Add(new DroppedFileRejection(fileName, "文件不存在或无法访问。"));
            }
            else if (!IsPdfName(path))
            {
                rejected.Add(new DroppedFileRejection(fileName, "仅支持 PDF 文件。"));
            }
            else
            {
                pdfs.Add(path);
            }
        }

        if (pdfs.Count == 0)
        {
            await ShowSummaryAsync(new DragDropImportOutcome(0, 0, [], rejected, false));
            return;
        }

        DragDropImportOutcome outcome = await _modalOperations.RunAsync(
            new ModalOperationOptions(
                "导入 PDF",
                $"正在导入 {pdfs.Count} 个 PDF 文件。",
                CanCancel: true),
            context => RunImportsAsync(pdfs, rejected, context, cancellationToken));

        await ShowSummaryAsync(outcome);
    }

    private async Task<DragDropImportOutcome> RunImportsAsync(
        IReadOnlyList<string> pdfs,
        IReadOnlyList<DroppedFileRejection> rejected,
        ModalOperationContext context,
        CancellationToken cancellationToken)
    {
        int added = 0;
        int duplicates = 0;
        List<DroppedFileFailure> failures = [];
        try
        {
            for (int index = 0; index < pdfs.Count; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                string path = pdfs[index];
                string fileName = Path.GetFileName(path);
                // Visible progress during long fingerprinting: the import core reports per
                // file, and cancellation is observed between files so a book is never half
                // imported (per-file atomic commits).
                context.Report(index + 1, pdfs.Count, $"正在导入 {index + 1}/{pdfs.Count}：{fileName}", fileName);
                Result<FileImportResult> imported = await _fileImport.ImportAsync(
                    new FileImportRequest(path, "pdf", FileImportTrigger.DragDrop), context.CancellationToken);
                if (imported.IsSuccess)
                {
                    if (imported.Value.Reused)
                    {
                        duplicates++;
                    }
                    else
                    {
                        added++;
                    }

                    foreach (FileImportIssue issue in imported.Value.Issues)
                    {
                        await context.AddLogAsync($"{fileName}：{issue.Message}");
                    }
                }
                else
                {
                    failures.Add(new DroppedFileFailure(fileName, imported.ErrorMessage ?? "导入失败。"));
                    await context.AddLogAsync($"{fileName}：导入失败：{imported.ErrorMessage}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Completed files are kept, not-started files are skipped; the runner marks
            // the blocking dialog cancelled and releases the blocking state.
            return new DragDropImportOutcome(added, duplicates, failures, rejected, true);
        }

        return new DragDropImportOutcome(added, duplicates, failures, rejected, false);
    }

    private async Task ShowSummaryAsync(DragDropImportOutcome outcome)
    {
        List<string> lines = [];
        if (outcome.Cancelled)
        {
            lines.Add("已取消。已完成的文件保留，未开始的文件已跳过。");
        }

        lines.Add($"新增 {outcome.Added} 个，重复 {outcome.Duplicates} 个，失败 {outcome.Failures.Count} 个。");
        foreach (DroppedFileFailure failure in outcome.Failures)
        {
            lines.Add($"失败：{failure.FileName}：{failure.ErrorMessage}");
        }

        foreach (DroppedFileRejection rejection in outcome.Rejected)
        {
            lines.Add($"跳过：{rejection.FileName}：{rejection.Reason}");
        }

        await _dialogs.ShowDialogAsync(
            new ConfirmDialogViewModel("导入 PDF", string.Join("\n", lines), "确定"));
    }

    private static bool IsPdfName(string path)
    {
        return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }
}
