namespace Patchouli.Tests;

internal sealed class TemporaryMigrationDirectory : IDisposable
{
    private TemporaryMigrationDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryMigrationDirectory Create()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"patchouli-migrations-{Guid.NewGuid():N}");

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
            Directory.Delete(Path, true);
        }
    }
}
