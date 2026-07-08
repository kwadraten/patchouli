namespace Patchouli.Infrastructure.Csl;

internal static class CslStoragePaths
{
    public static string GetStylesRoot(string databasePath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullDatabasePath)
            ?? throw new InvalidOperationException("Database path must resolve to a parent directory.");
        var databaseName = Path.GetFileNameWithoutExtension(fullDatabasePath);
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            databaseName = "patchouli-runtime";
        }

        return Path.Combine(directory, $"{databaseName}.csl-styles");
    }
}
