using Patchouli.Core.Results;

namespace Patchouli.Host.Agent;

/// <summary>The outcome of one host OCR enqueue primitive call.</summary>
/// <param name="Accepted">True when a host OCR job was enqueued.</param>
/// <param name="Payload">The text fed back to the core as a tool result.</param>
public sealed record AgentOcrEnqueueOutcome(bool Accepted, string Payload);

/// <summary>
///     The host primitives an agent session may invoke: OCR enqueue, an armed wait for a host run
///     event (no polling) and progress reporting.
/// </summary>
/// <remarks>
///     <para>
///         <b>D7 (ADR 0036).</b> These are the only way a session reaches host work. An
///         implementation must enqueue through the OCR queue's own public contract and must never
///         share, rename or embed the OCR queue's queue, scheduler, execution state machine or
///         activity tracking: agent sessions and OCR runs are observed side by side in
///         <c>runs/</c> without unifying their states.
///     </para>
///     <para>
///         <b>S4 wakeup.</b> The real OCR wakeup — arming a wait that a completed OCR job satisfies —
///         is <see cref="OcrQueueHostPrimitives" />, the production default wired in
///         <c>HostServices</c>. <see cref="PlaceholderAgentHostPrimitives" /> remains the explicit,
///         non-silent "not armed" fallback for offline hosts and tests: it reports an explicit
///         outcome so a wait is never silently dropped, and it enqueues nothing into the OCR queue.
///     </para>
/// </remarks>
public interface IAgentHostPrimitives
{
    /// <summary>Enqueues one OCR job for a document page range.</summary>
    Task<AgentOcrEnqueueOutcome> EnqueueOcrAsync(string documentId, string pageRange,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Arms a wait for one host run event and completes when the host satisfies or cancels it.
    ///     Failure means the wait could not be armed; the caller must feed that back as a
    ///     <c>RunEvent</c> rather than leaving the wait dangling.
    /// </summary>
    Task<Result<string>> WaitRunEventAsync(string runUri, long waitId, CancellationToken cancellationToken);

    /// <summary>Reports run progress to the host status surface.</summary>
    Task ReportProgressAsync(string message, CancellationToken cancellationToken);
}

/// <summary>
///     The S1-S3 default host primitives: no OCR enqueue, no armed wait, no host-side progress
///     side effect. Every call still produces an explicit result, so the session never stalls on a
///     silently dropped wait.
/// </summary>
public sealed class PlaceholderAgentHostPrimitives : IAgentHostPrimitives
{
    /// <summary>Stable code reported when a wait cannot be armed yet.</summary>
    public const string WaitUnavailableCode = "HOST_WAIT_UNAVAILABLE";

    /// <inheritdoc />
    public Task<AgentOcrEnqueueOutcome> EnqueueOcrAsync(string documentId, string pageRange,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new AgentOcrEnqueueOutcome(false,
            $"OCR enqueue is not wired to the host queue yet (S4); requested document '{documentId}' " +
            $"range '{pageRange}'."));
    }

    /// <inheritdoc />
    public Task<Result<string>> WaitRunEventAsync(string runUri, long waitId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Result<string>.Failure(WaitUnavailableCode,
            $"The host run-event wakeup for '{runUri}' (wait {waitId}) is wired in S4; " +
            "the wait was not armed."));
    }

    /// <inheritdoc />
    public Task ReportProgressAsync(string message, CancellationToken cancellationToken)
    {
        // Progress is recorded in the session event sequence (and reported through
        // IHostActivityTracker by the session service), never through library_revision.
        return Task.CompletedTask;
    }
}
