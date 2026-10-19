using Patchouli.Core.Agents;
using Patchouli.Infrastructure.Agents;

namespace Patchouli.Tests;

/// <summary>
/// In-memory platform environment for Agent detection tests. It never reads the real machine: the
/// well-known directory map, home directory and existing-file set are supplied by the test.
/// </summary>
internal sealed class FakeAgentPlatformEnvironment : IAgentPlatformEnvironment
{
    public AgentPlatform Platform { get; set; } = AgentPlatform.Windows;

    public AgentArchitecture Architecture { get; set; } = AgentArchitecture.X64;

    public string HomeDirectory { get; set; } = AppContext.BaseDirectory;

    public Dictionary<string, string> WellKnownDirectories { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryResolveWellKnown(string name, out string? value)
    {
        value = null;
        return !string.IsNullOrWhiteSpace(name) &&
               WellKnownDirectories.TryGetValue(name.Trim(), out value);
    }

    public string ExpandPath(string path)
    {
        return AgentPathExpansion.Expand(path, this);
    }

    public bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && ExistingFiles.Contains(path);
    }
}

/// <summary>
/// Records every process request and answers it with a test-supplied handler. Using this instead of
/// <see cref="SystemProcessRunner"/> keeps detection and launch tests deterministic and fast.
/// </summary>
internal sealed class ScriptedProcessRunner : IProcessRunner
{
    public List<AgentProcessRequest> Requests { get; } = [];

    public Func<AgentProcessRequest, AgentProcessResult> Handler { get; set; } =
        _ => AgentProcessResult.NotStarted("no scripted response");

    public int CallCount => Requests.Count;

    public IReadOnlyList<string> FileNames => Requests.Select(request => request.FileName).ToArray();

    public Task<AgentProcessResult> RunAsync(
        AgentProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Handler(request));
    }

    public static AgentProcessResult Output(string standardOutput, int exitCode = 0)
    {
        return new AgentProcessResult(true, false, exitCode, standardOutput, string.Empty, null);
    }

    public static AgentProcessResult OutputWithError(string standardOutput, string standardError,
        int exitCode = 0)
    {
        return new AgentProcessResult(true, false, exitCode, standardOutput, standardError, null);
    }

    public static AgentProcessResult Failure(int exitCode, string standardError)
    {
        return new AgentProcessResult(true, false, exitCode, string.Empty, standardError, null);
    }

    public static AgentProcessResult TimedOut(string standardOutput = "")
    {
        return new AgentProcessResult(true, true, -1, standardOutput, string.Empty, "timed out");
    }

    public static AgentProcessResult NotStarted(string failure)
    {
        return AgentProcessResult.NotStarted(failure);
    }
}

internal sealed class FakeInstallOfferProvider : IAgentInstallOfferProvider
{
    public AgentInstallOffer? Offer { get; set; }

    public Exception? Exception { get; set; }

    public int CallCount { get; private set; }

    public Task<AgentInstallOffer?> GetOfferAsync(AgentCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.FromResult(Offer);
    }

    public Task<IReadOnlyList<AgentInstallOffer>> GetAllOffersAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<AgentInstallOffer>>(Offer is null ? [] : [Offer]);
    }
}

/// <summary>Workspace locator whose directory and failure mode are controlled by the test.</summary>
internal sealed class FakeAgentWorkspaceLocator : IAgentWorkspaceLocator
{
    public string Directory { get; set; } = AppContext.BaseDirectory;

    public Exception? Failure { get; set; }

    public int GetCalls { get; private set; }

    public int EnsureCalls { get; private set; }

    public string GetWorkspaceDirectory()
    {
        GetCalls++;
        return Directory;
    }

    public string EnsureWorkspaceDirectory()
    {
        EnsureCalls++;
        if (Failure is not null)
        {
            throw Failure;
        }

        System.IO.Directory.CreateDirectory(Directory);
        return Directory;
    }
}

/// <summary>One isolated directory under the test temporary root.</summary>
internal sealed class AgentTestDirectory : IDisposable
{
    public AgentTestDirectory()
    {
        Path = Directory.CreateDirectory(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agent-test-{Guid.NewGuid():N}"))
            .FullName;
    }

    public string Path { get; }

    public string Combine(params string[] segments)
    {
        return System.IO.Path.Combine([Path, .. segments]);
    }

    public string CreateFile(string relativePath, string content = "")
    {
        string path = Combine(relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(Path);
    }
}

internal static class AgentTestCatalog
{
    public static AgentCatalogEntry OpenCode => AgentCatalog.Find("opencode")!;

    public static AgentCatalogEntry Claude => AgentCatalog.Find("claude-acp")!;

    public static AgentCatalogEntry Create(
        string id = "test-agent",
        AgentLaunchKind kind = AgentLaunchKind.NativeTui,
        string commandName = "test-agent",
        IReadOnlyList<string>? windowsPaths = null,
        IReadOnlyList<string>? macPaths = null,
        IReadOnlyList<string>? linuxPaths = null,
        IReadOnlyList<string>? versionArguments = null,
        string? minimumVersion = null)
    {
        return new AgentCatalogEntry(
            id,
            id,
            "test entry",
            kind,
            commandName,
            [],
            windowsPaths ?? [],
            macPaths ?? [],
            linuxPaths ?? [],
            versionArguments ?? ["--version"],
            minimumVersion);
    }
}
