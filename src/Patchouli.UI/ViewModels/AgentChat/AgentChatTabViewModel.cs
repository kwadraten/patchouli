using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Agent;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.AgentChat;

/// <summary>
///     The chat tab: a session list (active / history, selecting one re-opens it), the selected
///     session's view (message flow projected from the append-only event log plus the snapshot's
///     status), a message input that queues one inbox message, and stop / resume controls.
/// </summary>
/// <remarks>
///     <para>
///         <b>Closing the tab never stops the session (D9).</b> This view model owns no run: every
///         lifecycle transition happens through the host session service, and <see cref="Deactivate" />
///         only stops the periodic refresh. Closing the workspace tab therefore leaves the run
///         untouched and the tab can be opened again.
///     </para>
///     <para>
///         <b>Message flow.</b> The flow is built from <see cref="IAgentChatSessionService.ReadEventLogAsync" />,
///         whose entries carry a monotonic session-scoped sequence number; a locally appended item
///         (a send acknowledgement) takes the next sequence number above the material log, so a later
///         refresh replaces nothing and duplicates nothing.
///     </para>
/// </remarks>
public sealed partial class AgentChatTabViewModel : ViewModelBase
{
    /// <summary>Default period between two live refreshes of the selected session.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IAgentChatHost _host;
    private readonly Func<CancellationToken, Task<IAgentChatSessionService>> _serviceProvider;
    private readonly TimeProvider _time;
    private readonly TimeSpan _refreshInterval;

    private IAgentChatSessionService? _service;
    private IDisposable? _refreshTimer;
    private long _nextLocalSeq;
    private long _loadedSeq;
    private int _sessionGeneration;
    private int _isRefreshing;
    private bool _isActive;
    private string? _loadedSessionId;
    private bool _newSessionRequested;
    private readonly Dictionary<string, AgentChatMessageViewModel> _pendingMessages = new(StringComparer.Ordinal);
    private string _lastModelName = "模型";
    private readonly HashSet<string> _presentedMessages = new(StringComparer.Ordinal);
    private bool _isOpeningSession;
    private int _sessionNavigationGeneration;

    /// <summary>Creates the tab over the window it lives in.</summary>
    public AgentChatTabViewModel(IAgentChatHost host,
        Func<CancellationToken, Task<IAgentChatSessionService>> serviceProvider)
        : this(host, serviceProvider, TimeProvider.System, DefaultRefreshInterval)
    {
    }

    /// <summary>Creates the tab with an explicit clock and refresh period (used by tests).</summary>
    public AgentChatTabViewModel(IAgentChatHost host,
        Func<CancellationToken, Task<IAgentChatSessionService>> serviceProvider,
        TimeProvider time,
        TimeSpan refreshInterval)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(time);
        _host = host;
        _serviceProvider = serviceProvider;
        _time = time;
        _refreshInterval = refreshInterval;

        Register(Disposable.Create(StopRefreshTimer));

        // The commands are constructed under the window's host activity tracker when it has one, so
        // every chat command shows up as a correlated UI command in the activity pane.
        IDisposable? lease = host.CommandContext.ActivityTracker is { } activityTracker
            ? AsyncCommand.UseActivityTracker(activityTracker)
            : null;
        try
        {
            RefreshCommand = new AsyncCommand(RefreshAsync);
            OpenSessionCommand = new AsyncCommand(OpenSessionAsync, () => SelectedSession is not null);
            SendMessageCommand = new AsyncCommand(SendMessageAsync, CanSendMessage);
            ComposerActionCommand = new AsyncCommand(ExecuteComposerActionAsync, CanExecuteComposerAction);
            StopSessionCommand = new AsyncCommand(StopSessionAsync, () => CanStopSession);
            ResumeSessionCommand = new AsyncCommand(ResumeSessionAsync, () => CanResumeSession);
            PurgeSessionCommand = new AsyncCommand(PurgeSessionAsync, () => CanPurgeSession);
            NewSessionCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(NewSession, () => !IsSending);
        }
        finally
        {
            lease?.Dispose();
        }

        SelectedSession = null;
    }

    /// <summary>Sessions that are not terminal (running or waiting for an effect result).</summary>
    public ObservableCollection<AgentChatSessionViewModel> ActiveSessions { get; } = [];

    /// <summary>Terminal sessions (finished, failed, cancelled, stopped); stopped ones are resumable.</summary>
    public ObservableCollection<AgentChatSessionViewModel> HistorySessions { get; } = [];

    /// <summary>The message flow of the selected session, in material order.</summary>
    public ObservableCollection<AgentChatMessageViewModel> Messages { get; } = [];

    /// <summary>The selected session, or null when the list is empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSessionTitle))]
    public partial AgentChatSessionViewModel? SelectedSession { get; set; }

    /// <summary>Title of the selected session, or a placeholder when nothing is selected.</summary>
    [ExcludeFromDerivedGeneration]
    public string SelectedSessionTitle => SelectedSession?.Title ?? "新会话";

    /// <summary>A new conversation is persisted when its first message is sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose))]
    public partial bool IsNewSession { get; private set; } = true;

    /// <summary>True while the current prompt is being accepted by the host.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose))]
    public partial bool IsSending { get; private set; }

    /// <summary>The composer works for a new conversation or an accepting session.</summary>
    [ExcludeFromDerivedGeneration]
    public bool CanCompose => !IsSending && (IsNewSession || CanSendSession);

    [ObservableProperty] public partial bool ReduceMotion { get; set; }

    /// <summary>Opens an empty composer without stopping the previous session.</summary>
    public CommunityToolkit.Mvvm.Input.RelayCommand NewSessionCommand { get; }

    /// <summary>Collapsible conversation navigation for narrow workspace tabs.</summary>
    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    private void NewSession()
    {
        _newSessionRequested = true;
        Interlocked.Increment(ref _sessionGeneration);
        SelectedSession = null;
        _loadedSessionId = null;
        _loadedSeq = 0;
        _nextLocalSeq = 0;
        _pendingMessages.Clear();
        _presentedMessages.Clear();
        Messages.Clear();
        MessageInput = "";
        MessageInputNotice = "";
        IsNewSession = true;
    }

    /// <summary>Selects and loads a row from either session group.</summary>
    public async Task SelectSessionAsync(AgentChatSessionViewModel session)
    {
        _newSessionRequested = false;
        SelectedSession = session;
        MessageInput = "";
        MessageInputNotice = "";
        await LoadSelectedSessionAsync();
    }

    /// <summary>Focuses a launched session even if it has already finished or a new-chat draft is open.</summary>
    public async Task<bool> OpenSessionByIdAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        int request = Interlocked.Increment(ref _sessionNavigationGeneration);
        Interlocked.Increment(ref _sessionGeneration);
        _isOpeningSession = true;
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            AgentSessionSnapshot? snapshot = await service.TryOpenAsync(sessionId);
            if (snapshot is null || request != Volatile.Read(ref _sessionNavigationGeneration))
            {
                return false;
            }

            AgentChatSessionViewModel? row = ActiveSessions.Concat(HistorySessions)
                .FirstOrDefault(session => session.SessionId == sessionId);
            if (row is null)
            {
                row = new AgentChatSessionViewModel(new AgentChatSessionDescriptor(snapshot.SessionId,
                    snapshot.Status, snapshot.UpdatedAt, snapshot.Detail));
                (AgentChatPresentation.IsActive(snapshot.Status) ? ActiveSessions : HistorySessions).Add(row);
            }

            await SelectSessionAsync(row);
            RegroupSessions();
            return SelectedSession?.SessionId == sessionId;
        }
        finally
        {
            if (request == Volatile.Read(ref _sessionNavigationGeneration))
            {
                _isOpeningSession = false;
            }
        }
    }

    /// <summary>Status line under the tab title.</summary>
    [ObservableProperty]
    public partial string StatusSummary { get; private set; } = "尚未加载会话。";

    /// <summary>The message input.</summary>
    [ObservableProperty]
    public partial string MessageInput { get; set; } = "";

    /// <summary>True while a refresh, a send, a stop or a resume is in flight.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Chinese label of the selected session's host status ("未选择" when there is none).</summary>
    [ObservableProperty]
    public partial string SessionStatusText { get; private set; } = "未选择";

    /// <summary>Host detail text of the selected session.</summary>
    [ObservableProperty]
    public partial string SessionDetailText { get; private set; } = "";

    /// <summary>True when a session is selected.</summary>
    [ObservableProperty]
    public partial bool HasSelectedSession { get; private set; }

    /// <summary>True for a selected session that can still accept an inbox message.</summary>
    [ObservableProperty]
    public partial bool CanSendSession { get; private set; }

    /// <summary>True for a selected session that can be stopped.</summary>
    [ObservableProperty]
    public partial bool CanStopSession { get; private set; }

    /// <summary>True for a selected session that can be resumed (stopped, cancelled or waiting).</summary>
    [ObservableProperty]
    public partial bool CanResumeSession { get; private set; }

    /// <summary>True for a selected session that is not running and can therefore be purged (S5).</summary>
    [ObservableProperty]
    public partial bool CanPurgeSession { get; private set; }

    /// <summary>True for a failed selected session (styles the status badge).</summary>
    [ObservableProperty]
    public partial bool SelectedFailed { get; private set; }

    /// <summary>True for a finished selected session (styles the status badge).</summary>
    [ObservableProperty]
    public partial bool SelectedFinished { get; private set; }

    /// <summary>The send acknowledgement, e.g. "已加入，将在当前步骤完成后生效。"; empty when there is none.</summary>
    [ObservableProperty]
    public partial string MessageInputNotice { get; private set; } = "";

    /// <summary>True when the active group has at least one row (drives the list's empty state).</summary>
    [ObservableProperty]
    public partial bool HasActiveSessionRows { get; private set; }

    /// <summary>True when the active group is empty.</summary>
    [ObservableProperty]
    public partial bool NoActiveSessionRows { get; private set; } = true;

    /// <summary>True when the history group has at least one row (drives the list's empty state).</summary>
    [ObservableProperty]
    public partial bool HasHistorySessionRows { get; private set; }

    /// <summary>True when the history group is empty.</summary>
    [ObservableProperty]
    public partial bool NoHistorySessionRows { get; private set; } = true;

    /// <summary>Reloads the session list and the selected session's message flow.</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>Re-opens the selected session through the host service (a history session is loadable).</summary>
    public AsyncCommand OpenSessionCommand { get; }

    /// <summary>Queues the message input into the selected session's inbox.</summary>
    public AsyncCommand SendMessageCommand { get; }

    public AsyncCommand ComposerActionCommand { get; }

    [ExcludeFromDerivedGeneration]
    public bool ComposerStops => string.IsNullOrWhiteSpace(MessageInput) && CanStopSession;

    [ExcludeFromDerivedGeneration]
    public bool ComposerResumes => string.IsNullOrWhiteSpace(MessageInput) && !CanStopSession && CanResumeSession;

    [ExcludeFromDerivedGeneration] public bool ComposerSends => !ComposerStops && !ComposerResumes;

    [ExcludeFromDerivedGeneration]
    public string ComposerActionLabel => ComposerStops ? "停止当前轮次" : ComposerResumes ? "恢复会话" : "发送消息";

    private Task ExecuteComposerActionAsync()
    {
        return ComposerStops ? StopSessionAsync() : ComposerResumes ? ResumeSessionAsync() : SendMessageAsync();
    }

    private bool CanExecuteComposerAction()
    {
        return !IsSending && (ComposerStops || ComposerResumes || CanSendMessage());
    }

    private void RefreshComposerAction()
    {
        OnPropertyChanged(nameof(ComposerStops));
        OnPropertyChanged(nameof(ComposerResumes));
        OnPropertyChanged(nameof(ComposerSends));
        OnPropertyChanged(nameof(ComposerActionLabel));
        ComposerActionCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>Stops the selected session (resumable).</summary>
    public AsyncCommand StopSessionCommand { get; }

    /// <summary>Resumes the selected session.</summary>
    public AsyncCommand ResumeSessionCommand { get; }

    /// <summary>Physically purges the selected session's directory after a confirmation (S5).</summary>
    public AsyncCommand PurgeSessionCommand { get; }

    /// <summary>True while the tab is shown in the workspace (the periodic refresh runs).</summary>
    public bool IsActive()
    {
        return _isActive;
    }

    partial void OnSelectedSessionChanged(AgentChatSessionViewModel? value)
    {
        IsNewSession = value is null;
        OpenSessionCommand?.NotifyCanExecuteChanged();
        OnSelectedSessionStateChanged();
    }

    partial void OnMessageInputChanged(string value)
    {
        SendMessageCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
    }

    partial void OnCanSendSessionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCompose));
        SendMessageCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
    }

    partial void OnIsNewSessionChanged(bool value)
    {
        SendMessageCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
    }

    partial void OnIsSendingChanged(bool value)
    {
        SendMessageCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
        NewSessionCommand?.NotifyCanExecuteChanged();
    }

    partial void OnCanStopSessionChanged(bool value)
    {
        StopSessionCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
    }

    partial void OnCanResumeSessionChanged(bool value)
    {
        ResumeSessionCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
    }

    partial void OnCanPurgeSessionChanged(bool value)
    {
        PurgeSessionCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>
    ///     Shows the tab: loads the session list and refreshes the selected session periodically while
    ///     the tab stays shown. It is idempotent, so re-attaching a view (or opening the tab again)
    ///     simply re-arms the refresh.
    /// </summary>
    public Task ActivateAsync()
    {
        return ShowAsync();
    }

    /// <summary>
    ///     Activates or re-activates the tab if it is currently shown, and does nothing otherwise.
    ///     A workflow that starts a session while the chat tab is hidden must not start the periodic
    ///     refresh: the tab picks the new session up when it is opened again.
    /// </summary>
    public Task NotifyTabOpenedAsync()
    {
        return _isActive ? ShowAsync() : Task.CompletedTask;
    }

    private async Task ShowAsync()
    {
        _isActive = true;
        StartRefreshTimer();
        await RefreshAsync();
    }

    /// <summary>
    ///     Hides the tab and stops the periodic refresh. It deliberately issues no stop, cancel or
    ///     purge: closing the tab removes the front end only, and the session keeps running (accepted
    ///     criterion 2, D9). It is idempotent.
    /// </summary>
    public void Deactivate()
    {
        _isActive = false;
        StopRefreshTimer();
    }

    /// <summary>Loads the session list, then the selected session's snapshot and message flow.</summary>
    public async Task RefreshAsync()
    {
        if (IsSending || _isOpeningSession)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        IsBusy = true;
        int generation = Volatile.Read(ref _sessionGeneration);
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            IReadOnlyList<AgentSessionSnapshot> sessions = await service.ListSessionsAsync();
            if (IsSending || _isOpeningSession || generation != Volatile.Read(ref _sessionGeneration))
            {
                return;
            }

            string? previous = SelectedSession?.SessionId ?? _loadedSessionId;
            RebuildSessionGroups(sessions);

            // Keep the selection when it still exists; otherwise fall back to a live session, then to
            // any history session, so opening the tab always shows something to look at.
            SelectedSession = _newSessionRequested
                ? null
                : ActiveSessions.Concat(HistorySessions)
                      .FirstOrDefault(session => string.Equals(session.SessionId, previous,
                          StringComparison.Ordinal)) ??
                  ActiveSessions.FirstOrDefault() ?? HistorySessions.FirstOrDefault();
            await LoadSelectedSessionAsync();
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    /// <summary>Re-opens (loads) the selected session through the host service.</summary>
    public async Task OpenSessionAsync()
    {
        if (SelectedSession is not { } session)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            AgentSessionSnapshot? reopened = await service.TryOpenAsync(session.SessionId);
            if (reopened is null)
            {
                StatusSummary = $"会话 {session.SessionId} 无法打开（目录缺失或不可读）。";
                return;
            }

            // Re-opening validates the session directory and moves the run into the service's open set;
            // the message flow and the row status are then reloaded from the host.
            ApplySnapshot(reopened);
            await LoadSelectedSessionAsync();
            StatusSummary = $"已重新打开会话 {session.SessionId}（{AgentChatPresentation.StatusText(reopened.Status)}）。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///     Queues the message input into the selected session's inbox. A message never interrupts the
    ///     in-flight call: it is folded into history at the next Event boundary, which is what the
    ///     acknowledgement "已加入，将在当前步骤完成后生效。" tells the user.
    /// </summary>
    public async Task SendMessageAsync()
    {
        if (IsSending || string.IsNullOrWhiteSpace(MessageInput))
        {
            return;
        }

        if (IsNewSession)
        {
            IsSending = true;
            try
            {
                IAgentChatSessionService service = await ResolveServiceAsync();
                AgentSessionSnapshot created = await service.CreateChatAsync(MessageInput.Trim());
                AgentChatSessionViewModel row = new(new AgentChatSessionDescriptor(created.SessionId,
                    created.Status, created.UpdatedAt, created.Detail));
                ActiveSessions.Insert(0, row);
                _newSessionRequested = false;
                SelectedSession = row;
                RegroupSessions();
                MessageInput = "";
                MessageInputNotice = "会话已创建，正在生成回复。";
                await LoadSelectedSessionAsync();
            }
            catch (Exception exception)
            {
                MessageInputNotice = "无法创建会话：" + exception.GetBaseException().Message;
                _host.ReportError(MessageInputNotice);
            }
            finally
            {
                IsSending = false;
            }

            return;
        }

        if (SelectedSession is not { } session)
        {
            return;
        }

        string text = MessageInput.Trim();
        bool wasActive = AgentChatPresentation.IsActive(session.Status);
        if (text.Length == 0)
        {
            return;
        }

        if (!AgentChatPresentation.CanSend(session.Status))
        {
            MessageInputNotice =
                $"会话{AgentChatPresentation.StatusText(session.Status)}，消息未被接收。" + SessionSuffix(session.SessionId);
            return;
        }

        IsBusy = true;
        IsSending = true;
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            string messageId = "chat-" + Guid.NewGuid().ToString("N");
            AgentInboxMessage message = AgentInboxMessage.Create(messageId, text, _time.GetUtcNow());
            AgentMessageSendResult result = await service.SendAsync(session.SessionId, message);
            if (!result.Accepted)
            {
                MessageInputNotice = "消息未被接收，请保留输入后重试。" + SessionSuffix(session.SessionId);
                return;
            }

            if (result.Deduplicated)
            {
                MessageInputNotice = "该消息此前已加入，未重复追加。" + SessionSuffix(session.SessionId);
                return;
            }

            AgentChatMessageViewModel pending = new(new AgentChatMessage(NextLocalSeq(), AgentChatMessageKind.User,
                "用户（已加入）", text, message.ReceivedAt));
            _pendingMessages[messageId] = pending;
            Messages.Add(pending);
            MessageInput = "";
            MessageInputNotice = (wasActive ? "已加入，将在当前步骤完成后生效。" : "已继续对话，正在生成回复。") + SessionSuffix(session.SessionId);
            StatusSummary = MessageInputNotice;
        }
        finally
        {
            IsSending = false;
            IsBusy = false;
        }
    }

    /// <summary>Stops the selected session at the boundary. The session stays resumable.</summary>
    public async Task StopSessionAsync()
    {
        if (SelectedSession is not { } session)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            AgentSessionSnapshot stopped = await service.StopAsync(session.SessionId);
            ApplySnapshot(stopped);
            await LoadSelectedSessionAsync();
            StatusSummary = $"已停止会话 {stopped.SessionId}；会话保留，可随时恢复。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Resumes the selected session from its recorded results.</summary>
    public async Task ResumeSessionAsync()
    {
        if (SelectedSession is not { } session)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            AgentSessionSnapshot resumed = await service.ResumeAsync(session.SessionId);
            ApplySnapshot(resumed);
            await LoadSelectedSessionAsync();
            StatusSummary = $"已恢复会话 {resumed.SessionId}（{AgentChatPresentation.StatusText(resumed.Status)}）。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///     Physically purges the selected session's directory after an explicit confirmation (S5).
    ///     The purge touches only the session directory: items, original documents, translations and
    ///     OCR results are never affected. A running session can never reach this path: its purge
    ///     command is disabled (and says so) while it is running.
    /// </summary>
    public async Task PurgeSessionAsync()
    {
        if (SelectedSession is not { } session)
        {
            return;
        }

        if (!AgentChatPresentation.CanPurge(session.Status))
        {
            StatusSummary = $"会话{AgentChatPresentation.StatusText(session.Status)}，不能清除。" +
                            SessionSuffix(session.SessionId);
            return;
        }

        ConfirmDialogResult? choice = await _host.ShowDialogAsync<ConfirmDialogResult>(new ConfirmDialogViewModel(
            "清除会话",
            $"会话 {session.SessionId} 的记录和对应临时工作目录将被物理清除，且不可恢复。题录、原文、译文与 OCR 成果不受影响。",
            "清除", confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            StatusSummary = $"已取消清除会话 {session.SessionId}。";
            return;
        }

        IsBusy = true;
        try
        {
            IAgentChatSessionService service = await ResolveServiceAsync();
            bool purged = await service.PurgeAsync(session.SessionId);
            if (!purged)
            {
                StatusSummary = $"会话 {session.SessionId} 的目录无法清除（不存在或不可写）。";
                return;
            }

            ActiveSessions.Remove(session);
            HistorySessions.Remove(session);
            SelectedSession = ActiveSessions.FirstOrDefault() ?? HistorySessions.FirstOrDefault();
            HasActiveSessionRows = ActiveSessions.Count > 0;
            NoActiveSessionRows = ActiveSessions.Count == 0;
            HasHistorySessionRows = HistorySessions.Count > 0;
            NoHistorySessionRows = HistorySessions.Count == 0;
            StatusSummary = $"已清除会话 {session.SessionId} 的记录和临时工作目录（题录、原文、译文与 OCR 成果未受影响）。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- session list -------------------------------------------------------

    private void RebuildSessionGroups(IReadOnlyList<AgentSessionSnapshot> sessions)
    {
        List<AgentSessionSnapshot> ordered =
        [
            .. sessions.OrderBy(session => session.SessionId,
                StringComparer.Ordinal)
        ];
        Replace(ActiveSessions, ordered.Where(snapshot => AgentChatPresentation.IsActive(snapshot.Status)));
        Replace(HistorySessions, ordered.Where(snapshot => !AgentChatPresentation.IsActive(snapshot.Status)));
        HasActiveSessionRows = ActiveSessions.Count > 0;
        NoActiveSessionRows = ActiveSessions.Count == 0;
        HasHistorySessionRows = HistorySessions.Count > 0;
        NoHistorySessionRows = HistorySessions.Count == 0;
        StatusSummary = sessions.Count == 0
            ? "还没有会话，发送消息即可开始。"
            : $"活跃 {ActiveSessions.Count} 个 · 历史 {HistorySessions.Count} 个。";
    }

    private static void Replace(ObservableCollection<AgentChatSessionViewModel> target,
        IEnumerable<AgentSessionSnapshot> snapshots)
    {
        target.Clear();
        foreach (AgentSessionSnapshot snapshot in snapshots)
        {
            target.Add(new AgentChatSessionViewModel(
                new AgentChatSessionDescriptor(snapshot.SessionId, snapshot.Status, snapshot.UpdatedAt,
                    snapshot.Detail)));
        }
    }

    /// <summary>
    ///     Re-classifies the displayed rows after a lifecycle transition (stop, resume, finish) without
    ///     waiting for the next list refresh, so a stopped session leaves the active group immediately.
    ///     The row instances are kept, so the group counts and the empty states stay consistent.
    /// </summary>
    private void RegroupSessions()
    {
        foreach (AgentChatSessionViewModel row in ActiveSessions.Concat(HistorySessions).ToList())
        {
            ObservableCollection<AgentChatSessionViewModel> wanted =
                AgentChatPresentation.IsActive(row.Status) ? ActiveSessions : HistorySessions;
            ObservableCollection<AgentChatSessionViewModel> other =
                ReferenceEquals(wanted, ActiveSessions) ? HistorySessions : ActiveSessions;
            if (other.Remove(row))
            {
                wanted.Add(row);
            }
        }

        HasActiveSessionRows = ActiveSessions.Count > 0;
        NoActiveSessionRows = ActiveSessions.Count == 0;
        HasHistorySessionRows = HistorySessions.Count > 0;
        NoHistorySessionRows = HistorySessions.Count == 0;
    }

    private async Task LoadSelectedSessionAsync()
    {
        int generation = Interlocked.Increment(ref _sessionGeneration);
        if (SelectedSession is not { } session)
        {
            _loadedSessionId = null;
            _loadedSeq = 0;
            Messages.Clear();
            ApplySnapshot(null);
            return;
        }

        if (!string.Equals(_loadedSessionId, session.SessionId, StringComparison.Ordinal))
        {
            _loadedSessionId = session.SessionId;
            _loadedSeq = 0;
            _nextLocalSeq = 0;
            _pendingMessages.Clear();
            _presentedMessages.Clear();
            _lastModelName = "模型";
            Messages.Clear();
        }

        IAgentChatSessionService service = await ResolveServiceAsync();
        AgentSessionSnapshot? snapshot = await service.TryOpenAsync(session.SessionId);
        if (generation != Volatile.Read(ref _sessionGeneration))
        {
            return;
        }

        IReadOnlyList<AgentSessionLogEntry> entries = await service.ReadEventLogAsync(session.SessionId);
        if (generation != Volatile.Read(ref _sessionGeneration))
        {
            return;
        }

        AppendLog(entries);
        if (snapshot is { Status: AgentSessionStatus.Failed } && !string.IsNullOrWhiteSpace(snapshot.Detail)
                                                              && snapshot.Detail != nameof(AgentSessionStatus.Failed)
                                                              && !entries.Any(entry =>
                                                                  entry.Kind == AgentLogKinds.Status &&
                                                                  AgentChatStreamBuilder.StatusText(entry.Payload)
                                                                      .Contains(snapshot.Detail,
                                                                          StringComparison.Ordinal))
                                                              && _presentedMessages.Add("failure/" +
                                                                  snapshot.SessionId + "/" + snapshot.Detail))
        {
            AppendLocal(new AgentChatMessage(NextLocalSeq(), AgentChatMessageKind.Progress, "失败详情",
                snapshot.Detail, snapshot.UpdatedAt) { State = "failed", Output = snapshot.Detail });
        }

        ApplySnapshot(snapshot);
        if (Messages.Count == 0)
        {
            AppendLocal(new AgentChatMessage(NextLocalSeq(), AgentChatMessageKind.System, "会话",
                "该会话还没有任何事件：创建会话后第一次推进边界会写入启动事件。", null));
        }
    }

    private void AppendLog(IReadOnlyList<AgentSessionLogEntry> entries)
    {
        if (_loadedSeq == 0 && entries.Count > 0)
        {
            foreach (AgentChatMessageViewModel hint in Messages
                         .Where(message => message.IsSystem && message.RecordedAt is null).ToList())
            {
                Messages.Remove(hint);
            }
        }

        foreach (AgentSessionLogEntry entry in entries
                     .Where(entry => entry.Seq > _loadedSeq)
                     .OrderBy(entry => entry.Seq))
        {
            if (entry.Kind == AgentLogKinds.EffectResult)
            {
                using JsonDocument payload = JsonDocument.Parse(entry.Payload);
                if (payload.RootElement.TryGetProperty("model", out JsonElement model) &&
                    model.ValueKind == JsonValueKind.String)
                {
                    _lastModelName = model.GetString() ?? "模型";
                }
                else if (payload.RootElement.TryGetProperty("summary", out JsonElement summary))
                {
                    Match match =
                        Regex.Match(summary.GetString() ?? "", @"model ([^)]+)\)");
                    if (match.Success)
                    {
                        _lastModelName = match.Groups[1].Value;
                    }
                }
            }

            if (AgentChatStreamBuilder.Map(entry) is { } message)
            {
                if (message.Kind == AgentChatMessageKind.Assistant && message.Title == "模型")
                {
                    message = message with { Title = _lastModelName };
                }

                if (Messages.FirstOrDefault(row => row.IsActivity &&
                                                   (message.OperationId.Length > 0
                                                       ? row.OperationId == message.OperationId
                                                       : message.EffectId > 0 && row.OperationId.Length == 0 &&
                                                         row.EffectId == message.EffectId)) is { } activity)
                {
                    activity.UpdateActivity(message);
                    _loadedSeq = entry.Seq;
                    continue;
                }

                if (message.Kind == AgentChatMessageKind.User &&
                    AgentChatStreamBuilder.MessageId(entry) is { Length: > 0 } messageId)
                {
                    if (_pendingMessages.Remove(messageId, out AgentChatMessageViewModel? pending))
                    {
                        Messages.Remove(pending);
                    }

                    if (!_presentedMessages.Add(messageId))
                    {
                        _loadedSeq = entry.Seq;
                        continue;
                    }
                }

                AppendLocal(message);
            }

            _loadedSeq = entry.Seq;
        }
    }

    // ---- selected-session state --------------------------------------------

    private void ApplySnapshot(AgentSessionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            OnSelectedSessionStateChanged();
            return;
        }

        AgentChatSessionViewModel? row = ActiveSessions.Concat(HistorySessions).FirstOrDefault(session =>
            string.Equals(session.SessionId, snapshot.SessionId, StringComparison.Ordinal));
        if (row is not null)
        {
            row.Status = snapshot.Status;
            row.UpdatedAt = snapshot.UpdatedAt;
            row.Detail = snapshot.Detail;
            RegroupSessions();
        }

        if (!string.Equals(SelectedSession?.SessionId, snapshot.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        SelectedSession = row ?? SelectedSession;
        OnSelectedSessionStateChanged();
    }

    private void OnSelectedSessionStateChanged()
    {
        AgentSessionStatus? status = SelectedSession?.Status;
        HasSelectedSession = SelectedSession is not null;
        if (status == AgentSessionStatus.Idle)
        {
            MessageInputNotice = "";
        }

        SessionStatusText = status is { } value ? AgentChatPresentation.StatusText(value) : "未选择";
        SessionDetailText = SelectedSession?.Detail ?? "";
        CanSendSession = status is { } send && AgentChatPresentation.CanSend(send);
        CanStopSession = status is { } stop && AgentChatPresentation.CanStop(stop);
        CanResumeSession = status is { } resume && AgentChatPresentation.CanResume(resume);
        CanPurgeSession = status is { } purge && AgentChatPresentation.CanPurge(purge);
        SelectedFailed = status == AgentSessionStatus.Failed;
        SelectedFinished = status == AgentSessionStatus.Finished;
        OpenSessionCommand?.NotifyCanExecuteChanged();
        SendMessageCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
        StopSessionCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
        ResumeSessionCommand?.NotifyCanExecuteChanged();
        RefreshComposerAction();
        PurgeSessionCommand?.NotifyCanExecuteChanged();
    }

    private bool CanSendMessage()
    {
        return CanCompose && !string.IsNullOrWhiteSpace(MessageInput);
    }

    private void AppendLocal(AgentChatMessage message)
    {
        Messages.Add(new AgentChatMessageViewModel(message));
    }

    private long NextLocalSeq()
    {
        _nextLocalSeq = Math.Max(_nextLocalSeq, _loadedSeq) + 1;
        return _nextLocalSeq;
    }

    private static string SessionSuffix(string sessionId)
    {
        return $"（会话 {sessionId}）";
    }

    // ---- service and refresh loop ------------------------------------------

    private async Task<IAgentChatSessionService> ResolveServiceAsync()
    {
        return _service ??= await _serviceProvider(CancellationToken.None);
    }

    private void StartRefreshTimer()
    {
        if (_refreshTimer is not null || _refreshInterval <= TimeSpan.Zero)
        {
            return;
        }

        _refreshTimer = DispatcherTimer.Run(OnRefreshTick, _refreshInterval, DispatcherPriority.Background);
    }

    private bool OnRefreshTick()
    {
        bool active = IsActive();
        if (!active || IsBusy)
        {
            return active;
        }

        // The periodic refresh never blocks the dispatcher; a failure is reported instead of faulting
        // the timer callback.
        _ = RefreshAsync().ContinueWith(
            task => _host.ReportError($"刷新会话失败：{task.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return true;
    }

    private void StopRefreshTimer()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }
}
