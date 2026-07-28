using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Database;

public sealed class SqliteConnectionFactory
{
    private readonly string _databasePath;

    public SqliteConnectionFactory(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        _databasePath = databasePath;
    }

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Acquires the process-wide workflow gate for this database file.
    /// </summary>
    public Task<IDisposable> EnterExclusiveAsync(CancellationToken cancellationToken = default)
    {
        return SqliteDatabaseExecutionGate.EnterAsync(_databasePath, cancellationToken);
    }

    public SqliteConnection CreateConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            ForeignKeys = true,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }
}
