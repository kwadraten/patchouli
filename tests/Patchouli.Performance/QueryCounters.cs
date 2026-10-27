using Patchouli.Core.Diagnostics;

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
    private long _uiThreadStart = UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite);

    public long Statements => Interlocked.Read(ref _statements);
    public long Writes => Interlocked.Read(ref _writes);
    public long RowsRead => Interlocked.Read(ref _rowsRead);

    /// <summary>
    /// Global SQLite connection-open violations since reset, including ordinary production factories,
    /// not just statements issued through this counting factory. Zero is AC3's execution-boundary gate.
    /// </summary>
    public long UiThreadCommands => UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite) -
                                    Interlocked.Read(ref _uiThreadStart);

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

    public void Reset()
    {
        Interlocked.Exchange(ref _statements, 0);
        Interlocked.Exchange(ref _writes, 0);
        Interlocked.Exchange(ref _rowsRead, 0);
        Interlocked.Exchange(ref _uiThreadStart, UiThreadExecutionGuard.GetCount(UiThreadExecutionGuard.Sqlite));
    }
}
