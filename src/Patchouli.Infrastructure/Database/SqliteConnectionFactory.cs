using Dapper;
using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Database;

public class SqliteConnectionFactory
{
    static SqliteConnectionFactory()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private readonly string _databasePath;
    private readonly string _generalConnectionString;
    private readonly string _readConnectionString;
    private readonly string _adminConnectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        _databasePath = databasePath;
        _generalConnectionString = BuildConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate, true);
        _readConnectionString = BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly, true);
        _adminConnectionString = BuildConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate, false);
    }

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Releases pooled connections for this database without affecting pools for other database files.
    /// Call before a workflow replaces, moves, or deletes this database file.
    /// </summary>
    public void ClearPools()
    {
        using SqliteConnection general = new(_generalConnectionString);
        using SqliteConnection read = new(_readConnectionString);
        SqliteConnection.ClearPool(general);
        SqliteConnection.ClearPool(read);
    }

    /// <summary>
    /// Releases this database's pools and deletes its main file and SQLite sidecars.
    /// </summary>
    public void DeleteDatabaseFiles()
    {
        ClearPools();
        foreach (string path in new[]
                     { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm", $"{_databasePath}-journal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Acquires the process-wide workflow gate for this database file. Long-running workflows
    /// (snapshot, migration, recovery) serialize on this lease.
    /// </summary>
    public Task<IDisposable> EnterExclusiveAsync(CancellationToken cancellationToken = default)
    {
        return SqliteDatabaseExecutionGate.EnterAsync(_databasePath, cancellationToken);
    }

    /// <summary>
    /// Acquires the process-wide single-writer lease for this database file. The host write
    /// service uses this so every Library has exactly one worker writing at a time.
    /// </summary>
    public Task<IDisposable> EnterWriteAsync(CancellationToken cancellationToken = default)
    {
        return SqliteDatabaseExecutionGate.EnterAsync(_databasePath, cancellationToken);
    }

    /// <summary>
    /// The general read/write connection. Connection strings are distinguishable by mode, so
    /// when pooling is enabled a read-only pooled connection can never be borrowed by a writer
    /// and <c>PRAGMA query_only</c> state cannot leak across the boundary.
    /// </summary>
    public virtual SqliteConnection CreateConnection()
    {
        return CreateConfiguredConnection(_generalConnectionString, false);
    }

    /// <summary>
    /// A read-only connection. SQLite enforces read-only at the file level
    /// (<see cref="SqliteOpenMode.ReadOnly"/>), which is stronger than a per-connection
    /// <c>PRAGMA query_only</c> and cannot be used for writes.
    /// </summary>
    public SqliteConnection CreateReadConnection()
    {
        return CreateConfiguredConnection(_readConnectionString, true);
    }

    /// <summary>
    /// A read-write connection for the single-writer host write service.
    /// </summary>
    public SqliteConnection CreateWriteConnection()
    {
        return CreateConfiguredConnection(_generalConnectionString, false);
    }

    /// <summary>
    /// A non-pooled, exclusive management connection used for WAL bootstrap, migrations,
    /// checkpoints, and snapshot maintenance. PRAGMA state on it never leaks into shared
    /// connections and it is never handed to a reader or writer.
    /// </summary>
    public SqliteConnection CreateAdminConnection()
    {
        return CreateConfiguredConnection(_adminConnectionString, false);
    }

    /// <summary>
    /// The single creation hook behind every connection entry point. Subclasses (such as the
    /// performance harness's counting factory) wrap connections here; the mode in the connection
    /// string is never altered, so a read-only request cannot gain write capability.
    /// </summary>
    protected virtual SqliteConnection CreateConnection(string connectionString)
    {
        return new SqliteConnection(connectionString);
    }

    private SqliteConnection CreateConfiguredConnection(string connectionString, bool readOnly)
    {
        SqliteConnection connection = CreateConnection(connectionString);
        connection.StateChange += (_, eventArgs) =>
        {
            if (eventArgs.CurrentState != System.Data.ConnectionState.Open)
            {
                return;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = readOnly
                ? "pragma busy_timeout = 30000; pragma synchronous = normal; pragma query_only = on;"
                : "pragma busy_timeout = 30000; pragma synchronous = normal; pragma query_only = off;";
            command.ExecuteNonQuery();
        };
        return connection;
    }

    private static string BuildConnectionString(string databasePath, SqliteOpenMode mode, bool pooling)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = mode,
            // Keep SQLite's page cache private to each physical connection. A shared SQLite cache
            // can retain the first connection's read-only open mode across otherwise distinct
            // Microsoft.Data.Sqlite pools, making a subsequent writer fail with SQLITE_READONLY.
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            ForeignKeys = true,
            Pooling = pooling
        };

        return builder.ToString();
    }
}
