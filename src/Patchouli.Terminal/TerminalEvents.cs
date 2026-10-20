namespace Patchouli.Terminal;

/// <summary>
/// Raw bytes read from the hosted process. The bytes are a private copy owned by this event.
/// </summary>
public sealed class TerminalOutputEventArgs : EventArgs
{
    public TerminalOutputEventArgs(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
    }

    /// <summary>
    /// Gets the raw output bytes. The terminal stream is UTF-8 on supported platforms.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes { get; }
}

/// <summary>
/// Incrementally decoded text read from the hosted process.
/// </summary>
public sealed class TerminalTextOutputEventArgs : EventArgs
{
    public TerminalTextOutputEventArgs(string text)
    {
        Text = text;
    }

    /// <summary>
    /// Gets the text decoded so far. Multi-byte UTF-8 sequences split across reads are preserved.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Describes a hosted process exit.
/// </summary>
public sealed class TerminalExitedEventArgs : EventArgs
{
    public TerminalExitedEventArgs(int exitCode, bool exitCodeKnown, int processId)
    {
        ExitCode = exitCode;
        ExitCodeKnown = exitCodeKnown;
        ProcessId = processId;
    }

    /// <summary>
    /// Gets the process exit code, or <c>-1</c> when the process was killed and no code was reported.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets whether <see cref="ExitCode"/> was reported by the terminal instead of being a kill fallback.
    /// </summary>
    public bool ExitCodeKnown { get; }

    /// <summary>
    /// Gets the operating-system process identifier that exited.
    /// </summary>
    public int ProcessId { get; }
}
