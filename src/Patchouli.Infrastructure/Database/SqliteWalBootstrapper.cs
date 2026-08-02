using Dapper;
using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Database;

/// <summary>
/// Bootstraps the runtime database into WAL journal mode before ordinary migrations run. It
/// uses a dedicated, non-pooled admin connection (never a pooled general connection) and must
/// be called before any migration transaction so the PRAGMA is never part of a migration unit.
/// Failure to reach WAL mode prevents the Library from opening with a diagnostic error.
/// </summary>
public sealed class SqliteWalBootstrapper
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteWalBootstrapper(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task EnableWalAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateAdminConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync("pragma busy_timeout = 30000;");
            await EnableWalCoreAsync(connection);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.sqlite-wal-bootstrap"))
        {
            throw new SqliteWalBootstrapFailedException(
                $"The runtime database could not be switched to WAL journal mode: {exception.Message}");
        }
    }

    private static async Task EnableWalCoreAsync(SqliteConnection connection)
    {
        string current = await connection.ExecuteScalarAsync<string>("pragma journal_mode;") ?? "";
        if (string.Equals(current, "wal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? journalMode = await connection.ExecuteScalarAsync<string>("pragma journal_mode = wal;");
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteWalBootstrapFailedException(
                $"PRAGMA journal_mode=WAL reported '{journalMode ?? "null"}' instead of 'wal'.");
        }
    }
}

public sealed class SqliteWalBootstrapFailedException : Exception
{
    public SqliteWalBootstrapFailedException(string message) : base(message)
    {
    }
}
