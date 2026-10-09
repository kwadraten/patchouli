using System.Runtime.CompilerServices;

namespace Patchouli.Testing;

internal static class RepositoryTestEnvironment
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string TemporaryRoot { get; } = Path.Combine(RepositoryRoot, ".tmp");
    public static string TestRoot { get; } = Path.Combine(TemporaryRoot, "tests");

    // Apply before any fixture or production code creates a temporary file, including child processes.
    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(TestRoot);
        string path = TestRoot + Path.DirectorySeparatorChar;
        foreach (string variable in new[] { "TEMP", "TMP", "TMPDIR", "SystemTemp" })
        {
            Environment.SetEnvironmentVariable(variable, path);
        }

        if (!IsTemporaryPath(Path.GetTempPath()))
        {
            throw new InvalidOperationException("The test process must use the repository .tmp directory.");
        }
    }

    public static bool IsTemporaryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), TemporaryRoot, comparison)
               || fullPath.StartsWith(TemporaryRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Patchouli.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate Patchouli.sln for the test temporary directory.");
    }
}
