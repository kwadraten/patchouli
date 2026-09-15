using Microsoft.Data.Sqlite;

namespace Patchouli.Tests;

public static class TestTempFileCleanup
{
    public static void DeleteFileWithRetry(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (attempt < 9 && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(50);
            }
        }
    }

    public static void DeleteDirectoryWithRetry(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 9 && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(50);
            }
        }
    }
}
