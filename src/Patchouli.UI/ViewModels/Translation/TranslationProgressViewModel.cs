using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels.Translation;

/// <summary>
/// Modeless progress surface for one direct translation run. It polls the host run service once per
/// second while the run is active; closing the window (or this view model) only stops polling and
/// never cancels the run. Cancellation is an explicit user action.
/// </summary>
public sealed partial class TranslationProgressViewModel : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<Task<ITranslationRunService>> _serviceFactory;
    private IDisposable? _timer;
    private bool _pollInFlight;
    private bool _cancelRequested;
    private bool _isDisposed;
    private string _failedPagesSignature = "";

    public TranslationProgressViewModel(Guid runId, TranslationRunStatus initial,
        Func<Task<ITranslationRunService>> serviceFactory)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(serviceFactory);
        RunId = runId;
        _serviceFactory = serviceFactory;
        CancelCommand = new AsyncCommand(CancelAsync, () => CanCancel);
        CloseCommand = new AsyncCommand(CloseAsync);
        Apply(initial);
        StartTimer();
    }

    public Guid RunId { get; }

    [ObservableProperty] public partial string StatusText { get; private set; } = "正在启动翻译…";

    [ObservableProperty] public partial string StateText { get; private set; } = "运行中";

    [ObservableProperty] public partial bool IsRunning { get; private set; } = true;

    [ObservableProperty] public partial bool CanCancel { get; private set; } = true;

    [ObservableProperty] public partial int TotalPages { get; private set; }

    [ObservableProperty] public partial int CompletedPages { get; private set; }

    [ObservableProperty] public partial int Succeeded { get; private set; }

    [ObservableProperty] public partial int Partial { get; private set; }

    [ObservableProperty] public partial int Failed { get; private set; }

    [ObservableProperty] public partial int Skipped { get; private set; }

    [ObservableProperty] public partial int CancelledPages { get; private set; }

    [ObservableProperty] public partial double ProgressPercent { get; private set; }

    [ObservableProperty] public partial string ProgressText { get; private set; } = "";

    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    /// <summary>Failed and partially successful pages; the reason is shown next to the page number.</summary>
    public ObservableCollection<TranslationFailedPageViewModel> FailedPages { get; } = [];

    public AsyncCommand CancelCommand { get; }
    public AsyncCommand CloseCommand { get; }

    /// <summary>Set by the window so the close command can close the modeless surface.</summary>
    public Action? RequestClose { get; set; }

    [ExcludeFromDerivedGeneration] public bool IsTerminal => !IsRunning;

    [ExcludeFromDerivedGeneration] public bool HasFailedPages => FailedPages.Count > 0;

    partial void OnIsRunningChanged(bool value)
    {
        Raise(nameof(IsTerminal));
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanCancelChanged(bool value)
    {
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void StartTimer()
    {
        if (_timer is not null || !IsRunning || _isDisposed)
        {
            return;
        }

        _timer = DispatcherTimer.Run(OnTimerTick, PollInterval, DispatcherPriority.Background);
    }

    private bool OnTimerTick()
    {
        if (_isDisposed || !IsRunning)
        {
            return false;
        }

        if (!_pollInFlight)
        {
            _pollInFlight = true;
            // The timer callback must not block the dispatcher; a failed poll is reported in-window.
            PollAsync().Observe(nameof(TranslationProgressViewModel), nameof(PollAsync));
        }

        return true;
    }

    private async Task PollAsync()
    {
        try
        {
            ITranslationRunService service = await _serviceFactory();
            Result<TranslationRunStatus> result = await service.GetAsync(RunId);
            if (_isDisposed)
            {
                return;
            }

            if (result.IsFailure)
            {
                ErrorMessage = $"无法读取翻译进度：{result.ErrorMessage}";
                return;
            }

            Apply(result.Value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"读取翻译进度失败：{exception.Message}";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private async Task CancelAsync()
    {
        if (!CanCancel)
        {
            return;
        }

        CanCancel = false;
        _cancelRequested = true;
        StatusText = "正在取消翻译…";
        try
        {
            ITranslationRunService service = await _serviceFactory();
            Result<TranslationRunStatus> result = await service.CancelAsync(RunId);
            if (_isDisposed)
            {
                return;
            }

            if (result.IsFailure)
            {
                _cancelRequested = false;
                CanCancel = IsRunning;
                ErrorMessage = $"取消翻译失败：{result.ErrorMessage}";
                StatusText = "取消请求失败，翻译仍在继续。";
                return;
            }

            Apply(result.Value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _cancelRequested = false;
            CanCancel = IsRunning;
            ErrorMessage = $"取消翻译失败：{exception.Message}";
        }
    }

    private Task CloseAsync()
    {
        RequestClose?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Projects one host snapshot into the displayed counts, failure list and terminal state.</summary>
    private void Apply(TranslationRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        TotalPages = status.TotalPages;
        CompletedPages = status.CompletedPages;
        Succeeded = status.Succeeded;
        Partial = status.Partial;
        Failed = status.Failed;
        Skipped = status.Skipped;
        CancelledPages = status.CancelledPages;
        ProgressPercent = status.TotalPages <= 0
            ? 0
            : Math.Clamp(status.CompletedPages * 100.0 / status.TotalPages, 0, 100);
        ProgressText = $"{status.CompletedPages} / {status.TotalPages} 页";
        RebuildFailedPages(status.Pages);
        ErrorMessage = status.State == TranslationRunLifecycle.Failed &&
                       !string.IsNullOrWhiteSpace(status.ErrorMessage)
            ? status.ErrorMessage
            : null;

        switch (status.State)
        {
            case TranslationRunLifecycle.Running:
                IsRunning = true;
                CanCancel = !_cancelRequested;
                StateText = _cancelRequested ? "正在取消" : "运行中";
                StatusText = _cancelRequested ? "正在取消翻译…" : "正在翻译…";
                StartTimer();
                return;
            case TranslationRunLifecycle.Completed:
                Finish("已完成", "翻译完成。", false);
                return;
            case TranslationRunLifecycle.Cancelled:
                Finish("已取消", "翻译已取消；已提交的页面保持原样。", false);
                return;
            case TranslationRunLifecycle.Failed:
                Finish("失败",
                    string.IsNullOrWhiteSpace(status.ErrorMessage) ? "翻译失败。" : $"翻译失败：{status.ErrorMessage}",
                    false);
                return;
            default:
                return;
        }
    }

    private void Finish(string stateText, string statusText, bool canCancel)
    {
        IsRunning = false;
        CanCancel = canCancel;
        StateText = stateText;
        StatusText = statusText;
        StopTimer();
    }

    private void RebuildFailedPages(IReadOnlyList<TranslationPageOutcome> pages)
    {
        TranslationPageOutcome[] failures = pages
            .Where(static page => page.State is TranslationPageRunState.Failed or TranslationPageRunState.Partial)
            .ToArray();
        string signature = string.Join('|', failures.Select(static page =>
            $"{page.PageIndex}:{page.State}:{page.ErrorCode}:{page.ErrorMessage}"));
        if (signature == _failedPagesSignature)
        {
            return;
        }

        _failedPagesSignature = signature;
        FailedPages.Clear();
        foreach (TranslationPageOutcome page in failures)
        {
            FailedPages.Add(new TranslationFailedPageViewModel(page));
        }

        Raise(nameof(HasFailedPages));
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposed = true;
            StopTimer();
        }

        base.Dispose(disposing);
    }
}

/// <summary>One failed or partially translated page of the run.</summary>
public sealed class TranslationFailedPageViewModel
{
    public TranslationFailedPageViewModel(TranslationPageOutcome page)
    {
        ArgumentNullException.ThrowIfNull(page);
        PageIndex = page.PageIndex;
        StateLabel = page.State == TranslationPageRunState.Partial ? "部分成功" : "失败";
        Error = page.ErrorMessage ?? page.ErrorCode ?? "未知错误";
    }

    public int PageIndex { get; }

    public string StateLabel { get; }

    public string Error { get; }

    public string PageText => $"第 {PageIndex} 页 · {StateLabel}";
}
