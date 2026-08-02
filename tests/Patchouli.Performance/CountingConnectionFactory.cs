using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Performance;

/// <summary>
/// A <see cref="SqliteConnectionFactory"/> whose connections count statements and rows read into a
/// single shared <see cref="QueryCounters"/> sink. This lets the harness measure the exact read and
/// write work performed by the production services it drives.
/// </summary>
public sealed class CountingConnectionFactory : SqliteConnectionFactory
{
    private readonly Func<bool>? _isUiThread;

    public CountingConnectionFactory(string databasePath) : base(databasePath)
    {
    }

    public CountingConnectionFactory(string databasePath, Func<bool> isUiThread) : base(databasePath)
    {
        _isUiThread = isUiThread;
    }

    public QueryCounters Counters { get; } = new();

    protected override SqliteConnection CreateConnection(string connectionString)
    {
        return _isUiThread is null
            ? new CountingSqliteConnection(connectionString, Counters)
            : new CountingSqliteConnection(connectionString, Counters, _isUiThread);
    }
}
