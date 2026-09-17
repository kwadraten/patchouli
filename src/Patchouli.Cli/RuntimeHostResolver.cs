using System.ComponentModel;
using System.Diagnostics;
using Patchouli.Host.Lifecycle;
using Patchouli.UI;

namespace Patchouli.Cli;

internal static class RuntimeHostResolver
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    public static async Task<McpHttpClient> ConnectAsync(
        string? databasePathOverride,
        string? tokenOverride,
        CancellationToken cancellationToken = default)
    {
        Stopwatch wait = Stopwatch.StartNew();
        CliUnavailableException? lastFailure = null;
        do
        {
            (string endpoint, string? token) = await DiscoverOrLaunchAsync(
                databasePathOverride, tokenOverride, cancellationToken);
            McpHttpClient client = new(endpoint, token);
            try
            {
                await client.InitializeAsync(cancellationToken);
                return client;
            }
            catch (CliUnavailableException exception)
            {
                client.Dispose();
                lastFailure = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        } while (wait.Elapsed < StartupTimeout);

        throw new CliUnavailableException(
            $"the discovered runtime host did not accept an MCP connection: {lastFailure?.Detail ?? "unknown failure"}");
    }

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

        using Process startedHost = StartHeadlessHost(databasePath);
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record = await RuntimeHostCoordinator.ReadAsync(databasePath, cancellationToken: cancellationToken);
            if (record is not null)
            {
                return (record.Endpoint, token);
            }

            if (startedHost.HasExited && startedHost.ExitCode != CliExitCode.Unavailable)
            {
                throw new CliUnavailableException(
                    $"the headless runtime host exited before becoming ready (exit code {startedHost.ExitCode}). " +
                    "Review the Patchouli host log for startup details.");
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
        try
        {
            return Process.Start(start) ??
                   throw new CliUnavailableException("the headless runtime host could not start.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new CliUnavailableException("the headless runtime host process could not be launched.", exception);
        }
    }

    internal static (string Executable, string? ManagedAssembly) ResolveHostExecutable(
        string? baseDirectory = null,
        string? processPath = null)
    {
        string effectiveBaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        string[] roots = HostSearchRoots(effectiveBaseDirectory, processPath ??
                                                                 (baseDirectory is null
                                                                     ? Environment.ProcessPath
                                                                     : null)).ToArray();

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

    internal static IReadOnlyList<string> HostSearchRoots(string baseDirectory, string? processPath)
    {
        List<string> roots = [];
        AddRoot(baseDirectory);
        AddRoot(Directory.GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName);

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                FileInfo processFile = new(Path.GetFullPath(processPath));
                FileSystemInfo? resolvedTarget = processFile.ResolveLinkTarget(true);
                AddRoot(resolvedTarget is DirectoryInfo targetDirectory
                    ? targetDirectory.FullName
                    : Path.GetDirectoryName(resolvedTarget?.FullName ?? processFile.FullName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  PlatformNotSupportedException)
            {
                AddRoot(Path.GetDirectoryName(processPath));
            }
        }

        DirectoryInfo outputDirectory = new(Path.GetFullPath(baseDirectory));
        DirectoryInfo? configurationDirectory = outputDirectory.Parent;
        DirectoryInfo? binDirectory = configurationDirectory?.Parent;
        DirectoryInfo? cliProjectDirectory = binDirectory?.Parent;
        DirectoryInfo? sourceDirectory = cliProjectDirectory?.Parent;
        if (configurationDirectory is not null &&
            string.Equals(binDirectory?.Name, "bin", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cliProjectDirectory?.Name, "Patchouli.Cli", StringComparison.OrdinalIgnoreCase) &&
            sourceDirectory is not null)
        {
            AddRoot(Path.Combine(sourceDirectory.FullName, "Patchouli.UI", "bin", configurationDirectory.Name,
                outputDirectory.Name));
        }

        return roots;

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath = Path.GetFullPath(path);
            if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(fullPath);
            }
        }
    }
}
