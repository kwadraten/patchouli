using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Patchouli.Agent;

namespace Patchouli.Host.Agent;

/// <summary>
///     Durable storage for agent sessions: one directory per session under the Library's auxiliary
///     data (<c>agent-sessions/</c> next to the runtime database). Each directory holds the launch
///     parameters, the current <c>Context</c> snapshot, the observable session state and one
///     append-only event log with monotonically increasing sequence numbers.
/// </summary>
/// <remarks>
///     <para>
///         A missing sessions root or a missing session directory is a normal, non-fatal condition:
///         every read returns <c>null</c>/empty instead of throwing, so a Library always opens
///         (ADR 0036). Directories are created lazily, only when a session is actually written.
///     </para>
///     <para>
///         Purging removes exactly one session directory and nothing else. It never touches Items,
///         original documents, translations or OCR results, and it never reuses the OCR queue
///         (ADR 0036 D7). Session data is never part of <c>library_revision</c>.
///     </para>
/// </remarks>
public sealed partial class AgentSessionStore
{
    /// <summary>Auxiliary Library directory that holds one subdirectory per session.</summary>
    public const string DirectoryName = "agent-sessions";

    private const string LaunchFileName = "launch.json";
    private const string StateFileName = "snapshot.json";
    private const string ContextFileName = "context.json";
    private const string LogFileName = "events.jsonl";

    private readonly Lock _seqGate = new();
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private readonly Dictionary<string, long> _lastSeq = new(StringComparer.Ordinal);

    /// <summary>Creates a store rooted at an explicit sessions directory.</summary>
    public AgentSessionStore(string sessionsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsRoot);
        SessionsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot));
    }

    /// <summary>The sessions root directory (may not exist yet).</summary>
    public string SessionsRoot { get; }

    /// <summary>
    ///     Resolves the auxiliary sessions root for one Library from the runtime database path:
    ///     a sibling <c>agent-sessions</c> directory, so session data travels with the library
    ///     folder and never with the index database file itself.
    /// </summary>
    public static AgentSessionStore ForLibrary(string libraryDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryDatabasePath);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(libraryDatabasePath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The library path must have a parent directory.",
                nameof(libraryDatabasePath));
        }

        return new AgentSessionStore(Path.Combine(directory, DirectoryName));
    }

    /// <summary>True when the session's own directory exists on disk.</summary>
    public bool Exists(string sessionId)
    {
        return Directory.Exists(ResolveSessionDirectory(sessionId));
    }

    /// <summary>
    ///     The absolute directory of one session. The id is validated as a single safe path segment,
    ///     so a caller-supplied id can never escape the sessions root.
    /// </summary>
    public string ResolveSessionDirectory(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionIdPattern().IsMatch(sessionId) || sessionId is "." or "..")
        {
            throw new ArgumentException(
                $"Invalid agent session id '{sessionId}': expected 1-64 characters of [A-Za-z0-9._-].",
                nameof(sessionId));
        }

        return Path.Combine(SessionsRoot, sessionId);
    }

    /// <summary>Session ids present on disk, in ordinal order; empty when the root does not exist.</summary>
    public IReadOnlyList<string> ListSessionIds()
    {
        if (!Directory.Exists(SessionsRoot))
        {
            return [];
        }

        List<string> ids = [];
        foreach (string directory in Directory.EnumerateDirectories(SessionsRoot))
        {
            string name = Path.GetFileName(directory);
            if (SessionIdPattern().IsMatch(name))
            {
                ids.Add(name);
            }
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>Creates the session directory and writes its launch parameters.</summary>
    public async Task CreateSessionAsync(AgentSessionLaunchParameters launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string directory = ResolveSessionDirectory(launch.SessionId);
        Directory.CreateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, LaunchFileName), AgentSessionCodec.ToJson(launch),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the launch parameters, or null when the session directory or file is missing.</summary>
    public async Task<AgentSessionLaunchParameters?> TryReadLaunchAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        string? text = await TryReadTextAsync(Path.Combine(ResolveSessionDirectory(sessionId), LaunchFileName),
            cancellationToken).ConfigureAwait(false);
        return text is null ? null : AgentSessionCodec.LaunchFromJson(text);
    }

    /// <summary>Writes the observable session state document (snapshot, pending inbox, pending events).</summary>
    public async Task WriteStateAsync(string sessionId, AgentSessionStateDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        await WriteAtomicAsync(Path.Combine(ResolveSessionDirectory(sessionId), StateFileName),
            AgentSessionCodec.ToJson(document), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the session state document, or null when it has not been written yet.</summary>
    public async Task<AgentSessionStateDocument?> TryReadStateAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        string? text = await TryReadTextAsync(Path.Combine(ResolveSessionDirectory(sessionId), StateFileName),
            cancellationToken).ConfigureAwait(false);
        return text is null ? null : AgentSessionCodec.StateFromJson(text);
    }

    /// <summary>Writes the full core <c>Context</c> snapshot for restore (S5 replays the log on top).</summary>
    public async Task WriteContextAsync(string sessionId, Context context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await WriteAtomicAsync(Path.Combine(ResolveSessionDirectory(sessionId), ContextFileName),
            AgentSessionCodec.ToJson(context), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the persisted core <c>Context</c>, or null when the session has no snapshot yet.</summary>
    public async Task<Context?> TryReadContextAsync(string sessionId, CancellationToken cancellationToken)
    {
        string? text = await TryReadTextAsync(Path.Combine(ResolveSessionDirectory(sessionId), ContextFileName),
            cancellationToken).ConfigureAwait(false);
        return text is null ? null : AgentSessionCodec.ContextFromJson(text);
    }

    /// <summary>
    ///     Appends one line to the session's append-only event log and returns its sequence number.
    ///     Sequence numbers are session-scoped, start at 1 and never decrease; they are seeded from
    ///     the recorded log on first use, so a reopened store continues the same sequence.
    /// </summary>
    public async Task<long> AppendLogAsync(string sessionId, string kind, string payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        string directory = ResolveSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, LogFileName);
        await _logGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long seq = await NextSeqAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
            AgentSessionLogEntry entry = new(seq, kind, payload, DateTimeOffset.UtcNow);
            string line = AgentSessionCodec.ToLine(entry) + "\n";
            await using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return seq;
        }
        finally
        {
            _logGate.Release();
        }
    }

    /// <summary>
    ///     Reads the whole append-only event log in recorded order. Unparsable lines are skipped
    ///     rather than failing the read: a truncated tail must not block opening a Library.
    /// </summary>
    public async Task<IReadOnlyList<AgentSessionLogEntry>> ReadLogAsync(string sessionId,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(ResolveSessionDirectory(sessionId), LogFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        List<AgentSessionLogEntry> entries = [];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            AgentSessionLogEntry? entry = AgentSessionCodec.TryParseLine(line);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    ///     Physically removes exactly one session directory. Nothing outside the sessions root is
    ///     ever touched: Items, original documents, translations, OCR results and the OCR queue are
    ///     unaffected (ADR 0036 D7 and item-purge separation).
    /// </summary>
    public bool Purge(string sessionId)
    {
        string directory = Path.GetFullPath(ResolveSessionDirectory(sessionId));
        string parent = Path.GetDirectoryName(directory) ?? string.Empty;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(SessionsRoot), PathComparison))
        {
            // The resolved directory is not a direct child of the sessions root: refuse rather than delete.
            throw new InvalidOperationException(
                $"Refusing to purge '{directory}': it is not a direct child of '{SessionsRoot}'.");
        }

        lock (_seqGate)
        {
            _lastSeq.Remove(sessionId);
        }

        if (!Directory.Exists(directory))
        {
            return false;
        }

        Directory.Delete(directory, true);
        return true;
    }

    private async Task<long> NextSeqAsync(string sessionId, string logPath, CancellationToken cancellationToken)
    {
        lock (_seqGate)
        {
            if (_lastSeq.TryGetValue(sessionId, out long known))
            {
                long next = known + 1;
                _lastSeq[sessionId] = next;
                return next;
            }
        }

        long recorded = await LastRecordedSeqAsync(logPath, cancellationToken).ConfigureAwait(false);
        lock (_seqGate)
        {
            long next = Math.Max(recorded, _lastSeq.TryGetValue(sessionId, out long raced) ? raced : 0) + 1;
            _lastSeq[sessionId] = next;
            return next;
        }
    }

    private static async Task<long> LastRecordedSeqAsync(string logPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(logPath))
        {
            return 0;
        }

        long last = 0;
        await using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            AgentSessionLogEntry? entry = AgentSessionCodec.TryParseLine(line);
            if (entry is not null)
            {
                last = Math.Max(last, entry.Seq);
            }
        }

        return last;
    }

    private static async Task<string?> TryReadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Writes a snapshot through a temporary file and an atomic replace, so a crash mid-write
    ///     leaves the previous snapshot intact instead of a half-written file.
    /// </summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();
}
