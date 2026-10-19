namespace Patchouli.Core.Agents;

/// <summary>Host platform used for catalog paths and registry platform keys.</summary>
public enum AgentPlatform
{
    Windows,
    MacOS,
    Linux
}

/// <summary>Process architecture used to select the registry binary distribution.</summary>
public enum AgentArchitecture
{
    X64,
    Arm64,
    X86,
    Arm
}

/// <summary>
/// One child process invocation. Process execution is abstracted so detection and launch validation can
/// be exercised without starting a real agent binary.
/// </summary>
/// <param name="FileName">Executable or shell to start.</param>
/// <param name="Arguments">Arguments passed verbatim (no shell interpretation).</param>
/// <param name="WorkingDirectory">Optional working directory; null inherits the current directory.</param>
/// <param name="Environment">Optional environment overrides; a null value removes the variable.</param>
/// <param name="Timeout">Optional timeout; callers apply a short default for version probes.</param>
public sealed record AgentProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null);

/// <summary>Result of one child process invocation. Output is captured as text.</summary>
public sealed record AgentProcessResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Failure)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    public static AgentProcessResult NotStarted(string failure)
    {
        return new AgentProcessResult(false, false, -1, string.Empty, string.Empty, failure);
    }
}

/// <summary>Runs one child process, capturing stdout, stderr and the exit code.</summary>
public interface IProcessRunner
{
    Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// OS, architecture, home directory, well-known directories and path expansion used by Agent detection.
/// Tests inject a fake implementation instead of reading the real machine.
/// </summary>
public interface IAgentPlatformEnvironment
{
    AgentPlatform Platform { get; }
    AgentArchitecture Architecture { get; }
    string HomeDirectory { get; }

    /// <summary>
    /// Resolves a well-known directory name (<c>USERPROFILE</c>, <c>HOME</c>, <c>LOCALAPPDATA</c>,
    /// <c>APPDATA</c>, <c>PROGRAMFILES</c>, <c>ProgramFiles(x86)</c>) to an absolute path. Unknown
    /// names return <see langword="false"/> so path expansion leaves the token literal.
    /// </summary>
    bool TryResolveWellKnown(string name, out string? value);

    /// <summary>Expands <c>%NAME%</c>, <c>$NAME</c> and a leading <c>~</c>, then returns a full path.</summary>
    string ExpandPath(string path);

    bool FileExists(string path);
}
