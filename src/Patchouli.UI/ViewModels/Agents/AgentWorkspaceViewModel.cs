using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Agents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.Terminal;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Agents;

/// <summary>One library item offered by the Zed-style @-reference picker.</summary>
public sealed class AgentItemReferenceViewModel
{
    public AgentItemReferenceViewModel(LibraryItemRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ItemId = row.ItemId;
        ItemIdText = row.ItemId.ToString();
        CanonicalUri = McpResourceUris.ItemUri(row.ItemId);
        Title = string.IsNullOrWhiteSpace(row.Title) ? "未命名题录" : row.Title.Trim();
        ItemType = row.ItemType;
        Authors = row.Authors;
    }

    public ItemId ItemId { get; }
    public string ItemIdText { get; }
    public string CanonicalUri { get; }
    public string Title { get; }
    public string ItemType { get; }
    public string Authors { get; }

    /// <summary>Client-side haystack. Source paths are deliberately never included.</summary>
    public string SearchText => $"{Title}\n{ItemIdText}\n{CanonicalUri}\n{ItemType}\n{Authors}";
}

/// <summary>An editable preset prompt. Applying one only fills the composer; it never sends.</summary>
public sealed record AgentPromptPreset(string Key, string Title, string Template);

/// <summary>
/// Issue #19 D3: the Agent integration workspace page. It owns the embedded terminal, the Zed-style
/// @-reference picker, the four editable preset prompts and the confirmed MCP config write action.
/// </summary>
public sealed partial class AgentWorkspaceViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<AgentPromptPreset> PromptPresets =
    [
        new("organize",
            "整理题录",
            "请检索网络，并对照对应的OCR文本（如果存在的话），为文献库中信息尚不完整、呈现为通用类型的文献更新题录信息。除了必要的作者、题目、出版地、文献来源之外，更新过程中还要注意获取图书分类以及文献在主流图书馆和文献数据库网站上的唯一标识符，前者需要写入标签，后者需要写入唯一标识符区域。"),
        new("investigate",
            "调查主题",
            "请在文献库中展开调查。调查结果应以文献为依据，你需要使用当前默认CSL样式渲染对应文献的引文，并附加上对应出处的URI超链接以表示文献出处。需要调查的主题是："),
        new("outline",
            "梳理文献",
            "你需要梳理文献。梳理文献需要揭示文献的主旨、研究方法、全文架构、核心概念等方面。为了清晰展示结构性要素，你应该在必要的时候使用mermaid绘图。需要梳理的文献是："),
        new("socratic",
            "反向提问",
            "为了帮助用户更加深刻地理解特定文献，你必须从头到尾阅读一遍文献，提炼文献的章节构造，并找到每个章节中被文献声称为确凿无疑的知识性内容，然后根据你的理解按照章节顺序依次以选择题的形式向用户提问，直至所有章节都被提问完成。在提问过程中，若用户提出了不同见解，你需要在忠实文献内容的前提下，与用户争辩。一旦所有提问都被完成，你需要将所有提问过的内容最终总结为报告以示你和用户最终达成一致的理解。需要反向提问式教学的文献是：")
    ];

    private readonly MainWindowViewModel _main;
    private readonly TerminalSessionManager _sessions = new();
    private readonly List<AgentItemReferenceViewModel> _allItems = [];
    private string _mcpToken = CreatePlaceholderMcpToken();
    private bool _mcpTokenFromSettings;
    private HostServices? _services;
    private AgentLaunchSpec? _launchSpec;
    private EmbeddedTerminal? _terminal;
    private CancellationTokenSource? _itemLoadCancellation;
    private bool _agentReady;
    private bool _isRefreshing;
    private bool _isLoadingItems;
    private bool _isStopping;
    private bool _shutdown;

    public AgentWorkspaceViewModel(MainWindowViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        StartCommand = new AsyncCommand(StartAsync, activityTracker: _main.ActivityTracker);
        StopCommand = new AsyncCommand(StopAsync, activityTracker: _main.ActivityTracker);
        RestartCommand = new AsyncCommand(RestartAsync, activityTracker: _main.ActivityTracker);
        RefreshAgentCommand = new AsyncCommand(() => RefreshAsync(true), activityTracker: _main.ActivityTracker);
        OpenAgentSettingsCommand = new AsyncCommand(OpenAgentSettingsAsync, activityTracker: _main.ActivityTracker);
        CopyInputCommand = new AsyncCommand(CopyInputAsync, activityTracker: _main.ActivityTracker);
        InjectInputCommand = new AsyncCommand(InjectInputAsync, activityTracker: _main.ActivityTracker);
        WriteMcpConfigCommand = new AsyncCommand(WriteMcpConfigAsync, activityTracker: _main.ActivityTracker);
        OpenItemPickerCommand = new AsyncCommand(OpenItemPickerAsync, activityTracker: _main.ActivityTracker);
        RefreshItemsCommand = new AsyncCommand(RefreshItemsAsync, activityTracker: _main.ActivityTracker);
        CloseItemPickerCommand = new RelayCommand(_ => CloseItemPicker());
        ApplyPresetCommand = new RelayCommand(parameter => ApplyPreset(parameter as string));
    }

    public IReadOnlyList<AgentPromptPreset> Presets => PromptPresets;

    public ObservableCollection<AgentItemReferenceViewModel> FilteredItems { get; } = new();

    [ObservableProperty] public partial string AgentDisplayName { get; private set; } = "Agent";

    [ObservableProperty] public partial string AgentVersionText { get; private set; } = "未检测";

    [ObservableProperty] public partial string ConnectionStatusText { get; private set; } = "未启动";

    [ObservableProperty] public partial string EmptyStateTitle { get; private set; } = "正在检测 Agent…";

    [ObservableProperty] public partial string EmptyStateMessage { get; private set; } = "正在读取 Agent 集成设置与可执行文件。";

    [ObservableProperty] public partial string McpStatusText { get; private set; } = "";

    [ObservableProperty] public partial string InputText { get; set; } = "";

    [ObservableProperty] public partial string ItemSearchText { get; set; } = "";

    [ObservableProperty] public partial AgentItemReferenceViewModel? SelectedItem { get; set; }

    [ObservableProperty] public partial bool IsItemPickerOpen { get; set; }

    [ObservableProperty] public partial string ItemPickerStatusText { get; private set; } = "尚未加载题录。";

    [ObservableProperty] public partial bool IsChecking { get; private set; }

    [ObservableProperty] public partial bool IsStarting { get; private set; }

    [ObservableProperty] public partial bool IsRunning { get; private set; }

    [ObservableProperty] public partial bool HasLaunched { get; private set; }

    [ObservableProperty] public partial bool IsIntegrationDisabled { get; private set; }

    [ObservableProperty] public partial bool IsAgentMissing { get; private set; }

    [ObservableProperty] public partial bool IsLoadingItems { get; private set; }

    [ExcludeFromDerivedGeneration] public EmbeddedTerminal? Terminal { get; private set; }

    [ExcludeFromDerivedGeneration] public bool IsAgentReady => _agentReady;
    [ExcludeFromDerivedGeneration] public bool IsBusy => IsChecking || IsStarting || _isStopping;
    [ExcludeFromDerivedGeneration] public bool ShowEmptyState => !HasLaunched;
    [ExcludeFromDerivedGeneration] public bool ShowTerminal => HasLaunched;
    [ExcludeFromDerivedGeneration] public bool CanStart => IsAgentReady && !IsStarting && !IsRunning && !_isStopping;
    [ExcludeFromDerivedGeneration] public bool CanStop => IsRunning && !_isStopping;
    [ExcludeFromDerivedGeneration] public bool CanRestart => IsAgentReady && !IsStarting && !_isStopping;
    [ExcludeFromDerivedGeneration] public bool CanWriteMcpConfig => HasLaunched;
    [ExcludeFromDerivedGeneration] public bool CanInjectInput => IsRunning && !string.IsNullOrWhiteSpace(InputText);
    [ExcludeFromDerivedGeneration] public bool CanCopyInput => !string.IsNullOrWhiteSpace(InputText);

    [ExcludeFromDerivedGeneration]
    public bool ShowSettingsShortcut =>
        IsIntegrationDisabled || IsAgentMissing || (!IsAgentReady && !IsChecking && _services is not null);

    [ExcludeFromDerivedGeneration] public bool ShowRetryShortcut => !IsChecking && !IsStarting;
    [ExcludeFromDerivedGeneration] public bool ShowStartShortcut => IsAgentReady && !HasLaunched && !IsStarting;
    [ExcludeFromDerivedGeneration] public bool HasActiveSession => IsRunning && _terminal is not null;

    internal bool CloseApproved { get; set; }

    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RestartCommand { get; }
    public AsyncCommand RefreshAgentCommand { get; }
    public AsyncCommand OpenAgentSettingsCommand { get; }
    public AsyncCommand CopyInputCommand { get; }
    public AsyncCommand InjectInputCommand { get; }
    public AsyncCommand WriteMcpConfigCommand { get; }
    public AsyncCommand OpenItemPickerCommand { get; }
    public AsyncCommand RefreshItemsCommand { get; }
    public RelayCommand CloseItemPickerCommand { get; }
    public RelayCommand ApplyPresetCommand { get; }

    partial void OnInputTextChanged(string value)
    {
        RaiseDerivedState();
    }

    partial void OnSelectedItemChanged(AgentItemReferenceViewModel? value)
    {
        RaiseDerivedState();
    }

    partial void OnHasLaunchedChanged(bool value)
    {
        RaiseDerivedState();
    }

    partial void OnIsRunningChanged(bool value)
    {
        RaiseDerivedState();
    }

    partial void OnItemSearchTextChanged(string value)
    {
        FilterItems();
    }

    partial void OnIsCheckingChanged(bool value)
    {
        RaiseDerivedState();
    }

    partial void OnIsStartingChanged(bool value)
    {
        RaiseDerivedState();
    }

    partial void OnIsIntegrationDisabledChanged(bool value)
    {
        RaiseDerivedState();
    }

    partial void OnIsAgentMissingChanged(bool value)
    {
        RaiseDerivedState();
    }

    /// <summary>Called when a page instance is attached to the cached view model.</summary>
    public void OnPageAttached()
    {
        // Realize the terminal before the user can press Start: EmbeddedTerminal needs an attached
        // visual tree with an applied template before StartAsync launches the PTY.
        if (!_shutdown)
        {
            EnsureTerminal();
        }

        RefreshConnectionFromTerminal();
    }

    /// <summary>Called by the page when it becomes active.</summary>
    public void OnPageActivated()
    {
        RefreshConnectionFromTerminal();
        if (!_shutdown)
        {
            RefreshAsync(false).Observe("agent-workspace", "refresh-on-activate");
        }
    }

    /// <summary>Called when the retained page is removed, before the terminal control is re-parented.</summary>
    public void OnPageClosed()
    {
        CloseApproved = false;
        if (IsRunning)
        {
            ConnectionStatusText = "后台运行中";
        }

        RaiseDerivedState();
    }

    /// <summary>
    /// Reconciles with the current library context and the current Agent settings. Detection uses the
    /// host cache, so re-activating the tab is cheap; the launch path still re-probes every candidate.
    /// </summary>
    public async Task RefreshAsync(bool forceDetection)
    {
        if (_shutdown || _isRefreshing || IsStarting)
        {
            return;
        }

        _isRefreshing = true;
        IsChecking = true;
        RaiseDerivedState();
        try
        {
            if (!_main.HasOpenRuntimeDatabase)
            {
                ApplyUnavailableState();
                return;
            }

            HostServices services = await _main.ServicesAsync();
            if (_services is not null && !ReferenceEquals(_services, services))
            {
                if (IsRunning)
                {
                    await StopSessionAsync("资料库已切换，上一 Agent 会话已停止。");
                }

                _allItems.Clear();
                FilteredItems.Clear();
                ItemPickerStatusText = "资料库已切换，题录需要重新加载。";
                _launchSpec = null;
                HasLaunched = false;
                McpStatusText = "";
            }

            _services = services;
            AgentIntegrationAppSettings settings = services.AgentSettings.Normalize();
            AgentCatalogEntry? entry = AgentCatalog.Find(settings.SelectedAgentId) ?? AgentCatalog.Default;
            if (entry is null)
            {
                ApplyFailure("没有可用的 Agent", "Agent 目录为空，无法启动任何 Agent。");
                return;
            }

            AgentDisplayName = entry.DisplayName;
            if (!settings.Enabled)
            {
                ApplyDisabledState();
                return;
            }

            AgentDetectionResult detection = await services.AgentDetection.DetectAsync(
                entry, settings, forceDetection);
            ApplyDetection(entry, detection);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplyFailure("无法检测 Agent", exception.Message);
        }
        finally
        {
            _isRefreshing = false;
            IsChecking = false;
            RaiseDerivedState();
        }
    }

    private async Task StartAsync()
    {
        if (_shutdown || IsRunning || IsStarting)
        {
            return;
        }

        if (!_main.HasOpenRuntimeDatabase)
        {
            ApplyUnavailableState();
            return;
        }

        IsStarting = true;
        ConnectionStatusText = "启动中…";
        EmptyStateTitle = $"正在启动 {AgentDisplayName}…";
        EmptyStateMessage = "正在验证可执行文件、创建 Agent 工作目录并准备终端环境。";
        RaiseDerivedState();
        try
        {
            HostServices services = await _main.ServicesAsync();
            _services = services;
            AgentIntegrationAppSettings settings = services.AgentSettings.Normalize();
            if (!settings.Enabled)
            {
                ApplyDisabledState();
                return;
            }

            AgentCatalogEntry? entry = AgentCatalog.Find(settings.SelectedAgentId) ?? AgentCatalog.Default;
            if (entry is null)
            {
                ApplyFailure("没有可用的 Agent", "Agent 目录为空，无法启动任何 Agent。");
                return;
            }

            AgentDisplayName = entry.DisplayName;
            AgentLaunchContext context = await CreateLaunchContextAsync(services);
            Result<AgentLaunchSpec> prepared = await services.AgentLauncher.PrepareLaunchAsync(
                entry.Id, settings, context);
            if (prepared.IsFailure)
            {
                if (string.Equals(prepared.ErrorCode, AgentIntegrationErrorCodes.NotInstalled,
                        StringComparison.Ordinal))
                {
                    ApplyMissingState(entry, null);
                }
                else if (string.Equals(prepared.ErrorCode, AgentIntegrationErrorCodes.Disabled,
                             StringComparison.Ordinal))
                {
                    ApplyDisabledState();
                }
                else
                {
                    ApplyFailure($"无法启动 {entry.DisplayName}", prepared.ErrorMessage ?? "启动准备失败。");
                }

                _main.ReportError(prepared.ErrorMessage ?? "无法启动 Agent。");
                return;
            }

            AgentLaunchSpec spec = prepared.Value;
            _launchSpec = spec;
            AgentVersionText = string.IsNullOrWhiteSpace(spec.Version) ? "版本未知" : spec.Version;
            TerminalSessionOptions options = CreateTerminalOptions(spec);
            EmbeddedTerminal terminal = EnsureTerminal();
            await _sessions.StartAsync(terminal, options);
            HasLaunched = true;
            IsRunning = terminal.IsRunning;
            ConnectionStatusText = IsRunning ? "已连接" : "启动后立即退出";
            McpStatusText = "";
            EmptyStateTitle = $"{spec.DisplayName} 已启动";
            EmptyStateMessage = "Agent 正在当前终端中运行。";
            _main.Report($"已启动 {spec.DisplayName}（终端进程 {terminal.Pid}）。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ApplyFailure($"无法启动 {AgentDisplayName}", exception.Message);
            _main.ReportError($"无法启动 Agent：{exception.Message}");
        }
        finally
        {
            IsStarting = false;
            RaiseDerivedState();
        }
    }

    private async Task StopAsync()
    {
        await StopSessionAsync("Agent 已停止。");
    }

    private async Task RestartAsync()
    {
        if (IsRunning)
        {
            bool stopped = await StopSessionAsync(null);
            if (!stopped)
            {
                return;
            }
        }

        await StartAsync();
    }

    private async Task<bool> StopSessionAsync(string? statusMessage)
    {
        if (_terminal is null)
        {
            IsRunning = false;
            RaiseDerivedState();
            return true;
        }

        _isStopping = true;
        RaiseDerivedState();
        try
        {
            bool stopped = await _sessions.StopAsync(_terminal);
            IsRunning = !stopped && _terminal.IsRunning;
            ConnectionStatusText = stopped ? "已停止" : "停止失败，进程仍在运行";
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                _main.Report(statusMessage);
            }

            return stopped;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConnectionStatusText = "停止失败";
            _main.ReportError($"无法停止 Agent：{exception.Message}");
            return false;
        }
        finally
        {
            _isStopping = false;
            RaiseDerivedState();
        }
    }

    private EmbeddedTerminal EnsureTerminal()
    {
        if (_terminal is not null)
        {
            return _terminal;
        }

        EmbeddedTerminal terminal = _sessions.CreateTerminal();
        terminal.Exited += OnTerminalExited;
        terminal.TerminalControl.FontSize = 13;
        terminal.TerminalControl.Background = new SolidColorBrush(Color.Parse("#101014"));
        terminal.TerminalControl.Foreground = new SolidColorBrush(Color.Parse("#E8E8EC"));
        _terminal = terminal;
        Terminal = terminal;
        Raise(nameof(Terminal));
        return terminal;
    }

    private void OnTerminalExited(object? sender, TerminalExitedEventArgs args)
    {
        if (_shutdown)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyExited(args);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyExited(args));
    }

    private void ApplyExited(TerminalExitedEventArgs args)
    {
        if (_shutdown)
        {
            return;
        }

        IsRunning = false;
        if (!_isStopping)
        {
            ConnectionStatusText = args.ExitCodeKnown
                ? $"已退出（代码 {args.ExitCode}）"
                : "已退出";
        }

        RaiseDerivedState();
    }

    private void RefreshConnectionFromTerminal()
    {
        if (_terminal is null)
        {
            return;
        }

        IsRunning = _terminal.IsRunning;
        if (IsRunning)
        {
            HasLaunched = true;
            ConnectionStatusText = "已连接";
        }

        RaiseDerivedState();
    }

    private async Task<AgentLaunchContext> CreateLaunchContextAsync(HostServices services)
    {
        string? libraryId = null;
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsSuccess)
        {
            libraryId = library.Value.LibraryId.ToString();
        }

        await ResolveMcpTokenAsync(services);
        Uri? endpoint = Uri.TryCreate(_main.McpEndpoint, UriKind.Absolute, out Uri? parsed) ? parsed : null;
        return new AgentLaunchContext(libraryId, endpoint, _mcpToken);
    }

    /// <summary>
    /// Reuses the issued MCP bearer token when the listener requires authentication, so the token
    /// exported to the Agent process and the one referenced by the generated config agree with the
    /// running server. Without authentication there is no issued token to reuse.
    /// </summary>
    private async Task ResolveMcpTokenAsync(HostServices services)
    {
        Result<McpServerSettings> configured = await services.McpSettings.GetSettingsAsync();
        if (configured.IsSuccess && configured.Value.AuthRequired &&
            !string.IsNullOrWhiteSpace(configured.Value.Token))
        {
            _mcpToken = configured.Value.Token.Trim();
            _mcpTokenFromSettings = true;
            return;
        }

        if (_mcpTokenFromSettings)
        {
            _mcpToken = CreatePlaceholderMcpToken();
            _mcpTokenFromSettings = false;
        }
    }

    private static string CreatePlaceholderMcpToken()
    {
        // Placeholder only for a listener without authentication: a generated config still references
        // PATCHOULI_MCP_TOKEN, and the random value keeps that environment variable well-formed. It
        // is never accepted when authentication is enabled; that case reuses the issued token above.
        return Guid.NewGuid().ToString("N");
    }

    private static TerminalSessionOptions CreateTerminalOptions(AgentLaunchSpec spec)
    {
        if (!spec.RequiresShell)
        {
            return new TerminalSessionOptions
            {
                Executable = spec.ExecutablePath,
                Arguments = spec.Arguments,
                WorkingDirectory = spec.WorkingDirectory,
                EnvironmentVariables = spec.Environment
            };
        }

        // Windows npm shims (.cmd/.bat) cannot be started by CreateProcess. Wrap the validated
        // command in the preferred interactive shell resolved by the terminal layer.
        ShellCommand shell = DefaultShellResolver.Resolve();
        List<string> arguments = [.. shell.Arguments];
        bool verbatim = false;
        if (IsPowerShell(shell.Executable))
        {
            arguments.Add("-Command");
            arguments.Add(BuildPowerShellCommand(spec));
        }
        else if (IsCommandProcessor(shell.Executable))
        {
            arguments.Add("/d");
            arguments.Add("/s");
            arguments.Add("/c");
            arguments.Add(BuildCommandProcessorCommand(spec));
            verbatim = true;
        }
        else
        {
            arguments.Add("-lc");
            arguments.Add(BuildPosixCommand(spec));
        }

        return new TerminalSessionOptions
        {
            Executable = shell.Executable,
            Arguments = arguments,
            WorkingDirectory = spec.WorkingDirectory,
            EnvironmentVariables = spec.Environment,
            VerbatimCommandLine = verbatim
        };
    }

    private static bool IsPowerShell(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandProcessor(string executable)
    {
        return Path.GetFileNameWithoutExtension(executable).Equals("cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPowerShellCommand(AgentLaunchSpec spec)
    {
        StringBuilder builder = new("& ");
        builder.Append(QuotePowerShell(spec.ExecutablePath));
        foreach (string argument in spec.Arguments)
        {
            builder.Append(' ').Append(QuotePowerShell(argument));
        }

        return builder.ToString();
    }

    private static string BuildPosixCommand(AgentLaunchSpec spec)
    {
        StringBuilder builder = new(QuotePosix(spec.ExecutablePath));
        foreach (string argument in spec.Arguments)
        {
            builder.Append(' ').Append(QuotePosix(argument));
        }

        return builder.ToString();
    }

    private static string BuildCommandProcessorCommand(AgentLaunchSpec spec)
    {
        StringBuilder builder = new("call ");
        builder.Append(QuoteCommandProcessor(spec.ExecutablePath));
        foreach (string argument in spec.Arguments)
        {
            builder.Append(' ').Append(QuoteCommandProcessor(argument));
        }

        return builder.ToString();
    }

    private static string QuotePowerShell(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string QuotePosix(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string QuoteCommandProcessor(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async Task OpenItemPickerAsync()
    {
        IsItemPickerOpen = true;
        ItemSearchText = "";
        if (_allItems.Count == 0)
        {
            await LoadItemsAsync();
        }
        else
        {
            FilterItems();
        }
    }

    private async Task RefreshItemsAsync()
    {
        await LoadItemsAsync();
    }

    public void CloseItemPicker()
    {
        IsItemPickerOpen = false;
    }

    public void MoveItemSelection(int delta)
    {
        if (FilteredItems.Count == 0)
        {
            return;
        }

        int index = SelectedItem is null ? -1 : FilteredItems.IndexOf(SelectedItem);
        index = index < 0
            ? delta >= 0 ? 0 : FilteredItems.Count - 1
            : Math.Clamp(index + delta, 0, FilteredItems.Count - 1);
        SelectedItem = FilteredItems[index];
    }

    public void CommitSelectedItem()
    {
        AgentItemReferenceViewModel? item = SelectedItem;
        if (item is null)
        {
            return;
        }

        string block = $"{item.Title}{Environment.NewLine}" +
                       $"Item ID: {item.ItemIdText}{Environment.NewLine}" +
                       $"规范 URI: {item.CanonicalUri}";
        string existing = InputText;
        string prefix = string.IsNullOrEmpty(existing)
            ? ""
            : existing.EndsWith('\n')
                ? existing + Environment.NewLine
                : existing + Environment.NewLine +
                  Environment.NewLine;
        InputText = prefix + block + Environment.NewLine;
        CloseItemPicker();
        _main.Report($"已插入题录引用「{item.Title}」；内容尚未发送。");
    }

    private async Task LoadItemsAsync()
    {
        if (_isLoadingItems)
        {
            return;
        }

        if (!_main.HasOpenRuntimeDatabase)
        {
            ItemPickerStatusText = "请先打开书库。";
            return;
        }

        _isLoadingItems = true;
        IsLoadingItems = true;
        ItemPickerStatusText = "正在加载题录…";
        _itemLoadCancellation?.Cancel();
        _itemLoadCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _itemLoadCancellation = cancellation;
        try
        {
            HostServices services = await _main.ServicesAsync();
            // Client-side filtering over one ListRowsAsync snapshot: SearchRowsAsync returns an
            // empty result for an empty query, while the picker must show the whole library before
            // the user types. Filtering locally keeps typing instant and needs no repeated DB reads.
            Result<IReadOnlyList<LibraryItemRow>> rows = await services.LibraryItems.ListRowsAsync(
                cancellationToken: cancellation.Token);
            if (cancellation.IsCancellationRequested || _shutdown)
            {
                return;
            }

            if (rows.IsFailure)
            {
                ItemPickerStatusText = $"无法读取题录：{rows.ErrorMessage}";
                return;
            }

            _allItems.Clear();
            _allItems.AddRange(rows.Value.Select(row => new AgentItemReferenceViewModel(row)));
            FilterItems();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ItemPickerStatusText = $"无法读取题录：{exception.Message}";
        }
        finally
        {
            _isLoadingItems = false;
            IsLoadingItems = false;
        }
    }

    private void FilterItems()
    {
        string query = ItemSearchText.Trim();
        FilteredItems.Clear();
        foreach (AgentItemReferenceViewModel item in _allItems)
        {
            if (query.Length == 0 ||
                item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredItems.Add(item);
            }
        }

        SelectedItem = FilteredItems.FirstOrDefault();
        ItemPickerStatusText = _allItems.Count == 0
            ? IsLoadingItems ? "正在加载题录…" : "书库中还没有题录。"
            : query.Length == 0
                ? $"共 {_allItems.Count} 条题录"
                : $"{FilteredItems.Count} / {_allItems.Count} 条匹配";
        RaiseDerivedState();
    }

    public void ApplyPreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        AgentPromptPreset? preset = PromptPresets.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.Ordinal));
        if (preset is null)
        {
            return;
        }

        InputText = preset.Template;
        _main.Report($"已填入预置提示词「{preset.Title}」；请预览或编辑后自行发送。");
    }

    private async Task CopyInputAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            _main.Report("输入组合框为空，没有可复制的内容。");
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(InputText);
            _main.Report("输入内容已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            _main.ReportError($"复制失败：{exception.Message}");
        }
    }

    private async Task InjectInputAsync()
    {
        if (_terminal is null || !_terminal.IsRunning)
        {
            _main.ReportError("Agent 尚未运行，无法注入输入。");
            return;
        }

        if (string.IsNullOrWhiteSpace(InputText))
        {
            _main.Report("输入组合框为空，没有可注入的内容。");
            return;
        }

        try
        {
            // No carriage return: the user reviews the text and presses Enter inside the terminal.
            await _terminal.WriteInputAsync(InputText);
            _main.Report("已把输入注入终端（未按下回车），请在终端中确认后自行回车。");
        }
        catch (Exception exception)
        {
            _main.ReportError($"注入终端失败：{exception.Message}");
        }
    }

    private async Task WriteMcpConfigAsync()
    {
        if (_services is null || _launchSpec is null)
        {
            _main.ReportError("请先成功启动 Agent，再写入 MCP 连接配置。");
            return;
        }

        AgentCatalogEntry? entry = AgentCatalog.Find(_launchSpec.AgentId);
        if (entry is null)
        {
            _main.ReportError("当前 Agent 不在目录中，无法生成 MCP 配置。");
            return;
        }

        if (!Uri.TryCreate(_main.McpEndpoint, UriKind.Absolute, out Uri? endpoint))
        {
            _main.ReportError("MCP 服务地址无效，无法生成连接配置。");
            return;
        }

        try
        {
            AgentMcpConfigDocument document = _services.AgentMcpConfig.Generate(
                entry, new AgentMcpConnection(endpoint, _mcpToken));
            string workspace = Path.GetFullPath(_services.AgentWorkspace.EnsureWorkspaceDirectory());
            string target = ResolveMcpConfigTarget(document, workspace, _launchSpec.McpConfigPath);
            string preview = document.ContentWithComments ?? document.Content;
            List<string> notes = [.. document.PermissionNotes];
            if (!_main.McpServerRunning)
            {
                notes.Add("MCP 服务当前未运行；请先在 AI集成菜单中启动 MCP 服务，Agent 才能连接。");
            }

            notes.Add("写入后需要重启 Agent（可用工具栏的重启按钮）才会加载此配置。");
            string environmentNote = _mcpTokenFromSettings
                ? $"PATCHOULI_MCP_TOKEN 使用 MCP 服务设置中已启用的 Bearer token（{MaskToken(_mcpToken)}），" +
                  "只在本次 Agent 进程环境中提供；token 值不写入配置文件。"
                : $"PATCHOULI_MCP_TOKEN 由 Patchouli 以随机 GUID 占位写入本次 Agent 进程环境（{MaskToken(_mcpToken)}）；" +
                  "当前 MCP 设置未启用鉴权，占位值不会被监听器校验。token 值不写入配置文件。";
            AgentMcpConfigDialogViewModel dialog = new(
                entry.DisplayName, document.RelativePath, preview, notes, environmentNote);
            bool confirmed = await _main.Dialogs.ShowDialogAsync<bool>(dialog);
            if (!confirmed)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(target, document.Content, new UTF8Encoding(false));
            McpStatusText = $"MCP 配置已写入 {document.FileName}；请重启 Agent 使其生效。";
            _main.Report(McpStatusText);
        }
        catch (Exception exception)
        {
            _main.ReportError($"写入 MCP 配置失败：{exception.Message}");
        }
    }

    private static string ResolveMcpConfigTarget(
        AgentMcpConfigDocument document,
        string workspace,
        string? launchSpecPath)
    {
        string workspacePrefix = workspace.TrimEnd(Path.DirectorySeparatorChar) +
                                 Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.IsNullOrWhiteSpace(launchSpecPath))
        {
            string preferred = Path.GetFullPath(launchSpecPath);
            if (preferred.StartsWith(workspacePrefix, comparison))
            {
                return preferred;
            }
        }

        string candidate = Path.GetFullPath(Path.Combine(workspace, document.RelativePath));
        return candidate.StartsWith(workspacePrefix, comparison)
            ? candidate
            : Path.Combine(workspace, document.FileName);
    }

    private static string MaskToken(string token)
    {
        return token.Length <= 8 ? "********" : $"{token[..4]}…{token[^4..]}";
    }

    private async Task OpenAgentSettingsAsync()
    {
        // D4 owns the settings section. Accept the likely ids so this page works whether the section
        // is registered as "agent", "agents" or "agent_integration", then fall back to the AI group.
        foreach (string id in new[] { "agent", "agents", "agent_integration" })
        {
            try
            {
                await _main.OpenSettingsAsync(id);
                return;
            }
            catch (ArgumentException)
            {
            }
        }

        await _main.OpenSettingsAsync("llm");
    }

    private void ApplyDisabledState()
    {
        _agentReady = false;
        IsIntegrationDisabled = true;
        IsAgentMissing = false;
        AgentVersionText = "未启用";
        ConnectionStatusText = "已停用";
        EmptyStateTitle = "Agent 集成已停用";
        EmptyStateMessage = "请在“设置 → Agent 集成”中启用集成并选择要使用的 Agent。此页面不会启动任何进程。";
        RaiseDerivedState();
    }

    private void ApplyUnavailableState()
    {
        _agentReady = false;
        IsIntegrationDisabled = false;
        IsAgentMissing = false;
        AgentVersionText = "未检测";
        ConnectionStatusText = "等待书库";
        EmptyStateTitle = "尚未打开书库";
        EmptyStateMessage = "Agent 集成需要一个已打开的书库来准备 Agent 工作目录和 MCP 连接。请先打开书库。";
        RaiseDerivedState();
    }

    private void ApplyDetection(AgentCatalogEntry entry, AgentDetectionResult detection)
    {
        AgentDisplayName = entry.DisplayName;
        if (detection.IsFound)
        {
            _agentReady = true;
            IsIntegrationDisabled = false;
            IsAgentMissing = false;
            AgentVersionText = string.IsNullOrWhiteSpace(detection.Version)
                ? "版本未知"
                : detection.Version!;
            EmptyStateTitle = $"{entry.DisplayName} 已就绪";
            EmptyStateMessage = "点击工具栏的启动按钮在终端中启动 Agent；也可以先编辑提示词或插入题录引用。";
            if (IsRunning)
            {
                ConnectionStatusText = "已连接";
            }
            else if (!HasLaunched)
            {
                ConnectionStatusText = "就绪";
            }

            return;
        }

        ApplyMissingState(entry, detection);
    }

    private void ApplyMissingState(AgentCatalogEntry entry, AgentDetectionResult? detection)
    {
        _agentReady = false;
        IsIntegrationDisabled = false;
        IsAgentMissing = true;
        bool notInstalled = detection is null ||
                            detection.Status is AgentDetectionStatus.NotInstalled or
                                AgentDetectionStatus.InvalidCustomPath;
        string state = notInstalled ? "未安装" : "不可用";
        AgentVersionText = state;
        ConnectionStatusText = state;
        EmptyStateTitle = $"{entry.DisplayName} {state}";
        string? detail = detection?.ErrorMessage;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = "没有检测到可用的可执行文件。";
        }

        string installHint = detection?.IsInstallable == true
            ? "设置页的 Agent 集成分区提供了安装选项；本页只做引导，不会自动下载或安装。"
            : "请先在设置页的 Agent 集成分区检查可执行文件路径，或安装对应 CLI。";
        EmptyStateMessage = $"{detail} {installHint}";
        RaiseDerivedState();
    }

    private void ApplyFailure(string title, string message)
    {
        _agentReady = false;
        IsIntegrationDisabled = false;
        IsAgentMissing = false;
        ConnectionStatusText = "不可用";
        EmptyStateTitle = title;
        EmptyStateMessage = message;
        RaiseDerivedState();
    }

    private void RaiseDerivedState()
    {
        Raise(nameof(IsAgentReady));
        Raise(nameof(IsBusy));
        Raise(nameof(ShowEmptyState));
        Raise(nameof(ShowTerminal));
        Raise(nameof(CanStart));
        Raise(nameof(CanStop));
        Raise(nameof(CanRestart));
        Raise(nameof(CanWriteMcpConfig));
        Raise(nameof(CanInjectInput));
        Raise(nameof(CanCopyInput));
        Raise(nameof(ShowSettingsShortcut));
        Raise(nameof(ShowRetryShortcut));
        Raise(nameof(ShowStartShortcut));
        Raise(nameof(HasActiveSession));
    }

    internal async Task<bool> ConfirmCloseAsync()
    {
        if (!HasActiveSession)
        {
            CloseApproved = true;
            return true;
        }

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "Agent 会话仍在运行",
                $"“{AgentDisplayName}” 仍在终端中运行。关闭标签页时要终止它，还是保留到后台继续运行？",
                "终止并关闭",
                "保留后台运行",
                true));
        if (choice == ConfirmDialogResult.Confirm)
        {
            bool stopped = await StopSessionAsync(null);
            CloseApproved = stopped;
            return stopped;
        }

        if (choice == ConfirmDialogResult.Discard)
        {
            CloseApproved = true;
            ConnectionStatusText = "后台运行中";
            _main.Report("Agent 会话已保留在后台运行；重新打开「Agent 集成」即可继续。");
            return true;
        }

        return false;
    }

    /// <summary>Stops the terminal session and disposes the PTY while the application shuts down.</summary>
    public async Task ShutdownAsync()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _itemLoadCancellation?.Cancel();
        _itemLoadCancellation?.Dispose();
        _itemLoadCancellation = null;
        if (_terminal is not null)
        {
            _terminal.Exited -= OnTerminalExited;
        }

        try
        {
            await _sessions.DisposeAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            _ = exception;
        }

        _terminal = null;
        Terminal = null;
        Raise(nameof(Terminal));
        IsRunning = false;
        HasLaunched = false;
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _terminal is not null)
        {
            _terminal.Exited -= OnTerminalExited;
        }

        base.Dispose(disposing);
    }
}
