using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Host.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: the per-library Agent workspace lives under the runtime database directory and is
/// created owner-only on Unix on first launch.
/// </summary>
public sealed class AgentWorkspaceLocatorTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void Get_workspace_directory_is_the_library_data_directory_plus_agent_workspace()
    {
        AgentWorkspaceLocator locator = new(_directory.Path);

        locator.GetWorkspaceDirectory().Should().Be(Path.GetFullPath(
            Path.Combine(_directory.Path, AgentWorkspaceLayout.DirectoryName)));
        locator.GetWorkspaceDirectory().Should().EndWith(
            Path.Combine(AgentWorkspaceLayout.DirectoryName));
    }

    [Fact]
    public void Ensure_workspace_directory_creates_it_once_and_is_idempotent()
    {
        AgentWorkspaceLocator locator = new(_directory.Path);

        string created = locator.EnsureWorkspaceDirectory();
        string again = locator.EnsureWorkspaceDirectory();

        Directory.Exists(created).Should().BeTrue();
        again.Should().Be(created);
        Directory.EnumerateFileSystemEntries(created).Should().BeEmpty();
    }

    [Fact]
    public void Ensure_workspace_directory_uses_owner_only_permissions_on_unix()
    {
        AgentWorkspaceLocator locator = new(_directory.Path);

        string directory = locator.EnsureWorkspaceDirectory();

        if (OperatingSystem.IsWindows())
        {
            Directory.Exists(directory).Should().BeTrue();
            return;
        }

        File.GetUnixFileMode(directory).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void From_runtime_database_uses_the_database_directory()
    {
        string databasePath = _directory.Combine("library", "runtime.sqlite");

        AgentWorkspaceLocator locator = AgentWorkspaceLocator.FromRuntimeDatabase(databasePath);

        locator.GetWorkspaceDirectory().Should().Be(Path.GetFullPath(
            Path.Combine(_directory.Path, "library", AgentWorkspaceLayout.DirectoryName)));
    }

    [Fact]
    public void From_runtime_database_tolerates_a_rooted_file_path()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_directory.Path))!;
        string databasePath = Path.Combine(root, $"patchouli-root-{Guid.NewGuid():N}.sqlite");

        AgentWorkspaceLocator locator = AgentWorkspaceLocator.FromRuntimeDatabase(databasePath);

        locator.GetWorkspaceDirectory().Should().StartWith(Path.GetFullPath(root));
        locator.GetWorkspaceDirectory().Should().EndWith(AgentWorkspaceLayout.DirectoryName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_library_data_directory_is_rejected(string? path)
    {
        Action act = () => _ = new AgentWorkspaceLocator(path!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Workspace_layout_rejects_a_blank_library_data_directory()
    {
        Action act = () => AgentWorkspaceLayout.GetDirectory(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Workspace_layout_returns_a_full_path_under_the_library_data_directory()
    {
        string directory = AgentWorkspaceLayout.GetDirectory(_directory.Path);

        Path.IsPathFullyQualified(directory).Should().BeTrue();
        directory.Should().Be(Path.Combine(Path.GetFullPath(_directory.Path),
            AgentWorkspaceLayout.DirectoryName));
    }

    [Fact]
    public void A_relative_library_data_directory_is_made_absolute()
    {
        string relative = $"relative-agent-library-{Guid.NewGuid():N}";
        AgentWorkspaceLocator locator = new(relative);

        Path.IsPathFullyQualified(locator.GetWorkspaceDirectory()).Should().BeTrue();
        locator.GetWorkspaceDirectory().Should().EndWith(Path.Combine(relative,
            AgentWorkspaceLayout.DirectoryName));
    }
}
