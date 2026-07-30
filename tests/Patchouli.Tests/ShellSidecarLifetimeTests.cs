using FluentAssertions;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
    [Theory]
    [InlineData(999)]
    [InlineData(60_001)]
    public async Task Host_rejects_command_timeout_outside_sidecar_range(int timeoutMilliseconds)
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        SystemClock clock = new();
        LibraryIdentityService library = new(db.ConnectionFactory, clock);
        (await library.CreateLibraryAsync("Shell timeout validation")).IsSuccess.Should().BeTrue();
        SearchProfileService profiles = new(db.ConnectionFactory, library, clock);
        SqliteSearchService search = new(db.ConnectionFactory, profiles);
        EvidenceReferenceService evidence = new(db.ConnectionFactory, clock);
        McpReadApi api = new(db.ConnectionFactory, search, evidence);
        ShellDomainService domain = new(db.ConnectionFactory, api, search, evidence, library: library);
        ShellResourceLimits limits = new() { CommandTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds) };

        Action create = () => _ = new ShellSidecarHost(domain, limits: limits);

        create.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*between 1 and 60 seconds*");
    }

    [Fact]
    public async Task Host_rejects_initialize_response_with_different_effective_timeout()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        SystemClock clock = new();
        LibraryIdentityService library = new(db.ConnectionFactory, clock);
        (await library.CreateLibraryAsync("Shell initialize validation")).IsSuccess.Should().BeTrue();
        SearchProfileService profiles = new(db.ConnectionFactory, library, clock);
        SqliteSearchService search = new(db.ConnectionFactory, profiles);
        EvidenceReferenceService evidence = new(db.ConnectionFactory, clock);
        McpReadApi api = new(db.ConnectionFactory, search, evidence);
        ShellDomainService domain = new(db.ConnectionFactory, api, search, evidence, library: library);
        await using ShellSidecarHost host = new(domain);
        JsonElement response = JsonSerializer.SerializeToElement(new
        {
            protocol_version = ShellRpcProtocol.Version,
            status = "ready",
            capabilities = new
            {
                execution_scoped_reverse_cancellation = true
            },
            limits = new
            {
                command_timeout_ms = 14_999,
                max_terminal_output_bytes = 1024 * 1024,
                max_commands = 2000,
                max_loop_iterations = 5000,
                max_function_depth = 16,
                max_string_bytes = 2 * 1024 * 1024
            }
        }, ShellRpcFraming.JsonOptions);
        MethodInfo validate = typeof(ShellSidecarHost).GetMethod("ValidateInitializeResponse",
                                  BindingFlags.Instance | BindingFlags.NonPublic) ??
                              throw new InvalidOperationException("Initialize response validator was not found.");

        // The assertion executes before the async-disposed host leaves this scope.
        // ReSharper disable once AccessToDisposedClosure
        Action invoke = () => validate.Invoke(host, [response]);

        invoke.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*command_timeout_ms*14999*expected 15000*");
        host.Status.Should().Be(ShellSandboxStatus.ProtocolIncompatible);
    }

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
