using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Infrastructure.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: <see cref="PlatformAgentEnvironment"/> maps the documented well-known names and
/// derives platform, architecture and home directory without reading any Agent-specific state.
/// </summary>
public sealed class PlatformAgentEnvironmentTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Theory]
    [InlineData("USERPROFILE", Environment.SpecialFolder.UserProfile)]
    [InlineData("HOME", Environment.SpecialFolder.UserProfile)]
    [InlineData("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData)]
    [InlineData("APPDATA", Environment.SpecialFolder.ApplicationData)]
    [InlineData("PROGRAMFILES", Environment.SpecialFolder.ProgramFiles)]
    public void Documented_well_known_names_resolve_to_the_os_folder(string name,
        Environment.SpecialFolder folder)
    {
        string expected = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        PlatformAgentEnvironment environment = new();

        environment.TryResolveWellKnown(name, out string? resolved).Should().BeTrue();
        resolved.Should().Be(expected);
    }

    [Fact]
    public void Well_known_names_are_trimmed_and_case_insensitive()
    {
        PlatformAgentEnvironment environment = new();
        string expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        environment.TryResolveWellKnown("  userprofile  ", out string? resolved).Should().BeTrue();
        resolved.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT_A_REAL_FOLDER")]
    public void Unknown_or_blank_well_known_names_return_false(string? name)
    {
        PlatformAgentEnvironment environment = new();

        environment.TryResolveWellKnown(name!, out string? resolved).Should().BeFalse();
        resolved.Should().BeNull();
    }

    [Fact]
    public void A_blank_constructor_home_falls_back_to_the_application_directory()
    {
        PlatformAgentEnvironment environment = new(AgentPlatform.Linux, AgentArchitecture.Arm64, "  ");

        environment.HomeDirectory.Should().Be(Path.GetFullPath(AppContext.BaseDirectory));
    }

    [Fact]
    public void A_relative_constructor_home_is_made_absolute()
    {
        string relative = $"agent-home-{Guid.NewGuid():N}";

        PlatformAgentEnvironment environment = new(AgentPlatform.Linux, AgentArchitecture.Arm64, relative);

        Path.IsPathFullyQualified(environment.HomeDirectory).Should().BeTrue();
        environment.HomeDirectory.Should().Be(Path.GetFullPath(relative));
    }

    [Fact]
    public void Tilde_expansion_uses_the_injected_home_directory()
    {
        string home = Directory.CreateDirectory(_directory.Combine("home")).FullName;
        PlatformAgentEnvironment environment = new(AgentPlatform.Linux, AgentArchitecture.Arm64, home);

        string expanded = environment.ExpandPath("~/.local/bin/opencode");

        expanded.Should().Be(Path.GetFullPath(Path.Combine(home, ".local", "bin", "opencode")));
    }

    [Fact]
    public void The_default_environment_expands_the_real_home_directory()
    {
        PlatformAgentEnvironment environment = new();
        string realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(realHome))
        {
            return;
        }

        string expanded = environment.ExpandPath("%HOME%/agent-tools");

        expanded.Should().Be(Path.GetFullPath(Path.Combine(realHome, "agent-tools")));
    }

    [Fact]
    public void File_exists_requires_a_non_blank_existing_path()
    {
        PlatformAgentEnvironment environment = new();
        string file = _directory.CreateFile("existing.txt", "content");

        environment.FileExists(file).Should().BeTrue();
        environment.FileExists(_directory.Combine("missing.txt")).Should().BeFalse();
        environment.FileExists("").Should().BeFalse();
        environment.FileExists("   ").Should().BeFalse();
    }

    [Fact]
    public void Detected_platform_matches_the_host()
    {
        AgentPlatform expected = OperatingSystem.IsWindows()
            ? AgentPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? AgentPlatform.MacOS
                : AgentPlatform.Linux;

        PlatformAgentEnvironment.DetectPlatform().Should().Be(expected);
    }

    [Fact]
    public void Detected_architecture_is_one_of_the_supported_values()
    {
        AgentArchitecture architecture = PlatformAgentEnvironment.DetectArchitecture();

        architecture.Should().BeOneOf(AgentArchitecture.X64, AgentArchitecture.Arm64, AgentArchitecture.X86,
            AgentArchitecture.Arm);
    }

    [Fact]
    public void Constructor_values_round_trip_through_the_properties()
    {
        PlatformAgentEnvironment environment = new(AgentPlatform.MacOS, AgentArchitecture.X64,
            _directory.Path);

        environment.Platform.Should().Be(AgentPlatform.MacOS);
        environment.Architecture.Should().Be(AgentArchitecture.X64);
        environment.HomeDirectory.Should().Be(Path.GetFullPath(_directory.Path));
    }

    [Theory]
    [InlineData(AgentPlatform.Windows, AgentArchitecture.X64, "windows-x86_64")]
    [InlineData(AgentPlatform.Windows, AgentArchitecture.Arm64, "windows-aarch64")]
    [InlineData(AgentPlatform.MacOS, AgentArchitecture.X64, "darwin-x86_64")]
    [InlineData(AgentPlatform.MacOS, AgentArchitecture.Arm64, "darwin-aarch64")]
    [InlineData(AgentPlatform.Linux, AgentArchitecture.X64, "linux-x86_64")]
    [InlineData(AgentPlatform.Linux, AgentArchitecture.Arm64, "linux-aarch64")]
    public void Registry_platform_keys_follow_the_documented_mapping(AgentPlatform platform,
        AgentArchitecture architecture, string expected)
    {
        AgentRegistryPlatformKey.TryGet(platform, architecture).Should().Be(expected);
    }

    [Theory]
    [InlineData(AgentPlatform.Windows, AgentArchitecture.X86)]
    [InlineData(AgentPlatform.Windows, AgentArchitecture.Arm)]
    [InlineData(AgentPlatform.Linux, AgentArchitecture.X86)]
    [InlineData(AgentPlatform.MacOS, AgentArchitecture.X86)]
    public void Unsupported_architecture_combinations_have_no_registry_key(AgentPlatform platform,
        AgentArchitecture architecture)
    {
        AgentRegistryPlatformKey.TryGet(platform, architecture).Should().BeNull();
    }
}
