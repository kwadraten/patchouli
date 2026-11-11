using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Direct translation (issue #20) launch surface. The Library context menu and the menu bar start
/// one host run per selected item; the reading toolbar starts a run for its own document.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private AsyncCommand? _startFullTranslationCommand;

    /// <summary>Starts a full-document translation for every selected Library item.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand StartFullTranslationCommand => _startFullTranslationCommand ??=
        new AsyncCommand(RunFullTranslationFromLibrarySelectionAsync, activityTracker: ActivityTracker);

    private async Task RunFullTranslationFromLibrarySelectionAsync()
    {
        if (!Shell.CanModifyLibraryItems)
        {
            Report("回收站中的题录不可翻译。");
            return;
        }

        IReadOnlyList<LibraryItemViewModel> items = Shell.SelectedItems.Count > 0
            ? Shell.SelectedItems.ToArray()
            : Shell.SelectedItem is not null
                ? [Shell.SelectedItem]
                : [];
        if (items.Count == 0)
        {
            Report("请先选择一个题录。");
            return;
        }

        if (!HasOpenRuntimeDatabase)
        {
            Report("请先打开书库。");
            return;
        }

        HostServices services = await ServicesAsync();
        int libraryGeneration = LibraryGeneration;
        int startedDocuments = 0;
        int totalPages = 0;
        int skippedNoDocument = 0;
        List<Result<TranslationRunStatus>> failures = [];
        TranslationRunStatus? firstRun = null;
        try
        {
            foreach (LibraryItemViewModel item in items)
            {
                if (string.IsNullOrWhiteSpace(item.DocumentInstanceId) ||
                    !Guid.TryParse(item.DocumentInstanceId, out Guid documentId))
                {
                    skippedNoDocument++;
                    continue;
                }

                Result<TranslationRunStatus> started = await services.TranslationRuns.StartAsync(
                    new TranslationRunStartRequest(new DocumentInstanceId(documentId), []));
                if (!IsCurrentLibraryContext(services, libraryGeneration))
                {
                    return;
                }

                if (started.IsSuccess)
                {
                    firstRun ??= started.Value;
                    startedDocuments++;
                    totalPages += started.Value.TotalPages;
                }
                else
                {
                    failures.Add(started);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError($"无法启动全文翻译：{exception.Message}");
            return;
        }

        if (firstRun is { } focused)
        {
            await FocusTranslationRunInQueueAsync(focused);
        }

        if (startedDocuments == 0)
        {
            if (failures.Count > 0)
            {
                ReportError(DescribeTranslationStartFailure(failures[0]));
            }
            else
            {
                Report("所选内容没有可翻译的文档；请先为它关联或导入 PDF。");
            }

            return;
        }

        if (failures.Count == 0 && skippedNoDocument == 0)
        {
            Report(startedDocuments == 1
                ? $"已启动全文翻译：共 {totalPages} 页。"
                : $"已启动全文翻译：{startedDocuments} 个文档共 {totalPages} 页。");
            return;
        }

        string summary = $"已启动全文翻译：成功 {startedDocuments} 个文档（共 {totalPages} 页）";
        if (failures.Count > 0)
        {
            summary += $"，失败 {failures.Count} 个";
        }

        if (skippedNoDocument > 0)
        {
            summary += $"，无可翻译文档 {skippedNoDocument} 个";
        }

        if (failures.Count > 0)
        {
            string detail = failures[0].ErrorMessage ?? failures[0].ErrorCode ?? "未知错误";
            ReportError($"{summary}：{detail}");
        }
        else
        {
            Report($"{summary}。");
        }
    }

    private async Task FocusTranslationRunInQueueAsync(TranslationRunStatus status)
    {
        OcrQueue.FocusedTaskId = status.RunId.ToString();
        OcrQueue.SelectedTaskTabIndex = status.State == TranslationRunLifecycle.Running ? 0 : 1;
        OcrQueue.SelectedTaskType = "全部任务";
        await OpenOcrQueueAsync();
    }

    /// <summary>
    /// Starts a translation over every page of one document instance and opens the task queue
    /// page. An empty page list means the host translates the whole document. The host service owns
    /// validation (missing pages, credentials, unusable model) and returns an actionable failure.
    /// </summary>
    public async Task StartFullTranslationAsync(string? documentInstanceId)
    {
        if (!HasOpenRuntimeDatabase)
        {
            Report("请先打开书库。");
            return;
        }

        if (string.IsNullOrWhiteSpace(documentInstanceId) || !Guid.TryParse(documentInstanceId, out Guid documentId))
        {
            Report("所选内容没有可翻译的文档；请先为它关联或导入 PDF。");
            return;
        }

        try
        {
            HostServices services = await ServicesAsync();
            int libraryGeneration = LibraryGeneration;
            Result<TranslationRunStatus> started = await services.TranslationRuns.StartAsync(
                new TranslationRunStartRequest(new DocumentInstanceId(documentId), []));
            if (started.IsFailure)
            {
                ReportError(DescribeTranslationStartFailure(started));
                return;
            }

            TranslationRunStatus status = started.Value;
            if (!IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            await FocusTranslationRunInQueueAsync(status);
            Report($"已启动全文翻译：共 {status.TotalPages} 页。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError($"无法启动全文翻译：{exception.Message}");
        }
    }

    private static string DescribeTranslationStartFailure(Result<TranslationRunStatus> failure)
    {
        string detail = string.IsNullOrWhiteSpace(failure.ErrorMessage)
            ? failure.ErrorCode ?? "未知错误"
            : failure.ErrorMessage!;
        if (detail.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("api", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return $"无法启动全文翻译：{detail}。请检查所选文档的页面，以及「AI集成 → 模型连接」中的模型与凭据。";
        }

        return $"无法启动全文翻译：{detail}。";
    }
}
