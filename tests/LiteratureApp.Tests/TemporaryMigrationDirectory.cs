namespace LiteratureApp.Tests;

internal sealed class TemporaryMigrationDirectory : IDisposable
{
    private TemporaryMigrationDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryMigrationDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"literatureapp-migrations-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);
        return new TemporaryMigrationDirectory(path);
    }

    public void Write(string fileName, string sql)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, fileName), sql);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
