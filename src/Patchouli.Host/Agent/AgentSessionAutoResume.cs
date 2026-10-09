using Patchouli.Core.Diagnostics;
using Patchouli.Host.Workflows;

namespace Patchouli.Host.Agent;

/// <summary>
///     The S5 startup auto-resume (ADR 0036 Resume segment): once the host finished opening the
///     Library, every session that is still recorded as <see cref="AgentSessionStatus.Running" /> or
///     <see cref="AgentSessionStatus.AwaitingEffect" /> continues through the
///     <see cref="WorkflowSessionRunner" /> resume path, which replays the recorded results instead of
///     re-invoking the model or the tools. Stopped, cancelled, finished and failed sessions are never
///     auto-started.
/// </summary>
/// <remarks>
///     The scan is deliberately defensive: a missing, corrupt or unreadable session directory is
///     skipped with a structured log line and never prevents the Library from opening, and one
///     session's resume failure never stops the remaining sessions from being scanned. The host runs
///     the whole loop in the background, so startup is never blocked by a resume.
/// </remarks>
public static class AgentSessionAutoResume
{
    /// <summary>The log operation every line of the startup resume scan is written under.</summary>
    public const string LogOperation = "session-resume";

    /// <summary>
    ///     Whether a persisted status means the session was still in flight when the host last
    ///     stopped and therefore must be auto-resumed. Every other status is a host decision
    ///     (stopped / cancelled) or a terminal outcome (finished / failed) and is never auto-started.
    /// </summary>
    public static bool ShouldAutoResume(AgentSessionStatus status)
    {
        return status is AgentSessionStatus.Running or AgentSessionStatus.AwaitingEffect;
    }

    /// <summary>
    ///     Scans every session directory and resumes the ones still recorded as in flight. Returns
    ///     the number of sessions resumed; sessions skipped (terminal status, unreadable directory,
    ///     resume failure) are logged, never thrown.
    /// </summary>
    /// <param name="sessions">The host session service owning the directories.</param>
    /// <param name="runner">The workflow runner whose resume path replays recorded results.</param>
    /// <param name="logger">The structured startup logger; failures degrade to a trace line.</param>
    /// <param name="cancellationToken">The host lifetime; cancellation aborts the remaining scan.</param>
    public static async Task<int> ResumeInterruptedAsync(AgentSessionService sessions, WorkflowSessionRunner runner,
        IAppLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyList<string> sessionIds;
        try
        {
            sessionIds = sessions.ListSessionIds();
        }
        catch (Exception exception)
        {
            await LogAsync(logger,
                    $"enumerating session directories failed; auto-resume skipped: {exception.GetBaseException().Message}")
                .ConfigureAwait(false);
            return 0;
        }

        int resumed = 0;
        int skipped = 0;
        foreach (string sessionId in sessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentSessionSnapshot? snapshot =
                await TryOpenLoggedAsync(sessions, sessionId, logger, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                skipped++;
                continue;
            }

            if (!ShouldAutoResume(snapshot.Status))
            {
                skipped++;
                continue;
            }

            try
            {
                if (sessions.IsChatSession(sessionId))
                {
                    await sessions.WakeChatAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await runner.ResumeAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }

                resumed++;
                await LogAsync(logger,
                        $"session={sessionId} auto-resumed from recorded results (persisted status " +
                        $"{snapshot.Status}).")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One session's failure (e.g. a workflow snapshot a manual repair broke) must not stop
                // the remaining sessions from resuming, and must not prevent the Library from opening.
                skipped++;
                await LogAsync(logger,
                        $"session={sessionId} auto-resume failed and was skipped: " +
                        exception.GetBaseException().Message)
                    .ConfigureAwait(false);
            }
        }

        await LogAsync(logger, $"startup auto-resume finished: resumed={resumed}, skipped={skipped}, " +
                               $"total={sessionIds.Count}.")
            .ConfigureAwait(false);
        return resumed;
    }

    private static async Task<AgentSessionSnapshot?> TryOpenLoggedAsync(AgentSessionService sessions, string sessionId,
        IAppLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            AgentSessionSnapshot? snapshot = await sessions.TryOpenAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                await LogAsync(logger,
                        $"session={sessionId} directory is missing or unreadable; skipped " +
                        "(the Library still opens).")
                    .ConfigureAwait(false);
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await LogAsync(logger,
                    $"session={sessionId} directory could not be opened; skipped: " +
                    exception.GetBaseException().Message)
                .ConfigureAwait(false);
            return null;
        }
    }

    private static async Task LogAsync(IAppLogger logger, string message)
    {
        try
        {
            await logger.LogAsync(LogOperation, message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Observability must never break the resume loop or the Library open.
            System.Diagnostics.Trace.WriteLine($"Patchouli {LogOperation} log failed: {exception}");
        }
    }
}
