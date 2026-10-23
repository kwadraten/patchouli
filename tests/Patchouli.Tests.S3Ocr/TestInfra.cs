using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests.S3Ocr;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string MigrationsDirectory { get; } =
        Path.Combine(RepositoryRoot, "src", "Patchouli.Infrastructure", "migrations");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Patchouli.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Patchouli.sln from the test output directory.");
    }
}

internal sealed class TemporarySqliteDatabase : IAsyncDisposable
{
    private TemporarySqliteDatabase(string path)
    {
        Path = path;
        ConnectionFactory = new SqliteConnectionFactory(path);
    }

    public string Path { get; }
    public SqliteConnectionFactory ConnectionFactory { get; }

    public static TemporarySqliteDatabase Create()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"patchouli-s3-{Guid.NewGuid():N}.sqlite");

        return new TemporarySqliteDatabase(path);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            // Owners (OcrPerfContext connections) are disposed first; here we only release the
            // pools that can own this file and delete it with its -wal/-shm/-journal sidecars.
            // No process-wide ClearAllPools or forced GC; a still-open owner makes the delete
            // fail visibly instead of leaving orphan sidecars.
            ReleaseOwnedPools();
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

    private void ReleaseOwnedPools()
    {
        // The factory's general/read pools plus the default pool a raw
        // new SqliteConnection("Data Source=...") would form for this path.
        ConnectionFactory.ClearPools();
        using SqliteConnection raw = new(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        SqliteConnection.ClearPool(raw);
    }
}

internal sealed class FixedClock : Patchouli.Core.Time.IClock
{
    public FixedClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }
}
