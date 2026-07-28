using FluentAssertions;
using System.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Shell;

namespace Patchouli.Tests;

public sealed class ShellSidecarLifetimeTests
{
    [Fact]
    public void Child_process_lifetime_can_be_created_on_current_os()
    {
        using ChildProcessLifetime lifetime = new();
        lifetime.Should().NotBeNull();
    }

    [Fact]
    public async Task Force_kill_terminates_running_sidecar()
    {
        string sidecar = ShellSidecarHost.ResolveDefaultSidecarPath();
        if (!File.Exists(sidecar))
        {
            return;
        }

        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        SystemClock clock = new();
        LibraryIdentityService library = new(db.ConnectionFactory, clock);
        (await library.CreateLibraryAsync("Shell lifetime")).IsSuccess.Should().BeTrue();
        SearchProfileService profiles = new(db.ConnectionFactory, library, clock);
        SqliteSearchService search = new(db.ConnectionFactory, profiles);
        EvidenceReferenceService evidence = new(db.ConnectionFactory, clock);
        McpReadApi api = new(db.ConnectionFactory, search, evidence);
        ShellDomainService domain = new(db.ConnectionFactory, api, search, evidence, library: library);
        await using ShellSidecarHost host = new(domain, sidecar);
        await host.StartAsync();
        host.Status.Should().Be(ShellSandboxStatus.Ready);

        int? pid = host.SidecarProcessId;
        pid.Should().NotBeNull();
        Process.GetProcessById(pid!.Value).HasExited.Should().BeFalse();

        host.ForceKill();

        await WaitUntilAsync(() => !IsProcessAlive(pid.Value), TimeSpan.FromSeconds(8));
        IsProcessAlive(pid.Value).Should().BeFalse();
        host.SidecarProcessId.Should().BeNull();
    }

    [Fact]
    public async Task Library_switch_rejects_execute_then_replaces_sidecar()
    {
        string sidecar = ShellSidecarHost.ResolveDefaultSidecarPath();
        if (!File.Exists(sidecar))
        {
            return;
        }

        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        SystemClock clock = new();
        LibraryIdentityService library = new(db.ConnectionFactory, clock);
        (await library.CreateLibraryAsync("Shell library switch")).IsSuccess.Should().BeTrue();
        SearchProfileService profiles = new(db.ConnectionFactory, library, clock);
        SqliteSearchService search = new(db.ConnectionFactory, profiles);
        EvidenceReferenceService evidence = new(db.ConnectionFactory, clock);
        McpReadApi api = new(db.ConnectionFactory, search, evidence);
        ShellDomainService domain = new(db.ConnectionFactory, api, search, evidence, library: library);
        await using ShellSidecarHost host = new(domain, sidecar);
        await host.StartAsync();
        int? oldPid = host.SidecarProcessId;
        oldPid.Should().NotBeNull();

        await host.ReplaceForLibrarySwitchAsync();
        host.Status.Should().Be(ShellSandboxStatus.Ready);
        int? newPid = host.SidecarProcessId;
        newPid.Should().NotBeNull();
        newPid.Should().NotBe(oldPid);
        IsProcessAlive(oldPid!.Value).Should().BeFalse();

        Result<ShellExecuteResult> executed = await host.ExecuteAsync(Guid.NewGuid().ToString("N"), "pwd");
        executed.IsSuccess.Should().BeTrue(executed.ErrorMessage);
        executed.Value.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Library_switch_terminates_running_and_queued_calls_with_exit_125()
    {
        string sidecar = ShellSidecarHost.ResolveDefaultSidecarPath();
        if (!File.Exists(sidecar))
        {
            return;
        }

        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        SystemClock clock = new();
        LibraryIdentityService library = new(db.ConnectionFactory, clock);
        (await library.CreateLibraryAsync("Shell switch calls")).IsSuccess.Should().BeTrue();
        SearchProfileService profiles = new(db.ConnectionFactory, library, clock);
        SqliteSearchService search = new(db.ConnectionFactory, profiles);
        EvidenceReferenceService evidence = new(db.ConnectionFactory, clock);
        McpReadApi api = new(db.ConnectionFactory, search, evidence);
        ShellDomainService domain = new(db.ConnectionFactory, api, search, evidence, library: library);
        await using ShellSidecarHost host = new(domain, sidecar);
        await host.StartAsync();

        const string sessionId = "library-switch-session";
        Task<Result<ShellExecuteResult>> running =
            host.ExecuteAsync(sessionId, "while true; do :; done");
        Task<Result<ShellExecuteResult>> queued = host.ExecuteAsync(sessionId, "pwd");

        await host.StopForLibrarySwitchAsync();
        Result<ShellExecuteResult>[] results = await Task.WhenAll(running, queued);

        results.Should().OnlyContain(result =>
            result.IsSuccess &&
            result.Value.ExitCode == 125 &&
            result.Value.Text.Contains("library changed; shell session terminated", StringComparison.Ordinal));
        host.Status.Should().Be(ShellSandboxStatus.Stopped);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
}
