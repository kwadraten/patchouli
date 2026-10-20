using System;
using System.Threading.Tasks;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// Direct translation (issue #20) launch surface. The Library context menu, the menu bar and the
/// reading toolbar all resolve the current document and start the same host run.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private AsyncCommand? _startFullTranslationCommand;

    /// <summary>Starts a full-document translation for the selected Library item.</summary>
    [ExcludeFromDerivedGeneration]
    public AsyncCommand StartFullTranslationCommand => _startFullTranslationCommand ??=
        new AsyncCommand(RunFullTranslationFromLibrarySelectionAsync, activityTracker: ActivityTracker);

    private async Task RunFullTranslationFromLibrarySelectionAsync()
    {
        LibraryItemViewModel? item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        await StartFullTranslationAsync(item.DocumentInstanceId);
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

            OcrQueue.FocusedTaskId = status.RunId.ToString();
            OcrQueue.SelectedTaskTabIndex = status.State == TranslationRunLifecycle.Running ? 0 : 1;
            OcrQueue.SelectedTaskType = "全部任务";
            await OpenOcrQueueAsync();
            Report($"已启动全文翻译：共 {status.TotalPages} 页。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError($"无法启动全文翻译：{exception.Message}" +
                        " 请检查「AI集成 → 模型连接」中的模型与凭据。");
        }
    }

    private static string DescribeTranslationStartFailure(Result<TranslationRunStatus> failure)
    {
        string detail = string.IsNullOrWhiteSpace(failure.ErrorMessage)
            ? failure.ErrorCode ?? "未知错误"
            : failure.ErrorMessage!;
        return $"无法启动全文翻译：{detail}。请检查所选文档的页面，以及「AI集成 → 模型连接」" +
               "中的模型与凭据。";
    }
}
