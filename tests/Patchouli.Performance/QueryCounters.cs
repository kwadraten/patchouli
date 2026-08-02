namespace Patchouli.Performance;

/// <summary>
/// Thread-safe counters for SQL statement executions, writes, and rows read. The counters
/// deliberately never capture SQL text, parameter values, local paths, resource identifiers,
/// or any content, so they are safe to include in performance logs and machine-readable reports.
/// </summary>
public sealed class QueryCounters
{
    private long _statements;
    private long _writes;
    private long _rowsRead;
    private long _uiThreadCommands;

    public long Statements => Interlocked.Read(ref _statements);
    public long Writes => Interlocked.Read(ref _writes);
    public long RowsRead => Interlocked.Read(ref _rowsRead);

    /// <summary>
    /// Number of database command executions that happened on the Avalonia UI dispatcher thread.
    /// AC3 requires database work to never run on the UI dispatcher, so this counter is the
    /// deterministic proof that the measured write pipeline stayed off the UI thread.
    /// </summary>
    public long UiThreadCommands => Interlocked.Read(ref _uiThreadCommands);

    internal void RecordStatement()
    {
        Interlocked.Increment(ref _statements);
    }

    internal void RecordWrite()
    {
        Interlocked.Increment(ref _writes);
    }

    internal void RecordRow()
    {
        Interlocked.Increment(ref _rowsRead);
    }

    internal void RecordUiThreadCommand()
    {
        Interlocked.Increment(ref _uiThreadCommands);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _statements, 0);
        Interlocked.Exchange(ref _writes, 0);
        Interlocked.Exchange(ref _rowsRead, 0);
        Interlocked.Exchange(ref _uiThreadCommands, 0);
    }
}
