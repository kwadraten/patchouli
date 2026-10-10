using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Patchouli.Core.Diagnostics;
using Patchouli.Host.Agent;
using Patchouli.Host.Composition;
using Patchouli.Host.Workflows;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.UI.Services;

/// <summary>One workflow the user can launch from a menu, already ordered for display.</summary>
/// <remarks>
///     The menus (main menu bar, Library context menu and the reading toolbar) read this one ordered
///     list, so they can never disagree about which workflows are offered or in which order
///     (ADR 0036, plan §3.8).
/// </remarks>
public interface IWorkflowMenuService
{
    /// <summary>
    ///     Definitions with <see cref="WorkflowMenuPlacement.ShowInMenu" />, ordered by
    ///     <see cref="WorkflowMenuPlacement.Order" /> and then by name.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListMenuDefinitionsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Starts one session of <paramref name="workflowId" /> against <paramref name="selection" />.</summary>
    Task<string> StartAsync(string workflowId, WorkflowLaunchSelection selection,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The selection a menu launch carries. A Library launch names whole documents; a reading-view
///     launch names the document that is open plus the page range the reading toolbar offers.
/// </summary>
/// <param name="DocumentId">The selected document id, empty when nothing is selected.</param>
/// <param name="PageRange">The page range text, empty for a whole document.</param>
public sealed record WorkflowLaunchSelection(string DocumentId, string PageRange = "")
{
    /// <summary>Every document selected in the Library, retained alongside the legacy primary id.</summary>
    public IReadOnlyList<string> DocumentIds { get; init; } = string.IsNullOrWhiteSpace(DocumentId)
        ? Array.Empty<string>()
        : [DocumentId];

    /// <summary>Text selected in the reading view, empty when the UI cannot provide one.</summary>
    public string TextSelection { get; init; } = "";

    /// <summary>A selection that carries no document (the launch reports the missing selection).</summary>
    public static WorkflowLaunchSelection None { get; } = new(string.Empty);
}

/// <summary>One user-visible session outcome for the status bar.</summary>
/// <param name="SessionId">The session the outcome belongs to.</param>
/// <param name="Message">The status-bar message.</param>
/// <param name="IsError">True for a failed or otherwise unsuccessful session.</param>
public sealed record WorkflowSessionNotification(string SessionId, string Message, bool IsError);

/// <summary>
///     Reads one session's authoritative host status by id. It is the seam the status bar uses when a
///     session's activity scope closes: activity details are throttled, so the terminal status comes
///     from the host session service instead of a status line that may never be delivered.
/// </summary>
public interface IWorkflowSessionStatusLookup
{
    /// <summary>The session's current host snapshot, or null when it is not loaded.</summary>
    AgentSessionSnapshot? TryGetStatus(string sessionId);
}

/// <summary>
///     Resolves the session-outcome notifications a host activity snapshot implies. The agent session
///     service reports its sessions through <see cref="IHostActivityTracker" /> with
///     <see cref="HostActivityKind.Mcp" /> and the session id as the correlation id (S1), so the
///     tracker is the existing seam: a session completes or fails when its scope leaves a snapshot.
/// </summary>
public interface IWorkflowSessionNotifications
{
    /// <summary>The outcomes implied by one snapshot, each reported at most once per session.</summary>
    IReadOnlyList<WorkflowSessionNotification> ReadNotifications(HostActivitySnapshot snapshot,
        Func<string, AgentSessionSnapshot?> statusLookup);

    /// <summary>Observes the tracker; the returned subscription is disposed with the window.</summary>
    IDisposable Subscribe(IHostActivityTracker tracker, Func<string, AgentSessionSnapshot?> statusLookup,
        Action<IReadOnlyList<WorkflowSessionNotification>> onNotifications);
}

/// <summary>
///     The single source of the workflow menus and the status-bar session notifications
///     (ADR 0036, plan §3.8).
/// </summary>
/// <remarks>
///     <para>
///         <b>Menus.</b> <see cref="ListMenuDefinitionsAsync" /> is the one place the
///         <c>ShowInMenu</c> definitions are selected and ordered; every surface renders the same
///         list, and the launch path is shared, so the Library context menu and the menu bar are
///         reading the same <see cref="WorkflowStore" /> definitions by construction.
///     </para>
///     <para>
///         <b>Notifications.</b> The service never polls a session: it watches the host activity
///         tracker, which is where <c>AgentSessionService</c> already reports a session's terminal
///         status before releasing its scope (S1). A session whose scope vanished with a terminal
///         detail is reported as finished or failed; one that vanished without a recognised terminal
///         detail (a cancelled in-flight effect, a released window) is reported as unsuccessful.
///     </para>
/// </remarks>
public sealed class WorkflowMenuService : IWorkflowMenuService, IWorkflowSessionNotifications
{
    private const string SessionScopePrefix = "Agent session ";

    private readonly Func<CancellationToken, Task<WorkflowSessionRunner?>> _runnerProvider;
    private readonly IReadOnlyList<WorkflowDefinition>? _definitions;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _tracked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    public WorkflowMenuService(Func<CancellationToken, Task<WorkflowSessionRunner?>> runnerProvider,
        IReadOnlyList<WorkflowDefinition>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(runnerProvider);
        _runnerProvider = runnerProvider;
        _definitions = definitions;
    }

    /// <summary>The menu source over the workflows of the library the composition root opened.</summary>
    public static IWorkflowMenuService ForHost(Func<CancellationToken, Task<HostServices>> servicesProvider)
    {
        ArgumentNullException.ThrowIfNull(servicesProvider);
        return new WorkflowMenuService(async cancellationToken =>
            (await servicesProvider(cancellationToken).ConfigureAwait(false)).HostWorkflows);
    }

    /// <summary>The menu source over an already loaded definition list, without a runner.</summary>
    public static IWorkflowMenuService ForDefinitions(IReadOnlyList<WorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new WorkflowMenuService(_ => Task.FromResult<WorkflowSessionRunner?>(null), definitions);
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> ListMenuDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_definitions is { } definitions)
        {
            return MenuEntries(definitions);
        }

        WorkflowSessionRunner runner = await ResolveRunnerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WorkflowDefinition> loaded =
            await runner.Configuration.DiscoverDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        return MenuEntries(loaded);
    }

    public async Task<string> StartAsync(string workflowId, WorkflowLaunchSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(selection);
        WorkflowSessionRunner runner = await ResolveRunnerAsync(cancellationToken).ConfigureAwait(false);
        WorkflowSessionRequest request = CreateLaunchRequest(workflowId, selection);
        WorkflowSessionResult result = await runner.StartAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Session.SessionId;
    }

    public IReadOnlyList<WorkflowSessionNotification> ReadNotifications(HostActivitySnapshot snapshot,
        Func<string, AgentSessionSnapshot?> statusLookup)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(statusLookup);
        List<WorkflowSessionNotification> notifications = [];
        List<string> vanished = [];
        HashSet<string> active = new(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (HostActivityItem item in snapshot.Items)
            {
                if (SessionIdOf(item.Name) is not { } sessionId)
                {
                    continue;
                }

                active.Add(sessionId);
                _tracked.Add(sessionId);
                _notified.Remove(sessionId);
            }

            foreach (string sessionId in _tracked.Where(id => !active.Contains(id)))
            {
                vanished.Add(sessionId);
            }

            foreach (string sessionId in vanished)
            {
                _tracked.Remove(sessionId);
            }
        }

        // The host session service is the authority on the outcome: the activity scope closes at the
        // same moment its detail line becomes terminal, and that line is throttled, so reading the
        // status here is what makes the notification reliable for a fast run.
        foreach (string sessionId in vanished)
        {
            if (!_notified.Add(sessionId))
            {
                continue;
            }

            notifications.Add(Describe(sessionId, statusLookup(sessionId)));
        }

        return notifications;
    }

    public IDisposable Subscribe(IHostActivityTracker tracker, Func<string, AgentSessionSnapshot?> statusLookup,
        Action<IReadOnlyList<WorkflowSessionNotification>> onNotifications)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(statusLookup);
        ArgumentNullException.ThrowIfNull(onNotifications);
        return tracker.SnapshotStream.Subscribe(snapshot =>
        {
            IReadOnlyList<WorkflowSessionNotification> notifications =
                ReadNotifications(snapshot, statusLookup);
            if (notifications.Count > 0)
            {
                onNotifications(notifications);
            }
        });
    }

    /// <summary>Selects and orders the definitions the menus show, from any loaded definition list.</summary>
    public static IReadOnlyList<WorkflowDefinition> MenuEntries(IReadOnlyList<WorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return definitions
            .Where(definition => definition.Menu.ShowInMenu)
            .OrderBy(definition => definition.Menu.Order)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ThenBy(definition => definition.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Builds a launch with runtime context kept separate from user-supplied parameters.</summary>
    internal static WorkflowSessionRequest CreateLaunchRequest(string workflowId, WorkflowLaunchSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new WorkflowSessionRequest(workflowId,
            new Dictionary<string, string>(StringComparer.Ordinal), SelectionOf(selection));
    }

    /// <summary>The session id of a host activity item, or null when the item is not an agent session.</summary>
    public static string? SessionIdOf(string? activityName)
    {
        return activityName is not null && activityName.StartsWith(SessionScopePrefix, StringComparison.Ordinal) &&
               activityName.Length > SessionScopePrefix.Length
            ? activityName[SessionScopePrefix.Length..]
            : null;
    }

    private static WorkflowSelection SelectionOf(WorkflowLaunchSelection selection)
    {
        return new WorkflowSelection(
            selection.DocumentIds.ToArray(),
            selection.PageRange,
            selection.TextSelection);
    }

    private static WorkflowSessionNotification Describe(string sessionId, AgentSessionSnapshot? status)
    {
        bool unsuccessful = status is null || status.Status
            is AgentSessionStatus.Failed or AgentSessionStatus.Cancelled or AgentSessionStatus.Stopped;
        string outcome = status?.Status switch
        {
            AgentSessionStatus.Finished => "已完成",
            AgentSessionStatus.Failed => "失败",
            AgentSessionStatus.Cancelled => "已取消",
            AgentSessionStatus.Stopped => "已停止",
            _ => "已结束"
        };
        string detail = status?.Detail ?? string.Empty;
        string suffix = string.IsNullOrWhiteSpace(detail) ? "。" : $"：{detail}。";
        return new WorkflowSessionNotification(sessionId,
            $"内置 agent 会话 {sessionId} {outcome}{suffix}", unsuccessful);
    }

    private async Task<WorkflowSessionRunner> ResolveRunnerAsync(CancellationToken cancellationToken)
    {
        WorkflowSessionRunner? runner = await _runnerProvider(cancellationToken).ConfigureAwait(false);
        if (runner is null)
        {
            throw new InvalidOperationException("工作流服务尚未就绪：请先打开一个书库。");
        }

        return runner;
    }
}
