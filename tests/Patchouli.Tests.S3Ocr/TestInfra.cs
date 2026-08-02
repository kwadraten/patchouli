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
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        foreach (string candidate in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return ValueTask.CompletedTask;
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
