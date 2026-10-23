using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

internal sealed class TemporarySqliteDatabase : IAsyncDisposable
{
    private TemporarySqliteDatabase(string path)
    {
        Path = path;
        ConnectionFactory = new SqliteConnectionFactory(path);
    }

    public string Path { get; }
    public SqliteConnectionFactory ConnectionFactory { get; }

    /// <summary>
    /// Creates a database at a fresh path with nothing in it. This stays the entry point for
    /// migration, WAL, recovery, empty-database-start and lock tests that must observe a real
    /// empty file.
    /// </summary>
    public static TemporarySqliteDatabase Create()
    {
        return new TemporarySqliteDatabase(NewDatabasePath());
    }

    /// <summary>
    /// Creates this case's own writable copy of the process-wide migrated template: the real
    /// migration runs once per testhost while every case still gets an independent path and
    /// connection pools. Use <see cref="Create"/> instead wherever the test must start from an
    /// empty database.
    /// </summary>
    public static async Task<TemporarySqliteDatabase> CreateMigratedAsync(
        CancellationToken cancellationToken = default)
    {
        string path = NewDatabasePath();
        try
        {
            await MigratedDatabaseTemplate.Shared.CreateCopyAsync(path, cancellationToken);
            return new TemporarySqliteDatabase(path);
        }
        catch
        {
            // The same ownership boundary as DisposeAsync: release this file's pools and delete the
            // main file plus sidecars. The destination may be absent, partial, or still carry the
            // template's read-only attribute — all of these are handled here.
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            SqliteTestCleanup.ReleasePools(path);
            new SqliteConnectionFactory(path).DeleteDatabaseFiles();
            throw;
        }
    }

    private static string NewDatabasePath()
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"patchouli-{Guid.NewGuid():N}.sqlite");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            // Release only this database's pools (factory pools and any raw "Data Source=..."
            // pool), then delete the main file and its -wal/-shm/-journal sidecars through the
            // shared factory path. Deletion is not retried or swallowed: a still-open owner keeps
            // the file locked and the failure below names the fixture and the path.
            SqliteTestCleanup.ReleasePools(Path);
            ConnectionFactory.DeleteDatabaseFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"TemporarySqliteDatabase failed to delete '{Path}' and its SQLite sidecars; " +
                "dispose every service and connection using this database before the fixture.", ex);
        }

        return ValueTask.CompletedTask;
    }
}
