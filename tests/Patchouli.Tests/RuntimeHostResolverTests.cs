using System.Diagnostics;
using FluentAssertions;
using Patchouli.Cli;
using Patchouli.Host.Composition;
using Patchouli.Host.Lifecycle;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class RuntimeHostResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-host-resolver-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void Packaged_cli_finds_the_desktop_host_in_its_parent_directory()
    {
        string packageRoot = Directory.CreateDirectory(Path.Combine(_root, "package")).FullName;
        string cliDirectory = Directory.CreateDirectory(Path.Combine(packageRoot, "cli")).FullName;
        string executable = Path.Combine(packageRoot,
            OperatingSystem.IsWindows() ? "Patchouli.UI.exe" : "Patchouli.UI");
        File.WriteAllText(executable, string.Empty);

        (string resolved, string? assembly) = RuntimeHostResolver.ResolveHostExecutable(cliDirectory, null);

        resolved.Should().Be(executable);
        assembly.Should().BeNull();
    }

    [Fact]
    public void Development_cli_finds_the_sibling_ui_build_output()
    {
        string cliOutput = Directory.CreateDirectory(Path.Combine(
            _root, "src", "Patchouli.Cli", "bin", "Debug", "net10.0")).FullName;
        string uiOutput = Directory.CreateDirectory(Path.Combine(
            _root, "src", "Patchouli.UI", "bin", "Debug", "net10.0")).FullName;
        string assembly = Path.Combine(uiOutput, "Patchouli.UI.dll");
        File.WriteAllText(assembly, string.Empty);

        (string executable, string? resolvedAssembly) =
            RuntimeHostResolver.ResolveHostExecutable(cliOutput, null);

        executable.Should().Be("dotnet");
        resolvedAssembly.Should().Be(assembly);
    }

    [Fact]
    public async Task Built_cli_auto_launches_a_headless_host_and_completes_a_request()
    {
        string database = Path.Combine(_root, "end-to-end.sqlite");
        PatchouliAppSettings defaults = PatchouliAppSettings.Default();
        PatchouliAppSettings settings = defaults with
        {
            Runtime = defaults.Runtime with
            {
                RuntimeDatabasePath = database,
                DefaultSyncRoot = Path.Combine(_root, "sync"),
                DefaultStagingRoot = Path.Combine(_root, "staging"),
                LogDirectory = Path.Combine(_root, "logs"),
                UseMockOcrOnly = true
            }
        };
        HostServices services = await HostServices.CreateAsync(database, settings);
        try
        {
            (await services.Library.CreateLibraryAsync("CLI host launch test")).IsSuccess.Should().BeTrue();
        }
        finally
        {
            await services.ShutdownAsync();
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string cliDirectory = TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Cli", "bin", configuration, "net10.0");
        string cliPath = Path.Combine(cliDirectory,
            OperatingSystem.IsWindows() ? "patchouli-cli.exe" : "patchouli-cli");
        File.Exists(cliPath).Should().BeTrue();

        ProcessStartInfo start = new(cliPath)
        {
            WorkingDirectory = cliDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--db");
        start.ArgumentList.Add(database);
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("find");

        RuntimeHostLease? desktopLease = null;
        try
        {
            using Process process = Process.Start(start)!;
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(timeout.Token);

            RuntimeHostDiscoveryRecord? record = await RuntimeHostCoordinator.ReadAsync(database);
            record.Should().NotBeNull();
            record!.HostKind.Should().Be(RuntimeHostKind.Headless);

            // The headless child of the CLI inherits the redirected stdout/stderr handles, so the
            // output reads below only reach EOF once that child exits. Take the Library over first.
            desktopLease = await RuntimeHostCoordinator.AcquireDesktopAsync(
                database, TimeSpan.FromSeconds(10));
            string output = await outputTask;
            string error = await errorTask;

            process.ExitCode.Should().Be(0, error);
            output.Should().Contain("patchouli://items/").And.Contain("patchouli://texts/");
        }
        finally
        {
            if (desktopLease is not null)
            {
                await desktopLease.DisposeAsync();
            }
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }
}
