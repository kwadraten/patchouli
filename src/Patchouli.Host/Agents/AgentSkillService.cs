using System.Reflection;
using System.Text.Json;
using Patchouli.Core.Agents;
using Patchouli.Core.Results;

namespace Patchouli.Host.Agents;

public sealed record AgentSkillStatus(string Directory, bool IsInstalled, string DiscoveryNote);

/// <summary>Manages explicit user installs and library-local skills for agent startup.</summary>
public sealed class AgentSkillService
{
    private sealed record SkillRegistration(string Path, string Scope);

    private const string ResourcePrefix = "Patchouli.AgentSkill/";
    private const string VersionFile = ".patchouli-version";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly AgentSkillLocations _locations;
    private readonly string _version;
    private readonly Action<string, string> _activateDirectory;

    public AgentSkillService() : this(AgentSkillLocations.CreateDefault())
    {
    }

    public AgentSkillService(AgentSkillLocations locations)
        : this(locations, typeof(AgentSkillService).Assembly.GetName().Version!.ToString())
    {
    }

    internal AgentSkillService(AgentSkillLocations locations, string version,
        Action<string, string>? activateDirectory = null)
    {
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _version = version;
        _activateDirectory = activateDirectory ?? Directory.Move;
    }

    public Result<AgentSkillStatus> GetStatus(string agentId)
    {
        try
        {
            string directory = _locations.GetUserDirectory(agentId);
            ValidatePath(directory);
            if (File.Exists(directory))
            {
                throw new IOException($"Skill 目标必须是目录：{directory}");
            }

            return Result<AgentSkillStatus>.Success(new AgentSkillStatus(directory, Directory.Exists(directory),
                DiscoveryNote(agentId, directory)));
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return Result<AgentSkillStatus>.Failure("agent_skill_unavailable", DescribeError(exception));
        }
    }

    public Task<Result> InstallAsync(string agentId, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => InstallUser(agentId, cancellationToken, true), cancellationToken);
    }

    public Task<Result> ClearAsync(string agentId, CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            string target = _locations.GetUserDirectory(agentId);
            Dictionary<string, SkillRegistration> records = ReadRegistrations();
            ValidateTree(target, cancellationToken);
            DeleteSafe(target, cancellationToken);
            if (records.TryGetValue(agentId, out SkillRegistration? registration) &&
                SamePath(registration.Path, target))
            {
                records.Remove(agentId);
                try
                {
                    WriteRegistrations(records);
                }
                catch (Exception exception) when (IsFileError(exception) || exception is JsonException)
                {
                    throw new IOException($"用户级 Skill 已清除，但安装登记未能更新；请修复状态目录后重试：{_locations.StateDirectory}", exception);
                }
            }
        }, cancellationToken);
    }

    public Task<Result> ClearAllAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            Dictionary<string, SkillRegistration> records = ReadRegistrations();
            bool changed = false;
            foreach (AgentCatalogEntry agent in AgentCatalog.All)
            {
                ValidateTree(_locations.GetUserDirectory(agent.Id), cancellationToken);
            }

            foreach (AgentCatalogEntry agent in AgentCatalog.All)
            {
                string target = _locations.GetUserDirectory(agent.Id);
                changed |= Directory.Exists(target);
                DeleteSafe(target, cancellationToken);
                if (records.TryGetValue(agent.Id, out SkillRegistration? registration) &&
                    SamePath(registration.Path, target))
                {
                    changed |= records.Remove(agent.Id);
                }
            }

            if (!changed)
            {
                return;
            }

            try
            {
                WriteRegistrations(records);
            }
            catch (Exception exception) when (IsFileError(exception) || exception is JsonException)
            {
                throw new IOException("用户级 Skill 已清除，但安装登记未能更新；请修复状态目录后重试。", exception);
            }
        }, cancellationToken);
    }

    /// <summary>Prepares or removes managed copies immediately before this agent is launched.</summary>
    public Task<Result> PrepareWorkspaceAsync(string agentId, IAgentWorkspaceLocator workspace,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            string workspaceDirectory = Path.GetFullPath(workspace.GetWorkspaceDirectory());
            // Selection is presence-only. Do not inspect or diagnose user-authored Skill contents.
            bool userInstalled = _locations.GetVisibleUserDirectories(agentId).Any(Directory.Exists);
            IReadOnlyList<string> managed = _locations.GetVisibleManagedDirectories(agentId, workspaceDirectory);
            foreach (string path in managed)
            {
                ValidateTree(path, cancellationToken);
            }

            if (userInstalled)
            {
                foreach (string path in managed)
                {
                    DeleteSafe(path, cancellationToken);
                }

                return;
            }

            workspace.EnsureWorkspaceDirectory();
            string native = _locations.GetManagedDirectory(agentId, workspaceDirectory);
            InstallManaged(native, cancellationToken);
            foreach (string path in managed.Where(path => !SamePath(path, native)))
            {
                DeleteSafe(path, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>Updates explicit user installs after an application version change; missing installs stay missing.</summary>
    public Task<Result> UpdateUserInstallationsAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(() =>
        {
            Dictionary<string, SkillRegistration> records = ReadRegistrations();
            foreach ((string agentId, SkillRegistration registration) in records.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!AgentCatalog.All.Any(agent => agent.Id == agentId))
                {
                    continue;
                }

                string expected = _locations.GetUserDirectory(agentId);
                string path = registration.Path;
                if (!string.Equals(registration.Scope, "user", StringComparison.Ordinal) || !SamePath(path, expected) ||
                    !Directory.Exists(path))
                {
                    continue;
                }

                ValidatePath(path);
                string stamp = Path.Combine(path, VersionFile);
                ValidatePath(stamp);
                if (File.Exists(stamp) && File.ReadAllText(stamp) != _version)
                {
                    ValidateTree(path, cancellationToken);
                    InstallAt(path, cancellationToken);
                }
            }
        }, cancellationToken);
    }

    private async Task<Result> RunAsync(Action action, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(action, token).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception exception) when (IsFileError(exception) || exception is JsonException)
        {
            return Result.Failure("agent_skill_failed", DescribeError(exception));
        }
        finally
        {
            Gate.Release();
        }
    }

    private void InstallUser(string agentId, CancellationToken token, bool register)
    {
        string target = _locations.GetUserDirectory(agentId);
        Dictionary<string, SkillRegistration> records = ReadRegistrations();
        InstallAt(target, token);
        if (register)
        {
            records[agentId] = new SkillRegistration(target, "user");
            try
            {
                WriteRegistrations(records);
            }
            catch (Exception exception) when (IsFileError(exception) || exception is JsonException)
            {
                throw new IOException($"Skill 已安装到 {target}，但登记保存失败；安装升级不会自动维护此副本，修复状态目录后请重新安装。", exception);
            }
        }
    }

    private void InstallManaged(string target, CancellationToken token)
    {
        InstallAt(target, token);
    }

    private void InstallAt(string target, CancellationToken token)
    {
        ValidateTree(target, token);
        string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        string transaction = Path.Combine(parent, ".patchouli-stage-" + Guid.NewGuid().ToString("N"));
        string prepared = Path.Combine(transaction, "prepared"), previous = Path.Combine(transaction, "previous");
        bool preserve = false;
        Directory.CreateDirectory(prepared);
        try
        {
            Assembly assembly = typeof(AgentSkillService).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames()
                         .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
            {
                token.ThrowIfCancellationRequested();
                string relative = resource[ResourcePrefix.Length..].Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                string file = Path.Combine(prepared, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using Stream source = assembly.GetManifestResourceStream(resource)!;
                using FileStream destination = File.Create(file);
                source.CopyTo(destination);
            }

            if (!File.Exists(Path.Combine(prepared, "SKILL.md")))
            {
                throw new IOException("应用未包含 Patchouli Skill，请重新安装应用。");
            }

            File.WriteAllText(Path.Combine(prepared, VersionFile), _version);
            token.ThrowIfCancellationRequested();
            ValidateTree(target, token);
            if (Directory.Exists(target))
            {
                Directory.Move(target, previous);
            }

            try
            {
                _activateDirectory(prepared, target);
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                if (Directory.Exists(previous))
                {
                    preserve = true;
                    try
                    {
                        Directory.Move(previous, target);
                        preserve = false;
                    }
                    catch (Exception restore) when (IsFileError(restore))
                    {
                        throw new IOException($"Skill 替换失败，原目录保留在 {previous}，请手动恢复。", restore);
                    }
                }

                throw;
            }
        }
        finally
        {
            if (!preserve)
            {
                DeleteTree(transaction);
            }
        }
    }

    private Dictionary<string, SkillRegistration> ReadRegistrations()
    {
        string file = RegistrationFile;
        ValidatePath(file);
        if (!File.Exists(file))
        {
            return new Dictionary<string, SkillRegistration>(StringComparer.Ordinal);
        }

        try
        {
            Dictionary<string, SkillRegistration>? records =
                JsonSerializer.Deserialize<Dictionary<string, SkillRegistration>>(File.ReadAllText(file));
            if (records is null || records.Any(entry => entry.Value is null ||
                                                        string.IsNullOrWhiteSpace(entry.Value.Path) ||
                                                        string.IsNullOrWhiteSpace(entry.Value.Scope)))
            {
                throw new IOException($"Agent Skill 安装登记文件内容无效：{file}");
            }

            return records;
        }
        catch (JsonException exception)
        {
            throw new IOException($"Agent Skill 安装登记文件格式无效：{file}", exception);
        }
    }

    private void WriteRegistrations(Dictionary<string, SkillRegistration> records)
    {
        ValidatePath(_locations.StateDirectory);
        Directory.CreateDirectory(_locations.StateDirectory);
        string file = RegistrationFile, temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        ValidatePath(file);
        ValidatePath(temporary);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(records));
            File.Move(temporary, file, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string RegistrationFile => Path.Combine(_locations.StateDirectory, "agent-skills.json");

    private string DiscoveryNote(string id, string target)
    {
        string[] consumers = AgentCatalog.All.Where(agent => agent.Id != id &&
                                                             _locations.GetVisibleUserDirectories(agent.Id)
                                                                 .Any(path => SamePath(path, target)))
            .Select(agent => agent.DisplayName).ToArray();
        return consumers.Length == 0
            ? "只管理此 Agent 的原生用户级目录；其他兼容目录中的同名 Skill 不受影响。"
            : $"此共享用户目录也可被 {string.Join("、", consumers)} 读取，清除会同时影响这些 Agent 使用的此份 Skill；其他目录不受影响。";
    }

    private static bool SamePath(string left, string right)
    {
        return AgentSkillLocations.PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
    }

    private static void DeleteSafe(string path, CancellationToken token)
    {
        ValidateTree(path, token);
        token.ThrowIfCancellationRequested();
        DeleteTree(path);
    }

    private static FileAttributes? Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            FileSystemInfo file = new FileInfo(path);
            FileSystemInfo directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null)
            {
                return FileAttributes.ReparsePoint;
            }

            return null;
        }
    }

    private static void ValidatePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Attributes(current) is { } attr && (attr & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Skill 路径包含符号链接或重解析点，已拒绝操作：{current}");
            }
        }
    }

    private static void ValidateTree(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidatePath(path);
        FileAttributes? attrs = Attributes(path);
        if (attrs is null)
        {
            return;
        }

        if ((attrs & FileAttributes.Directory) == 0)
        {
            throw new IOException($"Skill 目标必须是目录：{path}");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();
            ValidatePath(entry);
            if (Directory.Exists(entry))
            {
                ValidateTree(entry, token);
            }
        }
    }

    private static void DeleteTree(string path)
    {
        ValidatePath(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            ValidatePath(entry);
            if (Directory.Exists(entry))
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path);
    }

    private static bool IsFileError(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }

    private static string DescribeError(Exception ex)
    {
        return $"{ex.Message} 请检查目录权限、只读/沙箱限制后重试；需使用支持 Agent Skills 的 Agent 版本。";
    }
}
