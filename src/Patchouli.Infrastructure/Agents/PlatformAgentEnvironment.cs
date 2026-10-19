using System.Runtime.InteropServices;
using Patchouli.Core.Agents;

namespace Patchouli.Infrastructure.Agents;

/// <summary>
/// Real OS environment for Agent detection. The explicit constructor is used by tests to replace the
/// platform, architecture and home directory.
/// </summary>
public sealed class PlatformAgentEnvironment : IAgentPlatformEnvironment
{
    private static readonly IReadOnlyDictionary<string, Environment.SpecialFolder> WellKnownDirectories =
        new Dictionary<string, Environment.SpecialFolder>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = Environment.SpecialFolder.UserProfile,
            ["HOME"] = Environment.SpecialFolder.UserProfile,
            ["LOCALAPPDATA"] = Environment.SpecialFolder.LocalApplicationData,
            ["APPDATA"] = Environment.SpecialFolder.ApplicationData,
            ["PROGRAMFILES"] = Environment.SpecialFolder.ProgramFiles,
            ["PROGRAMFILES(X86)"] = Environment.SpecialFolder.ProgramFilesX86
        };

    public PlatformAgentEnvironment()
        : this(DetectPlatform(), DetectArchitecture(), DetectHomeDirectory())
    {
    }

    public PlatformAgentEnvironment(
        AgentPlatform platform,
        AgentArchitecture architecture,
        string homeDirectory)
    {
        Platform = platform;
        Architecture = architecture;
        HomeDirectory = string.IsNullOrWhiteSpace(homeDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(homeDirectory);
    }

    public AgentPlatform Platform { get; }
    public AgentArchitecture Architecture { get; }
    public string HomeDirectory { get; }

    public bool TryResolveWellKnown(string name, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(name) ||
            !WellKnownDirectories.TryGetValue(name.Trim(), out Environment.SpecialFolder folder))
        {
            return false;
        }

        string resolved = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        value = resolved;
        return true;
    }

    public string ExpandPath(string path)
    {
        return AgentPathExpansion.Expand(path, this);
    }

    public bool FileExists(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public static AgentPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return AgentPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? AgentPlatform.MacOS : AgentPlatform.Linux;
    }

    public static AgentArchitecture DetectArchitecture()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => AgentArchitecture.X64,
            System.Runtime.InteropServices.Architecture.Arm64 => AgentArchitecture.Arm64,
            System.Runtime.InteropServices.Architecture.X86 => AgentArchitecture.X86,
            System.Runtime.InteropServices.Architecture.Arm => AgentArchitecture.Arm,
            _ => AgentArchitecture.X64
        };
    }

    private static string DetectHomeDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : home;
    }
}
