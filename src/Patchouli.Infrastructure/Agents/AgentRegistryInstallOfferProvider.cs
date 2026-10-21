using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Core.Agents;

namespace Patchouli.Infrastructure.Agents;

/// <summary>
/// Reads agent install metadata from a runtime copy of the Agent Client Protocol registry. The path is
/// injected by the host; a missing or malformed cache simply yields no offer. Nothing is downloaded.
/// </summary>
public sealed class AgentRegistryInstallOfferProvider : IAgentInstallOfferProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string? _registryPath;
    private readonly IAgentPlatformEnvironment _environment;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private RegistryDocument? _document;
    private DateTime _loadedWriteTimeUtc;
    private bool _loaded;

    public AgentRegistryInstallOfferProvider(string? registryPath, IAgentPlatformEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _registryPath = string.IsNullOrWhiteSpace(registryPath) ? null : registryPath;
        _environment = environment;
    }

    public async Task<AgentInstallOffer?> GetOfferAsync(
        AgentCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(entry.Id, "opencode", StringComparison.Ordinal))
        {
            return GetOpenCodeV2Offer(entry);
        }

        RegistryDocument? document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        RegistryAgent? agent = FindAgent(document, entry.Id);
        if (agent is null)
        {
            return null;
        }

        string? platformKey = AgentRegistryPlatformKey.TryGet(_environment.Platform, _environment.Architecture);
        RegistryBinary? binary = null;
        if (!string.IsNullOrWhiteSpace(platformKey) &&
            agent.Distribution?.Binary is { } binaries &&
            binaries.TryGetValue(platformKey, out RegistryBinary? publishedBinary))
        {
            binary = publishedBinary;
        }

        List<AgentInstallPackageOffer> packages = [];
        RegistryPackage? npx = agent.Distribution?.Npx;
        if (npx is not null && !string.IsNullOrWhiteSpace(npx.Package))
        {
            packages.Add(new AgentInstallPackageOffer("npx", npx.Package.Trim(), npx.Args ?? []));
        }

        RegistryPackage? uvx = agent.Distribution?.Uvx;
        if (uvx is not null && !string.IsNullOrWhiteSpace(uvx.Package))
        {
            packages.Add(new AgentInstallPackageOffer("uvx", uvx.Package.Trim(), uvx.Args ?? []));
        }

        if (agent.Distribution is null && packages.Count == 0 && binary is null)
        {
            return null;
        }

        return new AgentInstallOffer(
            entry.Id,
            string.IsNullOrWhiteSpace(agent.Name) ? entry.DisplayName : agent.Name.Trim(),
            agent.Version?.Trim() ?? "",
            platformKey,
            binary?.Archive?.Trim(),
            binary?.Sha256?.Trim(),
            binary?.Cmd?.Trim(),
            binary?.Args ?? [],
            packages,
            agent.Repository?.Trim(),
            agent.License?.Trim(),
            agent.LicenseUrl?.Trim());
    }

    // The ACP registry still distributes V1. Pin the official V2 native npm archives instead;
    // detection deliberately continues to accept either major version from the user's PATH.
    private AgentInstallOffer GetOpenCodeV2Offer(AgentCatalogEntry entry)
    {
        const string version = "2.1.2";
        string? target = (_environment.Platform, _environment.Architecture) switch
        {
            (AgentPlatform.Windows, AgentArchitecture.X64) => "windows-x64-baseline",
            (AgentPlatform.Windows, AgentArchitecture.Arm64) => "windows-arm64",
            (AgentPlatform.MacOS, AgentArchitecture.X64) => "darwin-x64-baseline",
            (AgentPlatform.MacOS, AgentArchitecture.Arm64) => "darwin-arm64",
            (AgentPlatform.Linux, AgentArchitecture.X64) => "linux-x64-baseline",
            (AgentPlatform.Linux, AgentArchitecture.Arm64) => "linux-arm64",
            _ => null
        };
        string? url = target is null
            ? null
            : $"https://registry.npmjs.org/@opencode/cli-{target}/-/cli-{target}-{version}.tgz";
        string command = _environment.Platform == AgentPlatform.Windows
            ? "package/bin/opencode.exe"
            : "package/bin/opencode";
        return new AgentInstallOffer(entry.Id, entry.DisplayName, version,
            AgentRegistryPlatformKey.TryGet(_environment.Platform, _environment.Architecture),
            url, null, command, [],
            [new AgentInstallPackageOffer("npm", $"@opencode/cli@{version}", [])],
            "https://github.com/anomalyco/opencode", "MIT",
            "https://github.com/anomalyco/opencode/blob/dev/LICENSE");
    }

    public async Task<IReadOnlyList<AgentInstallOffer>> GetAllOffersAsync(
        CancellationToken cancellationToken = default)
    {
        List<AgentInstallOffer> offers = [];
        foreach (AgentCatalogEntry entry in AgentCatalog.All)
        {
            AgentInstallOffer? offer = await GetOfferAsync(entry, cancellationToken).ConfigureAwait(false);
            if (offer is not null)
            {
                offers.Add(offer);
            }
        }

        return offers;
    }

    private async Task<RegistryDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_registryPath) || !File.Exists(_registryPath))
        {
            return null;
        }

        DateTime writeTimeUtc;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(_registryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Agent registry timestamp read failed: {exception}");
            return null;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded && _document is not null && writeTimeUtc <= _loadedWriteTimeUtc)
            {
                return _document;
            }

            string json = await File.ReadAllTextAsync(_registryPath, cancellationToken).ConfigureAwait(false);
            _document = JsonSerializer.Deserialize<RegistryDocument>(json, JsonOptions);
            _loadedWriteTimeUtc = writeTimeUtc;
            _loaded = true;
            return _document;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Agent registry load failed: {exception}");
            _loadedWriteTimeUtc = writeTimeUtc;
            _loaded = true;
            _document = null;
            return null;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static RegistryAgent? FindAgent(RegistryDocument? document, string agentId)
    {
        if (document?.Agents is not { Count: > 0 } agents)
        {
            return null;
        }

        return agents.FirstOrDefault(agent =>
                   !string.IsNullOrWhiteSpace(agent.Id) &&
                   string.Equals(agent.Id.Trim(), agentId, StringComparison.Ordinal)) ??
               agents.FirstOrDefault(agent =>
                   !string.IsNullOrWhiteSpace(agent.Id) &&
                   string.Equals(agent.Id.Trim(), agentId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RegistryDocument
    {
        public string? Version { get; set; }

        public List<RegistryAgent>? Agents { get; set; }
    }

    private sealed class RegistryAgent
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Repository { get; set; }
        public string? License { get; set; }

        [JsonPropertyName("license_url")] public string? LicenseUrl { get; set; }

        public RegistryDistribution? Distribution { get; set; }
    }

    private sealed class RegistryDistribution
    {
        public RegistryPackage? Npx { get; set; }
        public RegistryPackage? Uvx { get; set; }
        public Dictionary<string, RegistryBinary>? Binary { get; set; }
    }

    private sealed class RegistryPackage
    {
        public string? Package { get; set; }
        public List<string>? Args { get; set; }
    }

    private sealed class RegistryBinary
    {
        public string? Archive { get; set; }
        public string? Sha256 { get; set; }
        public string? Cmd { get; set; }
        public List<string>? Args { get; set; }
    }
}
