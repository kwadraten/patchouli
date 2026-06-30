namespace Patchouli.UI;

public sealed record AppRuntimeOptions(string RuntimeDatabasePath, string DefaultSyncRoot, string DefaultStagingRoot, string LogDirectory, bool UseMockOcrOnly = true)
{
    public static AppRuntimeOptions FromEnvironment()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Patchouli");
        return new(
            Environment.GetEnvironmentVariable("PATCHOULI_DB") ?? Path.Combine(root, "patchouli-runtime.sqlite"),
            Environment.GetEnvironmentVariable("PATCHOULI_SYNC_ROOT") ?? Path.Combine(root, "sync"),
            Environment.GetEnvironmentVariable("PATCHOULI_STAGING_ROOT") ?? Path.Combine(root, "staging"),
            Environment.GetEnvironmentVariable("PATCHOULI_LOG_DIR") ?? Path.Combine(root, "logs"));
    }
}
