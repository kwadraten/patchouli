using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Patchouli.Core.Agents;

/// <summary>
/// Layered external-Agent detection. Layers are attempted in order and the first executable that passes
/// a version probe wins:
/// <list type="number">
///   <item>user override from settings;</item>
///   <item>process <c>PATH</c> (Windows <c>where.exe</c>, Unix <c>which -a</c>);</item>
///   <item>macOS/Linux login-shell resolution (<c>/bin/zsh</c>, <c>/bin/bash</c> or <c>/bin/sh</c>
///   with <c>-lc 'command -v ...'</c>);</item>
///   <item>the catalog's absolute per-OS candidate paths;</item>
///   <item>an install offer, which is information only and never downloads anything.</item>
/// </list>
/// Results are cached for the process lifetime; <see cref="InvalidateCache"/> forces a refresh.
/// </summary>
public sealed class AgentDetectionService
{
    private readonly IProcessRunner _processRunner;
    private readonly IAgentPlatformEnvironment _environment;
    private readonly IAgentInstallOfferProvider? _installOffers;
    private readonly ConcurrentDictionary<string, AgentDetectionResult> _cache = new(StringComparer.Ordinal);
    private TimeSpan _versionProbeTimeout = TimeSpan.FromSeconds(5);

    public AgentDetectionService(
        IProcessRunner processRunner,
        IAgentPlatformEnvironment environment,
        IAgentInstallOfferProvider? installOffers = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(environment);
        _processRunner = processRunner;
        _environment = environment;
        _installOffers = installOffers;
    }

    /// <summary>Timeout for PATH and login-shell resolution. Kept short so settings stay responsive.</summary>
    public TimeSpan ResolutionTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout for one version probe. The contract caps this at five seconds.</summary>
    public TimeSpan VersionProbeTimeout
    {
        get => _versionProbeTimeout;
        init => _versionProbeTimeout = value <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(5)
            : value > MaxVersionProbeTimeout
                ? MaxVersionProbeTimeout
                : value;
    }

    /// <summary>Upper bound for a version probe so a hanging TUI cannot block detection.</summary>
    public static TimeSpan MaxVersionProbeTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Clears the process-lifetime detection cache.</summary>
    public void InvalidateCache()
    {
        _cache.Clear();
    }

    /// <summary>Returns a cached result when present without running a probe.</summary>
    public bool TryGetCached(string agentId, string? customPath, out AgentDetectionResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return false;
        }

        string key = BuildCacheKey(agentId.Trim(), customPath ?? "");
        return _cache.TryGetValue(key, out result);
    }

    /// <summary>Detects one catalog entry, reusing the cache unless <paramref name="forceRefresh"/> is set.</summary>
    public async Task<AgentDetectionResult> DetectAsync(
        AgentCatalogEntry entry,
        AgentIntegrationAppSettings? settings = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AgentIntegrationAppSettings normalized = (settings ?? AgentIntegrationAppSettings.Default()).Normalize();
        string customPath = normalized.FindCustomPath(entry.Id) ?? "";
        string cacheKey = BuildCacheKey(entry.Id, customPath);

        if (forceRefresh)
        {
            InvalidateCache();
        }
        else if (_cache.TryGetValue(cacheKey, out AgentDetectionResult? cached))
        {
            return cached;
        }

        AgentDetectionResult result = await DetectCoreAsync(entry, customPath, cancellationToken)
            .ConfigureAwait(false);
        _cache[cacheKey] = result;
        return result;
    }

    /// <summary>Detects every catalog entry in display order.</summary>
    public async Task<IReadOnlyList<AgentDetectionResult>> DetectAllAsync(
        AgentIntegrationAppSettings? settings = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (forceRefresh)
        {
            InvalidateCache();
        }

        List<AgentDetectionResult> results = [];
        foreach (AgentCatalogEntry entry in AgentCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await DetectAsync(entry, settings, false, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Probes one concrete executable path with its catalog version arguments. The launch service calls
    /// this again before starting a process so a cached detection result is never trusted blindly.
    /// </summary>
    public async Task<AgentCandidate> ProbeAsync(
        AgentCatalogEntry entry,
        string executablePath,
        AgentDetectionSource source = AgentDetectionSource.AbsolutePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string path = NormalizePath(executablePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AgentCandidate(executablePath ?? "", source, AgentCandidateStatus.Missing,
                Detail: "可执行文件路径为空。");
        }

        if (!_environment.FileExists(path))
        {
            return new AgentCandidate(path, source, AgentCandidateStatus.Missing, Detail: "可执行文件不存在。");
        }

        AgentProcessRequest request = BuildVersionProbeRequest(entry, path);
        AgentProcessResult result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Started)
        {
            return new AgentCandidate(path, source, AgentCandidateStatus.Invalid,
                Detail: result.Failure ?? "无法启动可执行文件。");
        }

        if (result.TimedOut)
        {
            return new AgentCandidate(path, source, AgentCandidateStatus.VersionProbeFailed,
                Detail: $"版本探测超时（{VersionProbeTimeout.TotalSeconds:0} 秒）。");
        }

        if (result.ExitCode != 0)
        {
            string stderr = FirstNonEmptyLine(result.StandardError) ?? "";
            return new AgentCandidate(path, source, AgentCandidateStatus.VersionProbeFailed,
                Detail: string.IsNullOrWhiteSpace(stderr)
                    ? $"版本探测退出码为 {result.ExitCode}。"
                    : $"版本探测退出码为 {result.ExitCode}：{TrimDetail(stderr)}");
        }

        string? versionLine = FirstNonEmptyLine(result.StandardOutput) ?? FirstNonEmptyLine(result.StandardError);
        if (string.IsNullOrWhiteSpace(versionLine))
        {
            // Some CLIs exit successfully without printing a version. The executable is usable; the
            // version is simply unknown, so a configured minimum version cannot be checked.
            return new AgentCandidate(path, source, AgentCandidateStatus.Valid, Detail: "版本输出为空。");
        }

        string version = TrimDetail(versionLine);
        if (!string.IsNullOrWhiteSpace(entry.MinimumVersion))
        {
            if (!AgentVersion.TryParse(version, out Version parsedVersion))
            {
                return new AgentCandidate(path, source, AgentCandidateStatus.VersionProbeFailed,
                    Detail: $"无法解析版本号：{version}");
            }

            if (AgentVersion.TryParse(entry.MinimumVersion, out Version minimumVersion) &&
                parsedVersion < minimumVersion)
            {
                return new AgentCandidate(path, source, AgentCandidateStatus.VersionTooOld,
                    version,
                    $"版本 {version} 低于最低要求 {entry.MinimumVersion}。");
            }
        }

        return new AgentCandidate(path, source, AgentCandidateStatus.Valid, version);
    }

    private async Task<AgentDetectionResult> DetectCoreAsync(
        AgentCatalogEntry entry,
        string customPath,
        CancellationToken cancellationToken)
    {
        List<AgentCandidate> candidates = [];
        HashSet<string> seen = new(PathComparer);
        AgentInstallOffer? installOffer = await GetInstallOfferSafeAsync(entry, cancellationToken)
            .ConfigureAwait(false);

        if (entry.Kind != AgentLaunchKind.NativeTui)
        {
            return new AgentDetectionResult(entry, AgentDetectionStatus.Unsupported, null, candidates, installOffer,
                "该 catalog 条目只提供 ACP/stdio 服务，没有可启动的 TUI。");
        }

        if (!string.IsNullOrWhiteSpace(customPath))
        {
            string normalized = NormalizePath(customPath);
            seen.Add(normalized);
            AgentCandidate custom = await ProbeAsync(entry, normalized, AgentDetectionSource.CustomPath,
                cancellationToken).ConfigureAwait(false);
            candidates.Add(custom);
            if (custom.IsValid)
            {
                return new AgentDetectionResult(entry, AgentDetectionStatus.Found, custom, candidates, installOffer);
            }

            AgentDetectionStatus status = custom.Status switch
            {
                AgentCandidateStatus.VersionTooOld => AgentDetectionStatus.VersionTooOld,
                AgentCandidateStatus.Missing => AgentDetectionStatus.InvalidCustomPath,
                _ => AgentDetectionStatus.InvalidExecutable
            };
            return new AgentDetectionResult(entry, status, null, candidates, installOffer,
                custom.Detail ?? "自定义 Agent 路径不可用。");
        }

        AgentCandidate? found = await CollectFromProcessPathAsync(entry, candidates, seen, cancellationToken)
            .ConfigureAwait(false);
        found ??= await CollectFromLoginShellAsync(entry, candidates, seen, cancellationToken).ConfigureAwait(false);
        found ??= await CollectFromAbsolutePathsAsync(entry, candidates, seen, cancellationToken)
            .ConfigureAwait(false);

        return BuildResult(entry, candidates, installOffer, found);
    }

    private async Task<AgentCandidate?> CollectFromProcessPathAsync(
        AgentCatalogEntry entry,
        List<AgentCandidate> candidates,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths = await FindOnProcessPathAsync(entry.CommandName, cancellationToken)
            .ConfigureAwait(false);
        foreach (string path in paths)
        {
            string normalized = NormalizePath(path);
            if (!seen.Add(normalized))
            {
                continue;
            }

            AgentCandidate candidate = await ProbeAsync(entry, normalized, AgentDetectionSource.ProcessPath,
                cancellationToken).ConfigureAwait(false);
            candidates.Add(candidate);
            if (candidate.IsValid)
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<AgentCandidate?> CollectFromLoginShellAsync(
        AgentCatalogEntry entry,
        List<AgentCandidate> candidates,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (_environment.Platform == AgentPlatform.Windows || string.IsNullOrWhiteSpace(entry.CommandName))
        {
            return null;
        }

        string? shell = FindUnixLoginShell();
        if (string.IsNullOrWhiteSpace(shell))
        {
            return null;
        }

        string script = "command -v " + ShellQuote(entry.CommandName.Trim());
        AgentProcessResult result = await _processRunner.RunAsync(
            new AgentProcessRequest(shell, ["-lc", script], Timeout: ResolutionTimeout),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        foreach (string line in SplitLines(result.StandardOutput))
        {
            string path = line.Trim().Trim('"');
            if (!Path.IsPathRooted(path))
            {
                continue;
            }

            string normalized = NormalizePath(path);
            if (!seen.Add(normalized))
            {
                continue;
            }

            AgentCandidate candidate = await ProbeAsync(entry, normalized, AgentDetectionSource.LoginShell,
                cancellationToken).ConfigureAwait(false);
            candidates.Add(candidate);
            if (candidate.IsValid)
            {
                return candidate;
            }
        }

        return null;
    }

    private string? FindUnixLoginShell()
    {
        foreach (string candidate in new[] { "/bin/zsh", "/bin/bash", "/bin/sh" })
        {
            if (_environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<AgentCandidate?> CollectFromAbsolutePathsAsync(
        AgentCatalogEntry entry,
        List<AgentCandidate> candidates,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        foreach (string template in GetAbsoluteCandidates(entry))
        {
            string normalized = NormalizePath(template);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            AgentCandidate candidate = await ProbeAsync(entry, normalized, AgentDetectionSource.AbsolutePath,
                cancellationToken).ConfigureAwait(false);
            candidates.Add(candidate);
            if (candidate.IsValid)
            {
                return candidate;
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetAbsoluteCandidates(AgentCatalogEntry entry)
    {
        return _environment.Platform switch
        {
            AgentPlatform.Windows => entry.WindowsPaths,
            AgentPlatform.MacOS => entry.MacPaths,
            AgentPlatform.Linux => entry.LinuxPaths,
            _ => []
        };
    }

    private async Task<IReadOnlyList<string>> FindOnProcessPathAsync(
        string? commandName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return [];
        }

        string command = commandName.Trim();
        List<string> paths = [];
        if (_environment.Platform == AgentPlatform.Windows)
        {
            AgentProcessResult where = await _processRunner.RunAsync(
                new AgentProcessRequest("where.exe", [command], Timeout: ResolutionTimeout),
                cancellationToken).ConfigureAwait(false);
            AddPathLines(where, paths);
        }
        else
        {
            AgentProcessResult which = await _processRunner.RunAsync(
                new AgentProcessRequest("which", ["-a", command], Timeout: ResolutionTimeout),
                cancellationToken).ConfigureAwait(false);
            AddPathLines(which, paths);
        }

        return Deduplicate(paths);
    }

    private static void AddPathLines(AgentProcessResult result, List<string> paths)
    {
        if (!result.Succeeded)
        {
            return;
        }

        foreach (string line in SplitLines(result.StandardOutput))
        {
            string path = line.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathRooted(path))
            {
                continue;
            }

            paths.Add(path);
        }
    }

    private AgentProcessRequest BuildVersionProbeRequest(AgentCatalogEntry entry, string path)
    {
        IReadOnlyList<string> versionArguments = entry.VersionArguments.Count > 0
            ? entry.VersionArguments
            : ["--version"];
        if (_environment.Platform == AgentPlatform.Windows && IsBatchFile(path))
        {
            string commandProcessor = "cmd.exe";
            List<string> arguments = ["/d", "/s", "/c", "call", path];
            arguments.AddRange(versionArguments);
            return new AgentProcessRequest(commandProcessor, arguments, Timeout: VersionProbeTimeout);
        }

        return new AgentProcessRequest(path, versionArguments, Timeout: VersionProbeTimeout);
    }

    private async Task<AgentInstallOffer?> GetInstallOfferSafeAsync(
        AgentCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        if (_installOffers is null)
        {
            return null;
        }

        try
        {
            return await _installOffers.GetOfferAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or InvalidOperationException)
        {
            Debug.WriteLine($"Agent install offer lookup failed for {entry.Id}: {exception}");
            return null;
        }
    }

    private static AgentDetectionResult BuildResult(
        AgentCatalogEntry entry,
        List<AgentCandidate> candidates,
        AgentInstallOffer? installOffer,
        AgentCandidate? found)
    {
        if (found is not null)
        {
            return new AgentDetectionResult(entry, AgentDetectionStatus.Found, found, candidates, installOffer);
        }

        if (candidates.Count == 0 || candidates.All(candidate => candidate.Status == AgentCandidateStatus.Missing))
        {
            return new AgentDetectionResult(entry, AgentDetectionStatus.NotInstalled, null, candidates, installOffer,
                "未找到可执行文件。");
        }

        AgentCandidate? tooOld = candidates.FirstOrDefault(candidate =>
            candidate.Status == AgentCandidateStatus.VersionTooOld);
        if (tooOld is not null)
        {
            return new AgentDetectionResult(entry, AgentDetectionStatus.VersionTooOld, null, candidates,
                installOffer, tooOld.Detail);
        }

        AgentCandidate failed = candidates.FirstOrDefault(candidate =>
                                    candidate.Status is AgentCandidateStatus.Invalid or
                                        AgentCandidateStatus.VersionProbeFailed) ??
                                candidates[^1];
        return new AgentDetectionResult(entry, AgentDetectionStatus.InvalidExecutable, null, candidates,
            installOffer, failed.Detail ?? "可执行文件验证失败。");
    }

    private string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return _environment.ExpandPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                              PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return path.Trim();
        }
    }

    private IReadOnlyList<string> Deduplicate(IEnumerable<string> paths)
    {
        HashSet<string> seen = new(PathComparer);
        List<string> result = [];
        foreach (string path in paths)
        {
            string normalized = NormalizePath(path);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private StringComparer PathComparer =>
        _environment.Platform is AgentPlatform.Windows or AgentPlatform.MacOS
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static bool IsBatchFile(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCacheKey(string agentId, string customPath)
    {
        return agentId + "\u001f" + customPath;
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static IEnumerable<string> SplitLines(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        foreach (string line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return line;
        }
    }

    private static string? FirstNonEmptyLine(string? value)
    {
        foreach (string line in SplitLines(value))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string TrimDetail(string value)
    {
        StringBuilder builder = new(Math.Min(value.Length, 200));
        foreach (char character in value)
        {
            if (builder.Length >= 200)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }
}

/// <summary>Best-effort semantic version parsing for CLI version output.</summary>
public static class AgentVersion
{
    private static readonly Regex Pattern = new(@"(?<!\d)(\d+(?:\.\d+){0,3})(?!\d)", RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        Match match = Pattern.Match(value ?? "");
        if (!match.Success)
        {
            return false;
        }

        string[] parts = match.Groups[1].Value.Split('.');
        int[] numbers = new int[4];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out numbers[index]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}
