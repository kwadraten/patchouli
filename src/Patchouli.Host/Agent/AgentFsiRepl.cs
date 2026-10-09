using System.Text.Json;

namespace Patchouli.Host.Agent;

/// <summary>Session-local FSI workers, started lazily on the first execution.</summary>
public sealed class AgentFsiRepl : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _clearing;
    private readonly bool _ownsWorkspaces;

    public AgentFsiRepl(AgentWorkspaceStore? workspaces = null)
    {
        _ownsWorkspaces = workspaces is null;
        Workspaces = workspaces ?? new AgentWorkspaceStore(new AgentSessionStore(
            Path.Combine(Path.GetTempPath(), "patchouli-repl", Guid.NewGuid().ToString("N"))));
    }

    public AgentWorkspaceStore Workspaces { get; }

    public Task<AgentToolOutcome> ExecuteAsync(string sessionId, string arguments, CancellationToken cancellationToken)
    {
        return ExecuteAsync(sessionId, arguments, null, cancellationToken);
    }

    public async Task<AgentToolOutcome> ExecuteAsync(string sessionId, string arguments,
        Func<string, string, Task<Patchouli.Agent.Sdk.SdkResult>>? invokeSdk, CancellationToken cancellationToken,
        string sdkBindings = "")
    {
        Session session;
        lock (_gate)
        {
            if (_disposed || _clearing)
            {
                return Unavailable();
            }

            if (!_sessions.TryGetValue(sessionId, out session!))
            {
                session = new Session();
                _sessions.Add(sessionId, session);
            }
        }

        string code;
        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("code", out JsonElement source) ||
                source.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(source.GetString()))
            {
                return new AgentToolOutcome(false, "INVALID_ARGUMENT: fsi requires a non-empty code string.",
                    "INVALID_ARGUMENT");
            }

            code = source.GetString()!;
        }
        catch (JsonException exception)
        {
            return new AgentToolOutcome(false, "INVALID_ARGUMENT: " + exception.Message, "INVALID_ARGUMENT");
        }

        await session.Execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool discardHost = false;
        try
        {
            lock (_gate)
            {
                if (_disposed || _clearing || session.Retired)
                {
                    return Unavailable();
                }
            }

            // Compiler startup stays outside the lifecycle lock so cleanup and disposal
            // can interrupt the worker, including during the first invocation.
            string directory = Workspaces.Ensure(sessionId);
            AgentFsiProcess host = session.Host ??
                                   await AgentFsiProcess.StartAsync(directory, cancellationToken)
                                       .ConfigureAwait(false);
            lock (_gate)
            {
                session.Host = host;
                // A removed session must not resume calls queued behind an evaluation.
                if (_disposed || _clearing || session.Retired)
                {
                    host.Interrupt();
                    discardHost = true;
                    return Unavailable();
                }
            }

            return await host.EvaluateAsync(code, invokeSdk, cancellationToken, sdkBindings).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ResetHostAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception
                                              or JsonException)
        {
            await ResetHostAsync(session).ConfigureAwait(false);
            return new AgentToolOutcome(false, "FSI_EVALUATION_FAILED: " + exception.Message, "FSI_EVALUATION_FAILED");
        }
        finally
        {
            if (discardHost)
            {
                await ResetHostAsync(session).ConfigureAwait(false);
            }

            session.Execution.Release();
        }
    }

    /// <summary>Stops workers before clearing scratch files; conversation history remains.</summary>
    public async Task ClearWorkingDirectoriesAsync()
    {
        Session[] sessions;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clearing)
            {
                throw new InvalidOperationException("Agent workspace cleanup is already running.");
            }

            _clearing = true;
            sessions = _sessions.Values.ToArray();
            foreach (Session session in sessions)
            {
                session.Host?.Interrupt();
            }
        }

        try
        {
            foreach (Session session in sessions)
            {
                await session.Execution.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ResetHostAsync(session).ConfigureAwait(false);
                }
                finally
                {
                    session.Execution.Release();
                }
            }

            await Task.Run(Workspaces.Clear).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _clearing = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            foreach (Session session in sessions)
            {
                session.Retired = true;
                session.Host?.Interrupt();
            }
        }

        foreach (Session session in sessions)
        {
            await DisposeSessionAsync(session).ConfigureAwait(false);
        }

        if (_ownsWorkspaces)
        {
            Workspaces.Clear();
        }
    }

    public async ValueTask ForgetAsync(string sessionId)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out session))
            {
                return;
            }

            session.Retired = true;
            session.Host?.Interrupt();
        }

        await DisposeSessionAsync(session).ConfigureAwait(false);
    }

    private static async Task DisposeSessionAsync(Session session)
    {
        await session.Execution.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetHostAsync(session).ConfigureAwait(false);
        }
        finally
        {
            session.Execution.Release();
        }
    }

    private static AgentToolOutcome Unavailable()
    {
        return new AgentToolOutcome(false,
            "FSI_UNAVAILABLE: FSI worker is being cleared or its session has been disposed.",
            "FSI_UNAVAILABLE");
    }

    private static async Task ResetHostAsync(Session session)
    {
        AgentFsiProcess? host = session.Host;
        session.Host = null;
        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Session
    {
        public bool Retired { get; set; }
        public AgentFsiProcess? Host { get; set; }
        public SemaphoreSlim Execution { get; } = new(1, 1);
    }
}
