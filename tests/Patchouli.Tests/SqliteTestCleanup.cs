using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

internal static class SqliteTestCleanup
{
    /// <summary>
    /// Releases every pooled connection string tests can form for this one database file:
    /// the factory's general/read pools plus the default pool produced by a raw
    /// <c>new SqliteConnection("Data Source=...")</c>. Pools for other database files are
    /// never touched, so clearing one fixture cannot disturb a live database elsewhere.
    /// </summary>
    public static void ReleasePools(string databasePath)
    {
        new SqliteConnectionFactory(databasePath).ClearPools();
        ClearPool(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
    }

    public static void ReleasePoolsInDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string databasePath in Directory.EnumerateFiles(directory, "*.sqlite", SearchOption.AllDirectories))
        {
            ReleasePools(databasePath);
        }
    }

    private static void ClearPool(string connectionString)
    {
        using SqliteConnection connection = new(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
