using System;
using System.Threading;
using System.Threading.Tasks;
using Patchouli.Core.Diagnostics;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Core;
using Patchouli.Workflows;

namespace Patchouli.UI.ViewModels;

/// <summary>
///     What a workflow menu entry needs from the window it lives in: a status bar, the host activity
///     tracker its command is correlated under, and the chat tab the launch opens. Keeping this an
///     interface lets the menu entries be tested without a window.
/// </summary>
public interface IWorkflowMenuEntryHost
{
    /// <summary>Reports a non-fatal message in the status bar.</summary>
    void Report(string message);

    /// <summary>Reports a failure in the status bar.</summary>
    void ReportError(string message);

    /// <summary>Shows the chat tab; the launch opens it immediately after starting the session.</summary>
    Task OpenChatTabAsync(string? sessionId = null);

    /// <summary>The host activity tracker commands are constructed under, or null.</summary>
    IHostActivityTracker? ActivityTracker { get; }
}

/// <summary>
///     One workflow-name menu item (ADR 0036, plan §3.8). Every surface builds these
///     from the same ordered definition list, and this view model owns the one click behaviour: start
///     the session against the captured selection, then open (or refresh) the chat tab.
/// </summary>
public sealed class WorkflowMenuEntryViewModel
{
    private readonly IWorkflowMenuEntryHost _host;
    private readonly IWorkflowMenuService _menu;
    private readonly WorkflowLaunchSelection _selection;

    public WorkflowMenuEntryViewModel(WorkflowDefinition definition, IWorkflowMenuService menu,
        IWorkflowMenuEntryHost host, WorkflowLaunchSelection selection)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(selection);
        WorkflowId = definition.Id;
        Name = definition.Name;
        Header = definition.Name;
        Tooltip = string.IsNullOrWhiteSpace(definition.Description) ? Header : definition.Description;
        _menu = menu;
        _host = host;
        _selection = selection;
        LaunchCommand = new AsyncCommand(LaunchAsync, activityTracker: host.ActivityTracker);
    }

    /// <summary>The workflow definition id this item launches.</summary>
    public string WorkflowId { get; }

    /// <summary>The workflow display name.</summary>
    public string Name { get; }

    /// <summary>The menu item text: the workflow name.</summary>
    public string Header { get; }

    /// <summary>The definition description, shown as the item tooltip.</summary>
    public string Tooltip { get; }

    /// <summary>Starts the session and opens the chat tab.</summary>
    public AsyncCommand LaunchCommand { get; }

    /// <summary>The menu item is always clickable; a missing selection is reported, not silently ignored.</summary>
    public bool IsEnabled => true;

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_selection.DocumentId))
        {
            _host.Report("请先选择一个题录，再让内置 agent 执行工作流。");
            await _host.OpenChatTabAsync();
            return;
        }

        string? sessionId = null;
        try
        {
            sessionId = await _menu.StartAsync(WorkflowId, _selection, cancellationToken);
            _host.Report($"已启动工作流会话：{sessionId}。");
        }
        catch (Exception exception)
        {
            _host.ReportError($"启动工作流失败：{exception.Message}");
        }

        // The session exists (or the launch reported why it does not), so the chat tab always comes
        // forward: an already open tab refreshes its session list instead of being re-created.
        await _host.OpenChatTabAsync(sessionId);
    }
}
