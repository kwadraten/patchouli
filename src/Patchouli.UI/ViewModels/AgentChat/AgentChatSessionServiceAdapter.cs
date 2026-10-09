using Patchouli.Host.Agent;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels.AgentChat;

/// <summary>
///     Adapts the host's <see cref="AgentSessionService" /> to the seam the chat tab uses and to the
///     status lookup the workflow status-bar notifications read. The adapter is deliberately thin: it
///     forwards the calls those two seams need and adds no policy of its own, so the host service
///     stays the single owner of session lifecycles (ADR 0036).
/// </summary>
public sealed class AgentChatSessionServiceAdapter : IAgentChatSessionService, IWorkflowSessionStatusLookup
{
    private readonly AgentSessionService _sessions;

    /// <summary>Wraps one host session service.</summary>
    public AgentChatSessionServiceAdapter(AgentSessionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <inheritdoc />
    public AgentSessionSnapshot? TryGetStatus(string sessionId)
    {
        return _sessions.TryGetSnapshot(sessionId);
    }

    /// <inheritdoc />
    public Task<AgentSessionSnapshot> CreateChatAsync(string text, CancellationToken cancellationToken = default)
    {
        return _sessions.CreateChatAsync(text, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentSessionSnapshot>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        return _sessions.ListSessionsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSessionSnapshot?> TryOpenAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _sessions.TryOpenAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentSessionLogEntry>> ReadEventLogAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        return _sessions.ReadEventLogAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentMessageSendResult> SendAsync(string sessionId, AgentInboxMessage message,
        CancellationToken cancellationToken = default)
    {
        return _sessions.SendAsync(sessionId, message, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSessionSnapshot> StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _sessions.StopAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSessionSnapshot> ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _sessions.ResumeAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> PurgeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _sessions.PurgeAsync(sessionId, cancellationToken);
    }
}
