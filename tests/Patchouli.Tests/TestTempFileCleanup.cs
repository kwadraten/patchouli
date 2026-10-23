using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

public static class TestTempFileCleanup
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Deletes one file with bounded, cancellable retries. For a <c>.sqlite</c> path this also
    /// releases that database's pools and removes its -wal/-shm/-journal sidecars. Cleanup is
    /// scoped to the given path: no process-wide pool clearing and no forced GC, so callers must
    /// dispose their owners first and see a visible failure when they do not.
    /// </summary>
    public static void DeleteFileWithRetry(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (IsSqliteDatabasePath(path))
        {
            Retry(() =>
            {
                SqliteTestCleanup.ReleasePools(path);
                new SqliteConnectionFactory(path).DeleteDatabaseFiles();
            }, cancellationToken);
            return;
        }

        Retry(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a directory tree with bounded, cancellable retries. Only pools owning
    /// <c>*.sqlite</c> files under this directory are released before the delete.
    /// </summary>
    public static void DeleteDirectoryWithRetry(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Retry(() =>
        {
            if (Directory.Exists(path))
            {
                SqliteTestCleanup.ReleasePoolsInDirectory(path);
                Directory.Delete(path, true);
            }
        }, cancellationToken);
    }

    private static bool IsSqliteDatabasePath(string path)
    {
        return string.Equals(Path.GetExtension(path), ".sqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static void Retry(Action action, CancellationToken cancellationToken)
    {
        for (int attempt = 0;; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts - 1 &&
                                       (ex is IOException || ex is UnauthorizedAccessException))
            {
                // Wait cancellably between attempts; the final attempt lets the failure escape.
                if (cancellationToken.WaitHandle.WaitOne(RetryDelay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
