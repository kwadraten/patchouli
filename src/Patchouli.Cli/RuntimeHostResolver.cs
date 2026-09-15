using System.Diagnostics;
using Patchouli.Host.Lifecycle;
using Patchouli.UI;

namespace Patchouli.Cli;

internal static class RuntimeHostResolver
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    public static async Task<(string Endpoint, string? Token)> DiscoverOrLaunchAsync(
        string? databasePathOverride,
        string? tokenOverride,
        CancellationToken cancellationToken = default)
    {
        PatchouliAppSettings settings = PatchouliAppSettings.Load();
        string databasePath = databasePathOverride ?? settings.Runtime.RuntimeDatabasePath;
        string? token = tokenOverride ?? settings.Mcp.Token;

        RuntimeHostDiscoveryRecord? record = await RuntimeHostCoordinator.ReadAsync(
            databasePath, cancellationToken: cancellationToken);
        if (record is not null)
        {
            return (record.Endpoint, token);
        }

        StartHeadlessHost(databasePath);
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record = await RuntimeHostCoordinator.ReadAsync(databasePath, cancellationToken: cancellationToken);
            if (record is not null)
            {
                return (record.Endpoint, token);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new CliUnavailableException("no runtime host became ready for the selected Library.");
    }

    internal static Process StartHeadlessHost(string databasePath)
    {
        (string executable, string? managedAssembly) = ResolveHostExecutable();
        ProcessStartInfo start = new(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (managedAssembly is not null)
        {
            start.ArgumentList.Add(managedAssembly);
        }

        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--db");
        start.ArgumentList.Add(Path.GetFullPath(databasePath));
        return Process.Start(start) ?? throw new CliUnavailableException("the headless runtime host could not start.");
    }

    internal static (string Executable, string? ManagedAssembly) ResolveHostExecutable()
    {
        string[] roots = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
        }.Where(path => path is not null).Select(path => path!).ToArray();

        List<string> candidates = [];
        string appHostName = OperatingSystem.IsWindows() ? "Patchouli.UI.exe" : "Patchouli.UI";
        foreach (string root in roots)
        {
            candidates.Add(Path.Combine(root, appHostName));
        }

        string? appHost = candidates.FirstOrDefault(File.Exists);
        if (appHost is not null)
        {
            return (appHost, null);
        }

        foreach (string root in roots)
        {
            string assembly = Path.Combine(root, "Patchouli.UI.dll");
            if (File.Exists(assembly))
            {
                return ("dotnet", assembly);
            }
        }

        throw new CliUnavailableException(
            "the Patchouli desktop executable was not found; reinstall Patchouli.");
    }
}
