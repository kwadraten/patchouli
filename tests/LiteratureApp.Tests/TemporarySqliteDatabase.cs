using LiteratureApp.Infrastructure.Database;

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
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        return ValueTask.CompletedTask;
    }
}
