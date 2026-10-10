using Microsoft.FSharp.Collections;
using System.Text.Json;
using Patchouli.Agent;
using Patchouli.Core.Diagnostics;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Host.Agent;

/// <summary> The host session service: it owns agent run lifecycles — creation, inbox, Event-boundary advancement, stop, cancel and physical purge (ADR 0036). A session is a first-class run, not a task-queue job, and it shares nothing with the OCR queue's queue, scheduler or execution state machine (D7). </summary>
/// <remarks>
///     <para>
///         <b>Boundaries.</b> <see cref = "AdvanceAsync(string, CancellationToken)"/> performs one
///         Event boundary: pending inbox messages are folded into history in recorded order, the
///         pure <c>step</c> decides, the returned effects are executed through
///         <see cref = "IAgentEffectInterpreter"/> and their results are fed back as the next
///         <c>Event</c>. One advance runs at most
///         <see cref = "MaxBoundariesPerAdvance"/> boundaries so a model that never stops cannot spin
///         the host.
///     </para>
///     <para>
///         <b>Cancellation.</b> <see cref = "CancelAsync"/> applies the core's <c>Cancel</c> control
///         event immediately — it never waits for the next model inference — and cancels the
///         in-flight effect. It never bypasses an atomic commit point: a started <c>put</c> is
///         executed without a cancellation token and its commit or rollback is still recorded.
///     </para>
///     <para>
///         <b>Persistence.</b> Each session owns one directory under the Library's auxiliary data
///         with launch parameters, the current <c>Context</c>, the observable state and an
///         append-only event log with monotonically increasing sequence numbers. Session sequence
///         numbers are never <c>library_revision</c>.
///     </para>
/// </remarks>
public sealed partial class AgentSessionService : IAsyncDisposable
{
    /// <summary> Largest number of Event boundaries one <see cref = "AdvanceAsync(string, CancellationToken)"/> call will run. It bounds a hostile or looping model: the caller always regains control and can stop, cancel or send a message. </summary>
    public const int MaxBoundariesPerAdvance = 32;

    /// <summary>Built-in interactive chat task; it has no workflow script to replay.</summary>
    public const string ChatWorkflowUri = "patchouli://agent/chat";

    private readonly AgentSessionStore _store;
    private readonly IAgentEffectInterpreter _interpreter;
    private readonly IHostActivityTracker? _activity;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _chatDrivers = [];
    private int _disposed;
    private readonly AgentFsiRepl? _fsi;
    private readonly AgentWorkspaceStore _workspaces;
    private readonly Func<int>? _maxRequestRetries;

    public int MaxRequestRetries =>
        Math.Clamp(_maxRequestRetries?.Invoke() ?? Llm.LlmAppSettings.DefaultAgentMaxRetries, 0,
            Llm.LlmAppSettings.MaxAgentMaxRetries);

    /// <summary>Creates the service over a session store and an effect interpreter.</summary>
    public AgentSessionService(AgentSessionStore store, IAgentEffectInterpreter interpreter,
        IHostActivityTracker? activityTracker = null, TimeProvider? timeProvider = null, AgentFsiRepl? fsi = null,
        AgentWorkspaceStore? workspaces = null, Func<int>? maxRequestRetries = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(interpreter);
        _maxRequestRetries = maxRequestRetries;
        _store = store;
        _interpreter = interpreter;
        _activity = activityTracker;
        _time = timeProvider ?? TimeProvider.System;
        _fsi = fsi;
        _workspaces = workspaces ?? fsi?.Workspaces ?? new AgentWorkspaceStore(store);
    }

    /// <summary>The sessions root this service persists into (it may not exist yet).</summary>
    public string SessionsRoot => _store.SessionsRoot;

    public string WorkspacesRoot => _workspaces.Root;

    public string GetWorkingDirectory(string sessionId)
    {
        return _workspaces.Ensure(sessionId);
    }

    public string EnsureWorkspacesRoot()
    {
        return _workspaces.EnsureRoot();
    }

    public Task ClearWorkingDirectoriesAsync()
    {
        if (_fsi is not null)
        {
            return _fsi.ClearWorkingDirectoriesAsync();
        }

        return Task.Run(_workspaces.Clear);
    }

    /// <summary>Creates a durable conversation and starts its first reply on the host lifetime.</summary>
    public async Task<AgentSessionSnapshot> CreateChatAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        AgentSessionLaunchParameters launch = AgentSessionLaunchParameters.Create(ChatWorkflowUri,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["prompt"] = text.Trim() },
            launchedAt: _time.GetUtcNow(),
            instructionPrefix: "You are Patchouli, a helpful research assistant. Reply in the user's language. " +
                               "Be clear about what you know and do not invent library contents or claim to have " +
                               "performed actions without tool results. " +
                               "Use native function calls for tools, separate from your assistant text. " +
                               "fsi is a persistent, session-local F# Interactive REPL with full .NET, filesystem, " +
                               "network and process execution access, available automatically in every session. " +
                               "Its workingDirectory binding and default current directory point to this session's " +
                               "OS temporary workspace. Keep scratch files there; they may be cleared in Local File " +
                               "Management and are deleted when the session is deleted. " +
                               "Bindings survive calls while the REPL worker is live and reset when it restarts. " +
                               "Use printfn to print results; await tool results before making claims. " +
                               "An ordinary text response completes the turn.");
        AgentSessionSnapshot snapshot = await CreateAsync(launch, cancellationToken).ConfigureAwait(false);
        ScheduleChat(snapshot.SessionId);
        return snapshot;
    }

    /// <summary>Whether the loaded session is a conversation created from the chat page.</summary>
    public bool IsChatSession(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out AgentSession? session) && session.IsChat;
        }
    }

    private void ScheduleChat(string sessionId)
    {
        Task driver;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            CancellationToken lifetime = _lifetime.Token;
            driver = Task.Run(() => WakeChatAsync(sessionId, lifetime), lifetime);
            _chatDrivers.Add(driver);
        }

        _ = driver.ContinueWith(completed =>
        {
            lock (_gate)
            {
                _chatDrivers.Remove(completed);
            }

            if (completed.Exception is { } exception)
            {
                UnexpectedExceptionReporter.ReportCatch(exception.GetBaseException(), "host.agent", "chat-driver");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Drains queued chat turns without regenerating replies already recorded on disk.</summary>
    public async Task WakeChatAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Driver.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if ((session.IsTerminal || session.Status == AgentSessionStatus.Idle) && session.Inbox.Count > 0)
                {
                    session.PendingEffects.Clear();
                    session.PendingEvents.Clear();
                    await ApplyStatusCoreAsync(session, AgentSessionStatus.Running, "continuing queued conversation",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                session.Gate.Release();
            }

            while (!session.IsTerminal)
            {
                Event? next;
                await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    next = DequeuePendingEvent(session);
                    if (next is null && session.Started)
                    {
                        next = session.Inbox.FirstOrDefault() is { } message
                            ? Event.NewUserMessage(message.MessageId, message.Text)
                            : session.Status == AgentSessionStatus.AwaitingEffect
                                ? Event.Resume
                                : null;
                        if (next is null)
                        {
                            return;
                        }
                    }
                }
                finally
                {
                    session.Gate.Release();
                }

                await DriveAsync(session, next, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Driver.Release();
        }
    }

    /// <summary>Session ids present on disk, in ordinal order; empty when the root is missing.</summary>
    public IReadOnlyList<string> ListSessionIds()
    {
        return _store.ListSessionIds();
    }

    /// <summary>True when the session is loaded in this process.</summary>
    public bool IsOpen(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(sessionId);
        }
    }

    /// <summary> Creates a session directory, records its launch parameters and returns its initial state. Nothing runs until the first boundary is advanced. </summary>
    public async Task<AgentSessionSnapshot> CreateAsync(AgentSessionLaunchParameters launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        Context initial = AgentCoreModule.withRetryLimit(MaxRequestRetries, AgentCoreModule.initial);

        _workspaces.Ensure(launch.SessionId);
        await _store.CreateSessionAsync(launch, cancellationToken).ConfigureAwait(false);
        AgentSession session = new(launch, _lifetime.Token)
        {
            // The head is part of the deterministic prefix: a caller that supplies one (a .fsx
            // workflow session supplies its script contract) keeps it byte-stable for the whole run.
            Instructions = InstructionPrefixOf(launch),
            Context = initial
        };
        session.Status = AgentSessionStatus.Running;
        session.Detail = "created";
        lock (_gate)
        {
            _sessions[launch.SessionId] = session;
        }

        await AppendLogAsync(session, AgentLogKinds.Launch, AgentSessionCodec.ToJson(launch), cancellationToken)
            .ConfigureAwait(false);
        await PersistAsync(session, cancellationToken).ConfigureAwait(false);
        Report(session);
        return Snapshot(session);
    }

    /// <summary> Loads a session from its own directory. A missing directory, a missing snapshot or an unreadable file yields <c>null</c> instead of throwing, so a Library always opens (ADR 0036). </summary>
    public async Task<AgentSessionSnapshot?> TryOpenAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out AgentSession? open))
            {
                return Snapshot(open);
            }
        }

        if (!_store.Exists(sessionId))
        {
            return null;
        }

        AgentSessionLaunchParameters? launch =
            await _store.TryReadLaunchAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (launch is null)
        {
            return null;
        }

        _workspaces.Ensure(sessionId);
        Context context = await _store.TryReadContextAsync(sessionId, cancellationToken).ConfigureAwait(false) ??
                          AgentCoreModule.initial;
        AgentSessionStateDocument? state =
            await _store.TryReadStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        AgentSession session = new(launch, _lifetime.Token)
        {
            Context = context,
            Started = !string.Equals(launch.WorkflowUri, ChatWorkflowUri, StringComparison.Ordinal) ||
                      context.ProcessedMessageIds.Contains("launch:" + sessionId)
        };
        // A session that persisted its instruction head keeps it; a legacy session without one falls
        // back to the head derived from its launch parameters (byte-stable either way).
        session.Instructions = state?.Instructions ?? InstructionPrefixOf(launch);
        if (state is not null)
        {
            session.Status = state.Snapshot.Status;
            session.Detail = state.Snapshot.Detail;
            session.Inbox.AddRange(state.PendingInbox);
            session.PendingEvents.AddRange(state.PendingEvents);
        }
        else
        {
            session.Status = AgentSessionCodec.FromCoreStatus(context.Status);
        }

        lock (_gate)
        {
            _sessions[sessionId] = session;
        }

        Report(session);
        return Snapshot(session);
    }

    /// <summary>The current state of a loaded session, or null when it is not loaded.</summary>
    public AgentSessionSnapshot? TryGetSnapshot(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out AgentSession? session) ? Snapshot(session) : null;
        }
    }

    /// <summary>Snapshots of every session on disk that can be read; unreadable sessions are skipped.</summary>
    public async Task<IReadOnlyList<AgentSessionSnapshot>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        List<AgentSessionSnapshot> snapshots = [];
        foreach (string sessionId in _store.ListSessionIds())
        {
            AgentSessionSnapshot? open = TryGetSnapshot(sessionId);
            if (open is not null)
            {
                snapshots.Add(open);
                continue;
            }

            AgentSessionStateDocument? state =
                await _store.TryReadStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                snapshots.Add(state.Snapshot);
            }
        }

        return snapshots;
    }

    /// <summary> Enqueues one message into the inbox, deduplicating by message id. Messages are folded into history at the next Event boundary in recorded order; they never interrupt the in-flight call and never wait for the whole workflow to finish. </summary>
    public async Task<AgentMessageSendResult> SendAsync(string sessionId, AgentInboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Context.ProcessedMessageIds.Contains(message.MessageId) || session.Inbox.Any(pending =>
                    string.Equals(pending.MessageId, message.MessageId, StringComparison.Ordinal)))
            {
                return new AgentMessageSendResult(true, true, session.SessionId, message.MessageId,
                    session.Context.EventSeq);
            }

            session.Inbox.Add(message);
            if (session.IsTerminal || session.Status == AgentSessionStatus.Idle)
            {
                // An automated prompt ending does not close its conversation. A new user turn
                // supersedes unexecuted effects while retaining all messages and completed results.
                session.PendingEffects.Clear();
                session.PendingEvents.Clear();
                await ApplyStatusCoreAsync(session, AgentSessionStatus.Running, "continuing conversation",
                    cancellationToken).ConfigureAwait(false);
            }

            session.Detail = $"message {message.MessageId} accepted";
            await AppendLogAsync(session, AgentLogKinds.Inbox, AgentSessionCodec.ToJson(message), cancellationToken)
                .ConfigureAwait(false);
            await PersistStateAsync(session, cancellationToken).ConfigureAwait(false);
            Report(session);
            return new AgentMessageSendResult(true, false, session.SessionId, message.MessageId,
                session.Context.EventSeq);
        }
        finally
        {
            session.Gate.Release();
            if (!session.IsTerminal)
            {
                ScheduleChat(sessionId);
            }
        }
    }

    /// <summary> Advances the run by up to <see cref = "MaxBoundariesPerAdvance"/> Event boundaries, starting from the next queued effect result (or the launch message for a session that has not started). Returns immediately when there is nothing to do. </summary>
    public async Task<AgentSessionSnapshot> AdvanceAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Driver.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DriveAsync(session, DequeuePendingEvent(session), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Driver.Release();
        }
    }

    /// <summary> Advances one boundary driven by an explicit event (a user message, a run event, a script progress report, resume, or cancel), then executes and feeds back its effects. </summary>
    public async Task<AgentSessionSnapshot> AdvanceAsync(string sessionId, Event evt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Driver.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DriveAsync(session, evt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Driver.Release();
        }
    }

    /// <summary> Applies the core's explicit <c>Cancel</c> control event immediately, without waiting for the next model inference, and cancels the in-flight effect. A <c>put</c> that already entered its atomic commit point is not bypassed: its commit or rollback is still recorded at the next boundary. </summary>
    public async Task<AgentSessionSnapshot> CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Status is AgentSessionStatus.Finished or AgentSessionStatus.Failed)
            {
                return Snapshot(session);
            }

            await ApplyEventAsync(session, Event.Cancel, cancellationToken).ConfigureAwait(false);
            session.Detail = "cancel applied immediately";
        }
        finally
        {
            session.Gate.Release();
        }

        // Immediate: the in-flight call is cancelled now, not at the next model inference.
        session.CancelInFlight();
        Report(session);
        return Snapshot(session);
    }

    /// <summary> Stops a run at the boundary: the host marks it stopped (resumable) and cancels the in-flight effect. Stop is an explicit control decision and is not undone by a queued message. </summary>
    public async Task<AgentSessionSnapshot> StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        session.CancelInFlight();
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Status is AgentSessionStatus.Finished or AgentSessionStatus.Failed)
            {
                return Snapshot(session);
            }

            await ApplyStatusCoreAsync(session, AgentSessionStatus.Stopped, "stopped by the host", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }

        return Snapshot(session);
    }

    /// <summary>Resumes a stopped or cancelled run from its recorded results and drives one cycle.</summary>
    public async Task<AgentSessionSnapshot> ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Driver.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Status is AgentSessionStatus.Finished)
            {
                return Snapshot(session);
            }

            return await DriveAsync(session, Event.Resume, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Driver.Release();
        }
    }

    /// <summary> Physically purges one session: it cancels any in-flight work and deletes exactly that session's directory. Items, original documents, translations, OCR results and the OCR queue are never touched (ADR 0036 D7). </summary>
    public async Task<bool> PurgeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _store.ResolveSessionDirectory(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        AgentSession? session;
        lock (_gate)
        {
            _sessions.TryGetValue(sessionId, out session);
            if (session is not null)
            {
                session.IsPurging = true;
            }
        }

        session?.CancelInFlight();
        try
        {
            if (_fsi is not null)
            {
                await _fsi.ForgetAsync(sessionId).ConfigureAwait(false);
            }

            if (session is not null)
            {
                await session.Driver.WaitAsync().ConfigureAwait(false);
            }

            try
            {
                if (session is not null)
                {
                    await session.Gate.WaitAsync().ConfigureAwait(false);
                }

                try
                {
                    bool workspaceDeleted = await Task.Run(() => _workspaces.Delete(sessionId)).ConfigureAwait(false);
                    bool journalDeleted = _store.Purge(sessionId);
                    lock (_gate)
                    {
                        _sessions.Remove(sessionId);
                    }

                    return journalDeleted || workspaceDeleted;
                }
                finally
                {
                    session?.Gate.Release();
                }
            }
            finally
            {
                session?.Driver.Release();
            }
        }
        catch
        {
            if (session is not null)
            {
                session.IsPurging = false;
            }

            throw;
        }
        finally
        {
            // Queued drivers can still hold the removed session. Managed semaphore objects are
            // collected with those references, instead of disposed under an awaiting driver.
            if (session is { IsPurging: true })
            {
                session.Dispose(false);
            }
        }
    }

    /// <summary> Records the terminal status a caller-driven run concluded with and persists it. A <c>.fsx</c> workflow run drives its own core loop through <c>WorkflowExecutor</c>, so the boundary engine never sees its terminal event; without this record the session would stay <see cref = "AgentSessionStatus.Running"/> on disk and the startup auto-resume would restart an already completed run (S5, ADR 0036 Resume segment). </summary>
    public async Task<AgentSessionSnapshot> RecordRunOutcomeAsync(string sessionId, AgentSessionStatus status,
        string? detail, CancellationToken cancellationToken = default)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyStatusCoreAsync(session, status, string.IsNullOrWhiteSpace(detail) ? status.ToString() : detail,
                cancellationToken).ConfigureAwait(false);
            return Snapshot(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Reads one session's append-only event log (the material S4 projects and S5 replays).</summary>
    public Task<IReadOnlyList<AgentSessionLogEntry>> ReadEventLogAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        return _store.ReadLogAsync(sessionId, cancellationToken);
    }

    /// <summary>Cancels every in-flight effect and releases scopes. Session directories are kept.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task[] chatDrivers;
        lock (_gate)
        {
            chatDrivers = [.._chatDrivers, .._harnessDrivers];
        }

        await Task.WhenAll(chatDrivers.Select(task => task.ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default))).ConfigureAwait(false);
        List<AgentSession> sessions;
        lock (_gate)
        {
            sessions = [.._sessions.Values];
            _sessions.Clear();
        }

        foreach (AgentSession session in sessions)
        {
            session.Dispose();
        }

        if (_fsi is not null)
        {
            await _fsi.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    // ---- boundary engine ----------------------------------------------------
    /// <summary> Runs boundaries until nothing is queued, the run is terminal, or the per-advance bound is reached. Each iteration is exactly one Event boundary plus the effects it issued. A session that has not started yet always begins with its launch boundary. </summary>
    private async Task<AgentSessionSnapshot> DriveAsync(AgentSession session, Event? first,
        CancellationToken cancellationToken)
    {
        if (session.IsPurging)
        {
            return Snapshot(session);
        }

        List<Event> queue = [];
        if (!session.Started)
        {
            session.Started = true;
            queue.Add(LaunchMessage(session));
        }

        if (first is not null)
        {
            queue.Add(first);
        }

        return await AgentDriver.RunAsync(0, async (boundary, token) =>
        {
            if (queue.Count == 0 || boundary >= MaxBoundariesPerAdvance || !Applies(session, queue[0]))
            {
                if (queue.Count > 0)
                {
                    // Preserve a produced event at the advance limit for the next owner of the driver.
                    session.PendingEvents.InsertRange(0, queue);
                    await PersistStateAsync(session, token).ConfigureAwait(false);
                }

                return AgentTurn<int, AgentSessionSnapshot>.NewComplete(Snapshot(session));
            }

            Event next = queue[0];
            queue.RemoveAt(0);
            List<Effect> effects = await BoundaryAsync(session, next, token).ConfigureAwait(false);
            if (session.IsTerminal || effects.Count == 0)
            {
                return AgentTurn<int, AgentSessionSnapshot>.NewContinue(boundary + 1);
            }

            await ExecuteAsync(session, effects, token).ConfigureAwait(false);
            if (DequeuePendingEvent(session) is { } produced)
            {
                // Effect results are fed back as the next Event, in produced order.
                queue.Insert(0, produced);
            }

            return AgentTurn<int, AgentSessionSnapshot>.NewContinue(boundary + 1);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary> Whether an event is still applied once the run became terminal. A recorded atomic <c>put</c> outcome is never revoked, and <c>Resume </c> is the explicit control event that leaves a terminal state; everything else is dropped at a terminal boundary. </summary>
    private static bool Applies(AgentSession session, Event evt)
    {
        return !session.IsPurging && (!session.IsTerminal || evt.IsPutResult || evt.IsResume ||
                                      ((evt.IsToolResult || evt.IsToolFailure) &&
                                       !session.Context.NativeCalls.IsEmpty));
    }

    /// <summary> One Event boundary: fold the inbox, let the pure core decide, record the outcome and return the effects to execute. The state gate is released before effects run, so a cancel can land while an effect is in flight. </summary>
    private async Task<List<Effect>> BoundaryAsync(AgentSession session, Event evt, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A recorded put outcome and the Resume control event are still applied after the run
            // became terminal; every other event is dropped at a terminal boundary.
            if (!Applies(session, evt))
            {
                return [];
            }

            await AppendLogAsync(session, AgentLogKinds.Event, AgentSessionCodec.ToJson(evt), cancellationToken)
                .ConfigureAwait(false);
            await ApplyEventAsync(session, evt, cancellationToken).ConfigureAwait(false);
            return ApplyEffects(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Applies one event to the context, persists the outcome and refreshes the host status.</summary>
    private async Task ApplyEventAsync(AgentSession session, Event evt, CancellationToken cancellationToken)
    {
        if (evt is Event.UserMessage)
        {
            await AppendLogAsync(session, "turn/start", "{}", cancellationToken).ConfigureAwait(false);
        }

        List<Tuple<string, string>> inbox = DrainInbox(session);
        Tuple<Context, FSharpList<Effect>> result =
            AgentCoreModule.chatStep(session.Context, ListModule.OfSeq(inbox), evt);
        await AppendRetryFeedbackAsync(session, session.Context, result.Item1, cancellationToken).ConfigureAwait(false);
        session.Context = result.Item1;
        session.PendingEffects = [..ListModule.ToSeq(result.Item2)];
        session.RefreshStatusFromCore();
        if (session.Status == AgentSessionStatus.Idle)
        {
            await AppendLogAsync(session, "turn/end", "{\"reason\":\"completed\"}", cancellationToken)
                .ConfigureAwait(false);
            await AppendLogAsync(session, AgentLogKinds.Status,
                LogStatusPayload(AgentSessionStatus.Idle, "turn completed"), cancellationToken).ConfigureAwait(false);
        }

        session.Detail =
            session.Context.Status.IsStopped && session.Context.History.LastOrDefault() is HistoryEntry.ToolResult
            {
                name: "agent-retry"
            } retry
                ? retry.payload
                : Describe(evt);
        await PersistAsync(session, cancellationToken).ConfigureAwait(false);
        Report(session);
    }

    /// <summary> Effects of the most recent boundary, guarded: a terminal run never issues another effect, so a result recorded after a cancel or stop cannot restart the loop. </summary>
    private static List<Effect> ApplyEffects(AgentSession session)
    {
        if (session.IsTerminal)
        {
            session.PendingEffects = [];
            return [];
        }

        List<Effect> effects = session.PendingEffects;
        session.PendingEffects = [];
        return effects;
    }

    /// <summary>Executes one boundary's effects in order and queues their results as the next events.</summary>
    private async Task ExecuteAsync(AgentSession session, List<Effect> effects, CancellationToken cancellationToken)
    {
        foreach (Effect effect in effects)
        {
            if (session.IsTerminal)
            {
                return;
            }

            await AppendEffectIssuedAsync(session, effect, cancellationToken).ConfigureAwait(false);
            AgentEffectOutcome outcome;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            CancellationToken effectToken = session.BeginEffect(cancellationToken);
            try
            {
                outcome = await _interpreter.ExecuteAsync(ContextFor(session), effect, effectToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await AppendEffectResultAsync(session, effect,
                    AgentEffectOutcome.Terminal(AgentSessionStatus.Cancelled, "执行已停止")with
                    {
                        FailureCode = "CANCELLED"
                    }, CancellationToken.None,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds).ConfigureAwait(false);
                // In-flight cancellation: the call is abandoned and no result is recorded. Consumed
                // tokens are not returned (ADR 0036).
                session.Detail = "in-flight effect cancelled";
                Report(session);
                return;
            }
            catch (Exception exception)
            {
                await AppendEffectResultAsync(session, effect,
                    AgentEffectOutcome.Terminal(AgentSessionStatus.Failed, exception.ToString())with
                    {
                        FailureCode = exception.GetType().Name
                    }, CancellationToken.None,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds).ConfigureAwait(false);
                await ApplyStatusAsync(session, AgentSessionStatus.Failed, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                session.EndEffect(effectToken);
            }

            await AppendEffectResultAsync(session, effect, outcome, CancellationToken.None,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds).ConfigureAwait(false);
            if (outcome.Disposition == AgentEffectDisposition.Terminal && outcome.TerminalStatus is { } terminalStatus)
            {
                await ApplyStatusAsync(session, terminalStatus, outcome.Summary, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (outcome.ResultEvent is not null)
            {
                session.PendingEvents.Add(outcome.ResultEvent);
            }
        }
    }

    /// <summary>Applies a host - side lifecycle transition, taking the state gate. </summary>
    private async Task ApplyStatusAsync(AgentSession session, AgentSessionStatus status, string detail,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyStatusCoreAsync(session, status, detail, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Applies a host-side lifecycle transition and records it in the log. The gate is held. </summary>
    private async Task ApplyStatusCoreAsync(AgentSession session, AgentSessionStatus status, string detail,
        CancellationToken cancellationToken)
    {
        session.Status = status;
        session.Context = WithStatus(session.Context, AgentSessionCodec.ToCoreStatus(status));
        session.Detail = detail;
        await AppendLogAsync(session, AgentLogKinds.Status, LogStatusPayload(status, detail), cancellationToken)
            .ConfigureAwait(false);
        await PersistAsync(session, cancellationToken).ConfigureAwait(false);
        Report(session);
    }

    private static string LogStatusPayload(AgentSessionStatus status, string detail)
    {
        return "{\"status\":\"" + status + "\",\"detail\":" + JsonSerializer.Serialize(detail) + "}";
    }

    private static Context WithStatus(Context context, RunStatus status)
    {
        return new Context(context.EventSeq, context.EffectSeq, context.WaitSeq, status, context.History, context.Calls,
            context.ProcessedMessageIds, context.ArmedWaits, context.Retry, context.NativeCalls);
    }

    private static string Describe(Event evt)
    {
        return AgentSessionCodec.CaseOf(evt) switch
        {
            "AssistantReply" => "assistant response recorded",
            "ModelResult" => "model result recorded",
            "ModelFailure" => ((Event.ModelFailure)evt).detail,
            "ToolResult" => "tool result recorded",
            "ToolFailure" => "tool failure recorded",
            "PutResult" => "put commit point recorded",
            "UserMessage" => "user message appended at the boundary",
            "RunEvent" => "host run event delivered",
            "ScriptProgress" => "progress reported",
            "Cancel" => "cancelled",
            "Resume" => "resumed",
            _ => "boundary advanced"
        };
    }

    public async Task RecordSdkAsync(string sessionId, AgentSdkReceipt receipt)
    {
        await _store.AppendLogAsync(sessionId, "sdk/operation", JsonSerializer.Serialize(receipt),
            CancellationToken.None).ConfigureAwait(false);
    }

    private AgentEffectContext ContextFor(AgentSession session)
    {
        ModelSelection? modelSelection = session.Launch.Parameters.TryGetValue(
                                             AgentEffectContext.ModelSelectionParameter, out string? encodedModel) &&
                                         !string.IsNullOrWhiteSpace(encodedModel)
            ? ModelSelectionCodec.Decode(encodedModel)
            : null;
        return new AgentEffectContext(session.SessionId,
            InstructionsFor(session) + "\n" + AgentNativeTools.Instructions, AgentNativeTools.Definitions,
            [..ListModule.ToSeq(session.Context.History)])
        {
            SessionDirectory = _store.ResolveSessionDirectory(session.SessionId),
            RecordSdk = receipt => RecordSdkAsync(session.SessionId, receipt),
            RequestRetryAttempt = session.Context.Retry.Attempts,
            ModelSelection = modelSelection
        };
    }

    /// <summary> The append-only instruction prefix of one loaded session, or null when it is not loaded. The value is what every <see cref = "AgentEffectContext"/> of the session carries. </summary>
    public string? TryGetInstructions(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out AgentSession? session) ? session.Instructions : null;
        }
    }

    /// <summary> The durable instruction head of one session: the prefix recorded at creation when it has one, otherwise the default derived from the launch parameters. It is readable without loading the session, so a resumed run resolves the same head the run started with. </summary>
    public async Task<string?> TryReadInstructionsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        string? open = TryGetInstructions(sessionId);
        if (open is not null)
        {
            return open;
        }

        string? stored = (await _store.TryReadStateAsync(sessionId, cancellationToken).ConfigureAwait(false))
            ?.Instructions;
        if (stored is not null)
        {
            return stored;
        }

        AgentSessionLaunchParameters? launch =
            await _store.TryReadLaunchAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return launch is null ? null : InstructionPrefixOf(launch);
    }

    /// <summary> Replaces the instruction prefix of one loaded session and persists it. The prefix is append-only for the caller: a workflow session applies the instructions its run appended, so a later boundary of the same session sends the same prefix the run used. </summary>
    public async Task ApplyInstructionsAsync(string sessionId, string instructions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(session.Instructions, instructions, StringComparison.Ordinal))
            {
                return;
            }

            session.Instructions = instructions;
            await PersistAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary> Persists the core <c>Context</c> a caller advanced outside the boundary loop (a <c>.fsx</c> workflow run drives its own core loop through <c>WorkflowExecutor</c>). A session that has no directory yet is created from <paramref name = "launch"/>; an existing one is opened, so a recorded <c>launch.json</c> is never overwritten by a later checkpoint. </summary>
    public async Task PersistContextAsync(string sessionId, Context context, AgentSessionLaunchParameters launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(launch);
        if (!_store.Exists(sessionId))
        {
            await CreateAsync(launch, cancellationToken).ConfigureAwait(false);
        }

        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendRetryFeedbackAsync(session, session.Context, context, cancellationToken).ConfigureAwait(false);
            session.Context = context;
            await PersistAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary> The instruction head a session starts with: the explicit prefix when the launch carried one, otherwise the default derived from the launch parameters (byte-stable across a resume). </summary>
    private static string InstructionPrefixOf(AgentSessionLaunchParameters launch)
    {
        return string.IsNullOrWhiteSpace(launch.InstructionPrefix)
            ? DefaultInstructionsFor(launch)
            : launch.InstructionPrefix;
    }

    /// <summary> The default immutable instruction head for a session. It is derived only from the launch parameters, so it is byte-stable across a resume (prefix-cache invariant). </summary>
    private static string DefaultInstructionsFor(AgentSessionLaunchParameters launch)
    {
        return "Patchouli agent session for workflow " + launch.WorkflowUri +
               ". Answer through the provided tools and report progress explicitly.";
    }

    /// <summary>The immutable instruction head of one session (never empty ). </summary>
    private static string InstructionsFor(AgentSession session)
    {
        return InstructionPrefixOf(session.Launch);
    }

    private static Event LaunchMessage(AgentSession session)
    {
        string text = session.IsChat && session.Launch.Parameters.TryGetValue("prompt", out string? prompt)
            ? prompt
            : session.Launch.ToBoundaryText();
        return Event.NewUserMessage("launch:" + session.SessionId, text);
    }

    private static List<Tuple<string, string>> DrainInbox(AgentSession session)
    {
        List<Tuple<string, string>> inbox = [];
        foreach (AgentInboxMessage message in session.Inbox)
        {
            inbox.Add(Tuple.Create(message.MessageId, message.Text));
        }

        session.Inbox.Clear();
        return inbox;
    }

    private static Event? DequeuePendingEvent(AgentSession session)
    {
        if (session.PendingEvents.Count == 0)
        {
            return null;
        }

        Event evt = session.PendingEvents[0];
        session.PendingEvents.RemoveAt(0);
        return evt;
    }

    // ---- persistence --------------------------------------------------------
    private async Task PersistAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (session.IsPurging)
        {
            return;
        }

        session.UpdatedAt = _time.GetUtcNow();
        await _store.WriteContextAsync(session.SessionId, session.Context, cancellationToken).ConfigureAwait(false);
        await PersistStateAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private Task PersistStateAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (session.IsPurging)
        {
            return Task.CompletedTask;
        }

        session.UpdatedAt = _time.GetUtcNow();
        AgentSessionStateDocument document = new(Snapshot(session), [..session.Inbox], [..session.PendingEvents],
            session.Instructions);
        return _store.WriteStateAsync(session.SessionId, document, cancellationToken);
    }

    private Task<long> AppendLogAsync(AgentSession session, string kind, string payload,
        CancellationToken cancellationToken)
    {
        if (session.IsPurging)
        {
            return Task.FromResult(0L);
        }

        return _store.AppendLogAsync(session.SessionId, kind, payload, cancellationToken);
    }

    private async Task AppendEffectIssuedAsync(AgentSession session, Effect effect, CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (effect is Effect.LlmChat && session.Context.Retry.Attempts == 0)
            {
                session.Context = AgentCoreModule.withRetryLimit(MaxRequestRetries, session.Context);
                await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            }

            if (effect is Effect.LlmChat && session.Context.Retry.Attempts > 0)
            {
                session.Detail = $"模型请求重试 {session.Context.Retry.Attempts}/{session.Context.Retry.Limit}，等待后重试";
                await AppendLogAsync(session, AgentLogKinds.Event,
                        AgentSessionCodec.ToJson(Event.NewScriptProgress(session.Detail)), cancellationToken)
                    .ConfigureAwait(false);
                Report(session);
            }

            await AppendLogAsync(session, AgentLogKinds.EffectIssued, EffectPayload(effect, session.Context),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task AppendEffectResultAsync(AgentSession session, Effect effect, AgentEffectOutcome outcome,
        CancellationToken cancellationToken, double elapsedMs = 0)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string output = outcome.ResultEvent switch
            {
                Event.ModelResult model => model.text,
                Event.ModelFailure failure => failure.detail,
                Event.AssistantReply assistant => assistant.reply.Text,
                Event.ToolResult tool => tool.payload,
                Event.ToolFailure tool => tool.payload,
                Event.RunEvent run => run.payload,
                _ => outcome.Summary
            };
            string state = outcome.TerminalStatus is AgentSessionStatus.Cancelled or AgentSessionStatus.Stopped
                ? "stopped"
                : outcome.FailureCode is not null || outcome.TerminalStatus == AgentSessionStatus.Failed
                    ? "failed"
                    : "completed";
            string payload = JsonSerializer.Serialize(new
            {
                effectId = EffectIdOf(effect), disposition = outcome.Disposition.ToString(), state,
                commitPointEntered = outcome.CommitPointEntered, summary = outcome.Summary,
                errorCode = outcome.FailureCode, elapsedMs, output,
                model = outcome.ResultEvent is Event.AssistantReply modelReply ? modelReply.reply.Model : null
            });
            await AppendLogAsync(session, AgentLogKinds.EffectResult, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static string EffectPayload(Effect effect, Context context)
    {
        string input = effect switch
        {
            Effect.McpToolCall tool => tool.arguments,
            Effect.Put put => JsonSerializer.Serialize(new { put.uri, put.content }),
            Effect.OcrEnqueue ocr => JsonSerializer.Serialize(new { ocr.documentId, ocr.pageRange }),
            Effect.WaitRunEvent wait => wait.runUri,
            Effect.LlmChat => context.History.OfType<HistoryEntry.UserMessage>().LastOrDefault()?.text ?? "",
            _ => ""
        };
        return JsonSerializer.Serialize(new
        {
            effectId = EffectIdOf(effect), kind = EffectKind(effect), state = "running", input,
            name = effect is Effect.McpToolCall namedTool ? namedTool.name : EffectKind(effect)
        });
    }

    private static long EffectIdOf(Effect effect)
    {
        return effect switch
        {
            Effect.LlmChat chat => chat.effectId.Item,
            Effect.McpToolCall tool => tool.effectId.Item,
            Effect.Put put => put.effectId.Item,
            Effect.OcrEnqueue ocr => ocr.effectId.Item,
            Effect.WaitRunEvent wait => wait.effectId.Item,
            Effect.ReportProgress progress => progress.effectId.Item,
            _ => 0L
        };
    }

    private static string EffectKind(Effect effect)
    {
        return effect switch
        {
            Effect.LlmChat => "LlmChat",
            Effect.McpToolCall => "McpToolCall",
            Effect.Put => "Put",
            Effect.OcrEnqueue => "OcrEnqueue",
            Effect.WaitRunEvent => "WaitRunEvent",
            Effect.ReportProgress => "ReportProgress",
            _ => effect.IsFinish ? "Finish" : "Stop"
        };
    }

    // ---- state access -------------------------------------------------------
    private AgentSessionSnapshot Snapshot(AgentSession session)
    {
        return new AgentSessionSnapshot(session.SessionId, session.Status, session.Context.EventSeq,
            session.Context.History.Length, session.Inbox.Count, session.PendingEvents.Count, session.Detail,
            session.UpdatedAt);
    }

    private async Task<AgentSession> RequireAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out AgentSession? open))
            {
                return open;
            }
        }

        AgentSessionSnapshot? opened = await TryOpenAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            throw new KeyNotFoundException($"Agent session '{sessionId}' has no directory under '{SessionsRoot}'.");
        }

        lock (_gate)
        {
            return _sessions[sessionId];
        }
    }

    // ---- activity reporting -------------------------------------------------
    private void OpenScope(AgentSession session)
    {
        if (_activity is null)
        {
            return;
        }

        // Agent sessions are reported as MCP-kind host activity: the built-in agent acts through the
        // MCP surface, and HostActivityKind has no dedicated agent member.
        session.Scope = _activity.BeginScope($"Agent session {session.SessionId}", HostActivityKind.Mcp, session.Detail,
            session.SessionId);
    }

    private void Report(AgentSession session)
    {
        if (session.IsTerminal || session.Status == AgentSessionStatus.Idle)
        {
            session.Scope?.UpdateDetail($"{session.Status}: {session.Detail}");
            CloseScope(session);
            return;
        }

        if (session.Scope is null)
        {
            OpenScope(session);
            return;
        }

        session.Scope.UpdateDetail($"{session.Status}: {session.Detail}");
    }

    private static void CloseScope(AgentSession session)
    {
        session.Scope?.Dispose();
        session.Scope = null;
    }

    /// <summary>Mutable in-process state of one loaded session. All access is gate-protected.</summary>
    private sealed class AgentSession : IDisposable
    {
        private readonly Lock _sync = new();
        private CancellationTokenSource? _effect;
        private bool _disposed;

        public AgentSession(AgentSessionLaunchParameters launch, CancellationToken lifetime)
        {
            Launch = launch;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        public string SessionId => Launch.SessionId;
        public AgentSessionLaunchParameters Launch { get; }
        public bool IsChat => string.Equals(Launch.WorkflowUri, ChatWorkflowUri, StringComparison.Ordinal);
        public CancellationTokenSource Lifetime { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim Driver { get; } = new(1, 1);
        public Context Context { get; set; } = AgentCoreModule.initial;

        /// <summary>The append-only instruction head sent to every effect of this session.</summary>
        public string Instructions { get; set; } = string.Empty;

        public AgentSessionStatus Status { get; set; } = AgentSessionStatus.Running;
        public string? Detail { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<AgentInboxMessage> Inbox { get; } = [];
        public List<Event> PendingEvents { get; } = [];
        public List<Effect> PendingEffects { get; set; } = [];
        public bool Started { get; set; }

        public volatile bool IsPurging;
        public IActivityScope? Scope { get; set; }

        public bool IsTerminal => IsPurging || Status is AgentSessionStatus.Cancelled or AgentSessionStatus.Stopped
            or AgentSessionStatus.Finished or AgentSessionStatus.Failed;

        /// <summary>Core statuses never override a host-side failure.</summary>
        public void RefreshStatusFromCore()
        {
            if (Status == AgentSessionStatus.Failed)
            {
                return;
            }

            Status = AgentSessionCodec.FromCoreStatus(Context.Status);
        }

        /// <summary> Starts one effect and returns the token it must observe : the caller 's token plus this session's immediate-cancel token. </summary>
        public CancellationToken BeginEffect(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _effect?.Dispose();
                _effect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
                return _effect.Token;
            }
        }

        public void EndEffect(CancellationToken effectToken)
        {
            lock (_sync)
            {
                if (_effect is { } current && current.Token == effectToken)
                {
                    current.Dispose();
                    _effect = null;
                }
            }
        }

        /// <summary>Cancels the in-flight effect immediately (used by cancel, stop and purge).</summary>
        public void CancelInFlight()
        {
            CancellationTokenSource? current;
            lock (_sync)
            {
                current = _effect;
                _effect = null;
            }

            try
            {
                current?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The effect already concluded; nothing to cancel.
            }
            finally
            {
                current?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void Dispose(bool disposeGates)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelInFlight();
            Scope?.Dispose();
            Scope = null;
            Lifetime.Dispose();
            if (disposeGates)
            {
                Gate.Dispose();
                Driver.Dispose();
            }
        }
    }
}
