using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

internal static class SqliteTestCleanup
{
    public static void ReleasePools(string databasePath)
    {
        new SqliteConnectionFactory(databasePath).ClearPools();
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
}
