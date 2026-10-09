using System.Diagnostics;
using FluentAssertions;
using Patchouli.Testing;

namespace Patchouli.Tests;

public sealed class RepositoryTemporaryDirectoryTests
{
    [Fact]
    public void Temporary_files_and_fixture_profiles_are_created_under_the_repository_tmp_directory()
    {
        RepositoryTestEnvironment.IsTemporaryPath(Path.GetTempPath()).Should().BeTrue();
        string file = Path.GetTempFileName();
        try
        {
            RepositoryTestEnvironment.IsTemporaryPath(file).Should().BeTrue();
            using TemporaryAppSettingsFile settings = new();
            RepositoryTestEnvironment.IsTemporaryPath(settings.Path).Should().BeTrue();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Child_processes_inherit_the_same_temporary_directory()
    {
        ProcessStartInfo options = new(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        options.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        options.ArgumentList.Add(OperatingSystem.IsWindows() ? "echo %TEMP%" : "printf '%s' \"$TMPDIR\"");
        using Process child = Process.Start(options) ?? throw new InvalidOperationException("Could not start shell.");
        string path = (await child.StandardOutput.ReadToEndAsync()).Trim();
        await child.WaitForExitAsync();
        child.ExitCode.Should().Be(0);
        RepositoryTestEnvironment.IsTemporaryPath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("artifacts")]
    [InlineData("scratch")]
    [InlineData("TestResults")]
    public void Deprecated_temporary_roots_do_not_exist(string name)
    {
        Directory.Exists(TestPaths.FromRepositoryRoot(name)).Should().BeFalse();
    }

    [Fact]
    public void Sibling_directory_names_do_not_pass_the_temporary_path_boundary()
    {
        RepositoryTestEnvironment.IsTemporaryPath(TestPaths.FromRepositoryRoot(".tmp-outside", "file.txt"))
            .Should().BeFalse();
    }
}
