namespace Patchouli.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string MigrationsDirectory { get; } =
        FromRepositoryRoot("src", "Patchouli.Infrastructure", "migrations");

    public static string FromRepositoryRoot(params string[] segments)
    {
        return Path.Combine(new[] { RepositoryRoot }.Concat(segments).ToArray());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

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
