using LiteratureApp.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace LiteratureApp.Tests;

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
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"literatureapp-{Guid.NewGuid():N}.sqlite");

        return new TemporarySqliteDatabase(path);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (File.Exists(Path))
        {
            try { File.Delete(Path); }
            catch { }
        }

        return ValueTask.CompletedTask;
    }
}
