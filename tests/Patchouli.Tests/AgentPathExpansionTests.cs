using FluentAssertions;
using Patchouli.Core.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: <see cref="AgentPathExpansion"/> expands the catalog's path templates using the
/// injected environment only. Unknown variables deliberately stay literal so a later probe fails
/// visibly instead of silently resolving to the wrong directory.
/// </summary>
public sealed class AgentPathExpansionTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();
    private readonly FakeAgentPlatformEnvironment _environment;

    public AgentPathExpansionTests()
    {
        string home = _directory.Combine("home");
        string localAppData = _directory.Combine("local-app-data");
        _environment = new FakeAgentPlatformEnvironment
        {
            Platform = AgentPlatform.Windows,
            HomeDirectory = home
        };
        _environment.WellKnownDirectories["USERPROFILE"] = home;
        _environment.WellKnownDirectories["HOME"] = home;
        _environment.WellKnownDirectories["LOCALAPPDATA"] = localAppData;
        _environment.WellKnownDirectories["APPDATA"] = _directory.Combine("roaming");
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void Percent_home_token_expands_to_the_environment_home()
    {
        string expanded = AgentPathExpansion.Expand("%HOME%/.config/opencode", _environment);

        expanded.Should().Be(Path.GetFullPath(
            Path.Combine(_environment.HomeDirectory, ".config/opencode")));
    }

    [Theory]
    [InlineData("USERPROFILE")]
    [InlineData("userprofile")]
    [InlineData(" HOME ")]
    public void Well_known_percent_names_are_resolved_case_insensitively(string name)
    {
        string expanded = AgentPathExpansion.Expand($"%{name}%/tools/agent.exe", _environment);

        expanded.Should().StartWith(_environment.HomeDirectory);
        expanded.Should().EndWith(Path.Combine("tools", "agent.exe"));
    }

    [Fact]
    public void Multiple_percent_tokens_are_all_expanded()
    {
        string expanded = AgentPathExpansion.Expand(
            "%LOCALAPPDATA%\\Patchouli\\%APPDATA%\\agents", _environment);

        expanded.Should().Contain(_directory.Combine("local-app-data"))
            .And.Contain(_directory.Combine("roaming"))
            .And.Contain("Patchouli");
    }

    [Fact]
    public void Unknown_percent_variable_is_left_literal()
    {
        string expanded = AgentPathExpansion.Expand("%PATCHOULI_UNKNOWN%\\tool\\agent.exe", _environment);

        expanded.Should().Contain("%PATCHOULI_UNKNOWN%");
        Path.IsPathFullyQualified(expanded).Should().BeTrue();
    }

    [Fact]
    public void Unterminated_percent_token_is_left_literal()
    {
        string expanded = AgentPathExpansion.Expand("%HOME/tool/agent.exe", _environment);

        expanded.Should().Be(Path.GetFullPath("%HOME/tool/agent.exe"));
    }

    [Fact]
    public void Empty_percent_token_is_left_literal()
    {
        string expanded = AgentPathExpansion.Expand("%%/tool/agent.exe", _environment);

        expanded.Should().Be(Path.GetFullPath("%%/tool/agent.exe"));
    }

    [Theory]
    [InlineData("$HOME/.opencode/bin/opencode")]
    [InlineData("${HOME}/.opencode/bin/opencode")]
    public void Dollar_home_token_expands_with_or_without_braces(string template)
    {
        string expanded = AgentPathExpansion.Expand(template, _environment);

        expanded.Should().Be(Path.GetFullPath(
            Path.Combine(_environment.HomeDirectory, ".opencode/bin/opencode")));
    }

    [Fact]
    public void Unknown_dollar_variable_is_left_literal()
    {
        string expanded = AgentPathExpansion.Expand("$PATCHOULI_UNKNOWN/tool", _environment);

        expanded.Should().Be(Path.GetFullPath("$PATCHOULI_UNKNOWN/tool"));
    }

    [Fact]
    public void Unknown_unbraced_dollar_variable_only_consumes_the_name()
    {
        string expanded = AgentPathExpansion.Expand("$NOPE-tail", _environment);

        expanded.Should().Be(Path.GetFullPath("$NOPE-tail"));
    }

    [Fact]
    public void Unterminated_braced_dollar_variable_is_left_literal()
    {
        string expanded = AgentPathExpansion.Expand("${HOME/tool", _environment);

        expanded.Should().Be(Path.GetFullPath("${HOME/tool"));
    }

    [Fact]
    public void Dollar_followed_by_punctuation_stays_literal()
    {
        string expanded = AgentPathExpansion.Expand("$ /tool/$/agent", _environment);

        expanded.Should().Be(Path.GetFullPath("$ /tool/$/agent"));
    }

    [Theory]
    [InlineData("~")]
    [InlineData("~/")]
    [InlineData("~\\")]
    public void Leading_tilde_expands_to_home(string template)
    {
        string expanded = AgentPathExpansion.Expand(template, _environment);

        expanded.Should().Be(Path.GetFullPath(_environment.HomeDirectory));
    }

    [Theory]
    [InlineData("~/tools/opencode")]
    [InlineData("~\\tools\\opencode")]
    public void Tilde_with_separator_expands_under_home(string template)
    {
        string expanded = AgentPathExpansion.Expand(template, _environment);

        expanded.Should().Be(Path.GetFullPath(
            Path.Combine(_environment.HomeDirectory, "tools", "opencode")));
    }

    [Fact]
    public void Tilde_without_a_separator_is_not_expanded()
    {
        string expanded = AgentPathExpansion.Expand("~other/tools", _environment);

        expanded.Should().Contain("~other");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_path_returns_empty(string? path)
    {
        AgentPathExpansion.Expand(path, _environment).Should().BeEmpty();
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        string expanded = AgentPathExpansion.Expand("  %HOME%/tools  ", _environment);

        expanded.Should().Be(Path.GetFullPath(
            Path.Combine(_environment.HomeDirectory, "tools")));
    }

    [Fact]
    public void Missing_environment_throws()
    {
        Action act = () => AgentPathExpansion.Expand("~/tools", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Expanded_result_is_always_a_full_path()
    {
        string expanded = AgentPathExpansion.Expand("relative/tools/agent", _environment);

        Path.IsPathFullyQualified(expanded).Should().BeTrue();
        expanded.Should().EndWith(Path.Combine("relative", "tools", "agent"));
    }
}
