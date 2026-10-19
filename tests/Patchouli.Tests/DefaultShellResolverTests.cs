using FluentAssertions;
using Patchouli.Terminal;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: <see cref="DefaultShellResolver"/> must return a real, rooted executable with the
/// documented argument shape for the current platform.
/// </summary>
public sealed class DefaultShellResolverTests
{
    [Fact]
    public void Resolve_returns_a_rooted_executable_and_a_consistent_argument_list()
    {
        ShellCommand shell = DefaultShellResolver.Resolve();

        shell.Executable.Should().NotBeNullOrWhiteSpace();
        Path.IsPathFullyQualified(shell.Executable).Should().BeTrue();
        shell.Arguments.Should().NotBeNull();

        string name = Path.GetFileNameWithoutExtension(shell.Executable);
        if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            shell.Arguments.Should().Equal("-NoLogo", "-NoProfile");
        }
        else if (name.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            shell.Arguments.Should().BeEmpty();
        }
    }

    [Fact]
    public void Create_options_uses_the_resolved_shell_and_the_requested_directory()
    {
        string directory = Path.GetFullPath(Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"agent-shell-{Guid.NewGuid():N}")).FullName);
        try
        {
            ShellCommand shell = DefaultShellResolver.Resolve();

            TerminalSessionOptions options = DefaultShellResolver.CreateOptions(directory);

            options.Executable.Should().Be(shell.Executable);
            options.Arguments.Should().Equal(shell.Arguments);
            options.WorkingDirectory.Should().Be(directory);
            options.InitialCols.Should().Be(TerminalSessionOptions.DefaultInitialCols);
            options.InitialRows.Should().Be(TerminalSessionOptions.DefaultInitialRows);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(directory);
        }
    }

    [Fact]
    public void Create_options_without_a_directory_leaves_the_working_directory_unset()
    {
        DefaultShellResolver.CreateOptions().WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public void Find_on_path_returns_a_full_path_for_a_known_command()
    {
        string command = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

        string? resolved = DefaultShellResolver.FindOnPath(command);

        resolved.Should().NotBeNullOrWhiteSpace();
        Path.IsPathFullyQualified(resolved!).Should().BeTrue();
        File.Exists(resolved!).Should().BeTrue();
    }

    [Fact]
    public void Find_on_path_returns_a_full_path_for_a_rooted_existing_file()
    {
        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"agent-shell-{Guid.NewGuid():N}")).FullName;
        try
        {
            string file = Path.Combine(directory, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
            File.WriteAllText(file, string.Empty);

            DefaultShellResolver.FindOnPath(file).Should().Be(Path.GetFullPath(file));
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(directory);
        }
    }

    [Fact]
    public void Find_on_path_returns_null_for_a_missing_rooted_file()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-shell-{Guid.NewGuid():N}.exe");

        DefaultShellResolver.FindOnPath(missing).Should().BeNull();
    }

    [Fact]
    public void Find_on_path_returns_null_for_an_unknown_command()
    {
        string missing = "patchouli-unknown-command-" + Guid.NewGuid().ToString("N");

        DefaultShellResolver.FindOnPath(missing).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_on_path_rejects_a_blank_command(string? command)
    {
        Action act = () => DefaultShellResolver.FindOnPath(command!);

        act.Should().Throw<ArgumentException>();
    }
}
