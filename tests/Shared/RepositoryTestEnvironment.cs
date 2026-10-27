using System.Runtime.CompilerServices;

namespace Patchouli.Testing;

#if PATCHOULI_SHARED_TEST_SUPPORT
public static class RepositoryTestEnvironment
#else
internal static class RepositoryTestEnvironment
#endif
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string TemporaryRoot { get; } = Path.Combine(RepositoryRoot, ".tmp");
    public static string TestRoot { get; } = Path.Combine(TemporaryRoot, "tests");

    // Apply before any fixture or production code creates a temporary file, including child processes.
#if PATCHOULI_SHARED_TEST_SUPPORT
    // F# fixtures access TestRoot before running tests; the support library uses type
    // initialization rather than an application-only module initializer.
    static RepositoryTestEnvironment()
    {
        Initialize();
    }
#else
    [ModuleInitializer]
#endif
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

        SweepStaleTestDirectories();
    }

    /// <summary>
    ///     Collectible-ALC file mappings and external indexers can outlive a test's bounded
    ///     cleanup; once the owning process exits the handles are gone. Removing stale trees
    ///     here keeps <c>.tmp/tests</c> from accumulating leftovers — only entries older than
    ///     the grace window are touched so a concurrently running suite is never disturbed.
    /// </summary>
    private static void SweepStaleTestDirectories()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        foreach (string entry in Directory.EnumerateFileSystemEntries(TestRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(entry) < cutoff.UtcDateTime)
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"Stale test sweep skipped '{entry}': {error.Message}");
            }
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
