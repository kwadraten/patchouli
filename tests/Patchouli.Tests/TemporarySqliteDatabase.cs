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

    public static TemporarySqliteDatabase Create()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"patchouli-{Guid.NewGuid():N}.sqlite");

        return new TemporarySqliteDatabase(path);
    }

    public ValueTask DisposeAsync()
    {
        ConnectionFactory.ClearPools();

        if (File.Exists(Path))
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}
