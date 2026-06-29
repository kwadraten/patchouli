namespace LiteratureApp.UI;

public sealed record AppRuntimeOptions(string RuntimeDatabasePath, string DefaultSyncRoot, string DefaultStagingRoot, string LogDirectory, bool UseMockOcrOnly = true)
{
    public static AppRuntimeOptions FromEnvironment()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteratureApp");
        return new(
            Environment.GetEnvironmentVariable("LITERATUREAPP_DB") ?? Path.Combine(root, "literatureapp-runtime.sqlite"),
            Environment.GetEnvironmentVariable("LITERATUREAPP_SYNC_ROOT") ?? Path.Combine(root, "sync"),
            Environment.GetEnvironmentVariable("LITERATUREAPP_STAGING_ROOT") ?? Path.Combine(root, "staging"),
            Environment.GetEnvironmentVariable("LITERATUREAPP_LOG_DIR") ?? Path.Combine(root, "logs"));
    }
}
