namespace Patchouli.Core.Agents;

/// <summary>Where a candidate executable came from, in detection priority order.</summary>
public enum AgentDetectionSource
{
    /// <summary>User override from <see cref="AgentIntegrationAppSettings.CustomPaths"/>.</summary>
    CustomPath,

    /// <summary>Resolved through the process <c>PATH</c>.</summary>
    ProcessPath,

    /// <summary>Resolved by the user's login shell (<c>$SHELL -lc 'command -v ...'</c>).</summary>
    LoginShell,

    /// <summary>One of the catalog's absolute per-OS candidates.</summary>
    AbsolutePath
}

/// <summary>Outcome of probing one executable candidate.</summary>
public enum AgentCandidateStatus
{
    Valid,
    Missing,
    Invalid,
    VersionProbeFailed,
    VersionTooOld
}

/// <summary>Overall outcome of detecting one catalog agent.</summary>
public enum AgentDetectionStatus
{
    Found,
    NotInstalled,
    InvalidExecutable,
    InvalidCustomPath,
    VersionTooOld,
    Unsupported
}

/// <summary>One probed candidate path and the version string it reported.</summary>
public sealed record AgentCandidate(
    string Path,
    AgentDetectionSource Source,
    AgentCandidateStatus Status,
    string? Version = null,
    string? Detail = null)
{
    public bool IsValid => Status == AgentCandidateStatus.Valid;
}

/// <summary>
/// Layered detection result for one catalog entry. <see cref="InstallOffer"/> is populated independently
/// of installation: detection never downloads or modifies the machine.
/// </summary>
public sealed record AgentDetectionResult(
    AgentCatalogEntry Entry,
    AgentDetectionStatus Status,
    AgentCandidate? Selected,
    IReadOnlyList<AgentCandidate> Candidates,
    AgentInstallOffer? InstallOffer = null,
    string? ErrorMessage = null)
{
    public bool IsFound => Status == AgentDetectionStatus.Found && Selected is not null;

    public string? ExecutablePath => Selected?.Path;

    public string? Version => Selected?.Version;

    public bool IsInstallable => InstallOffer?.IsInstallable == true;
}
