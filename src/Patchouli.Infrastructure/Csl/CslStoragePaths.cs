namespace Patchouli.Infrastructure.Csl;

internal static class CslStoragePaths
{
    public static string GetStylesRoot(string databasePath)
    {
        string fullDatabasePath = Path.GetFullPath(databasePath);
        string directory = Path.GetDirectoryName(fullDatabasePath)
                           ?? throw new InvalidOperationException("Database path must resolve to a parent directory.");
        string databaseName = Path.GetFileNameWithoutExtension(fullDatabasePath);
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            databaseName = "patchouli-runtime";
        }

        return Path.Combine(directory, $"{databaseName}.csl-styles");
    }
}
