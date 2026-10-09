using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Host.Agent;

namespace Patchouli.Tests;

public sealed class AgentWorkspaceTests
{
    [Fact]
    public async Task Relative_files_and_child_processes_use_the_session_temp_directory()
    {
        string parentDirectory = Directory.GetCurrentDirectory();
        await using AgentFsiRepl repl = new();
        string first = repl.Workspaces.Resolve("first");
        string second = repl.Workspaces.Resolve("second");
        first.Should().StartWith(Path.GetFullPath(Path.GetTempPath()));
        AgentToolOutcome[] outcomes = await Task.WhenAll(
            Run(repl, "first",
                "System.IO.File.WriteAllText(\"relative.txt\", \"first\")\nprintfn \"%s\" workingDirectory\nprintfn \"%s\" __SOURCE_DIRECTORY__"),
            Run(repl, "second", "System.IO.File.WriteAllText(\"relative.txt\", \"second\")"));
        outcomes.Should().OnlyContain(outcome => outcome.Succeeded);
        File.ReadAllText(Path.Combine(first, "relative.txt")).Should().Be("first");
        File.ReadAllText(Path.Combine(second, "relative.txt")).Should().Be("second");
        using JsonDocument payload = JsonDocument.Parse(outcomes[0].Payload);
        payload.RootElement.GetProperty("output").GetString().Should().Contain(first);
        Directory.GetCurrentDirectory().Should().Be(parentDirectory);

        string executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string option = OperatingSystem.IsWindows() ? "/c" : "-c";
        AgentToolOutcome child = await Run(repl, "first", $$"""
                                                            let options = System.Diagnostics.ProcessStartInfo("{{executable}}")
                                                            options.ArgumentList.Add("{{option}}")
                                                            options.ArgumentList.Add("echo inherited > child.txt")
                                                            options.UseShellExecute <- false
                                                            options.CreateNoWindow <- true
                                                            let child = System.Diagnostics.Process.Start(options)
                                                            child.WaitForExit()
                                                            child.Dispose()
                                                            """);
        child.Succeeded.Should().BeTrue(child.Payload);
        File.ReadAllText(Path.Combine(first, "child.txt")).Should().Contain("inherited");
    }

    [Fact]
    public async Task Clearing_workspaces_keeps_journals_and_recreates_the_directory()
    {
        await using Fixture fixture = new();
        await fixture.Service.CreateAsync(AgentSessionLaunchParameters.Create("test", sessionId: "one"));
        (await Run(fixture.Repl, "one",
                "let oldBinding = 3\nSystem.IO.File.WriteAllText(\"scratch.txt\", \"temporary\")"))
            .Succeeded.Should().BeTrue();
        await fixture.Service.ClearWorkingDirectoriesAsync();
        Directory.Exists(fixture.Workspaces.Resolve("one")).Should().BeFalse();
        fixture.Store.Exists("one").Should().BeTrue();
        (await Run(fixture.Repl, "one", "oldBinding")).Succeeded.Should().BeFalse();
        (await Run(fixture.Repl, "one", "System.IO.File.WriteAllText(\"new.txt\", \"recreated\")"))
            .Succeeded.Should().BeTrue();
        File.ReadAllText(Path.Combine(fixture.Workspaces.Resolve("one"), "new.txt")).Should().Be("recreated");
    }

    [Fact]
    public async Task Purge_removes_the_work_directory_and_journal_without_touching_another_session()
    {
        await using Fixture fixture = new();
        foreach (string id in new[] { "one", "two" })
        {
            await fixture.Service.CreateAsync(AgentSessionLaunchParameters.Create("test", sessionId: id));
            (await Run(fixture.Repl, id, "System.IO.File.WriteAllText(\"scratch.txt\", \"content\")"))
                .Succeeded.Should().BeTrue();
        }

        (await fixture.Service.PurgeAsync("one")).Should().BeTrue();
        fixture.Store.Exists("one").Should().BeFalse();
        Directory.Exists(fixture.Workspaces.Resolve("one")).Should().BeFalse();
        fixture.Store.Exists("two").Should().BeTrue();
        File.Exists(Path.Combine(fixture.Workspaces.Resolve("two"), "scratch.txt")).Should().BeTrue();
        (await fixture.Service.PurgeAsync("one")).Should().BeFalse();
    }

    [Fact]
    public async Task Reopened_sessions_reuse_their_device_local_workspace_and_can_be_purged_while_closed()
    {
        await using Fixture fixture = new();
        await fixture.Service.CreateAsync(AgentSessionLaunchParameters.Create("test", sessionId: "one"));
        string path = fixture.Service.GetWorkingDirectory("one");
        File.WriteAllText(Path.Combine(path, "scratch.txt"), "retained");
        await fixture.Service.DisposeAsync();
        await using AgentSessionService reopened = new(fixture.Store, new IdleInterpreter(),
            workspaces: fixture.Workspaces);
        await reopened.TryOpenAsync("one");
        reopened.GetWorkingDirectory("one").Should().Be(path);
        File.ReadAllText(Path.Combine(path, "scratch.txt")).Should().Be("retained");
        await reopened.DisposeAsync();
        await using AgentSessionService cold = new(fixture.Store, new IdleInterpreter(),
            workspaces: fixture.Workspaces);
        (await cold.PurgeAsync("one")).Should().BeTrue();
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Libraries_with_the_same_session_id_have_separate_scratch_directories()
    {
        await using Fixture first = new();
        AgentWorkspaceStore second = new(new AgentSessionStore(Path.Combine(first.Root, "second-journal")),
            Path.Combine(first.Root, "temp"));
        first.Workspaces.Resolve("one").Should().NotBe(second.Resolve("one"));
        string firstDirectory = first.Workspaces.Ensure("one");
        string secondDirectory = second.Ensure("one");
        File.WriteAllText(Path.Combine(firstDirectory, "scratch.txt"), "first");
        File.WriteAllText(Path.Combine(secondDirectory, "scratch.txt"), "second");
        first.Workspaces.Delete("one");
        File.ReadAllText(Path.Combine(secondDirectory, "scratch.txt")).Should().Be("second");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task Invalid_session_ids_cannot_become_cleanup_paths(string sessionId)
    {
        await using Fixture fixture = new();
        AgentWorkspaceStore workspaces = fixture.Workspaces;
        Action delete = () => workspaces.Delete(sessionId);
        delete.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Workspace_links_are_removed_without_deleting_their_targets()
    {
        await using Fixture fixture = new();
        string workspace = fixture.Workspaces.Ensure("one");
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "retained");
        string link = Path.Combine(workspace, "linked");
        if (OperatingSystem.IsWindows())
        {
            // NTFS directory junctions exercise reparse-point cleanup without symlink privileges.
            string script = "$ErrorActionPreference = 'Stop'\nNew-Item -ItemType Junction -Path '" +
                            link.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" +
                            outside.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null";
            ProcessStartInfo options = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true
            };
            options.ArgumentList.Add("-NoProfile");
            options.ArgumentList.Add("-NonInteractive");
            options.ArgumentList.Add("-EncodedCommand");
            options.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using Process child = Process.Start(options)!;
            string error = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            child.ExitCode.Should().Be(0, error);
        }
        else
        {
            Directory.CreateSymbolicLink(link, outside);
        }

        fixture.Workspaces.Delete("one").Should().BeTrue();
        File.ReadAllText(Path.Combine(outside, "keep.txt")).Should().Be("retained");
    }

    [Fact]
    public async Task Cancellation_stops_the_worker_and_allows_a_fresh_evaluation()
    {
        await using AgentFsiRepl repl = new();
        (await Run(repl, "one", "let initialized = 1")).Succeeded.Should().BeTrue();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));
        Task<AgentToolOutcome> execution = repl.ExecuteAsync("one",
            JsonSerializer.Serialize(new { code = "while true do System.Threading.Thread.Sleep(50)" }),
            cancellation.Token);
        Func<Task> infinite = () => execution;
        await infinite.Should().ThrowAsync<OperationCanceledException>();
        (await Run(repl, "one", "printfn \"restarted\"")).Payload.Should().Contain("restarted");
    }

    [Fact]
    public async Task Purge_stops_a_running_repl_before_deleting_its_directory()
    {
        await using Fixture fixture = new();
        await fixture.Service.CreateAsync(AgentSessionLaunchParameters.Create("test", sessionId: "one"));
        string directory = fixture.Workspaces.Resolve("one");
        Task<AgentToolOutcome> running = Run(fixture.Repl, "one",
            "System.IO.File.WriteAllText(\"entered.txt\", \"entered\")\nwhile true do System.Threading.Thread.Sleep(50)");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(directory, "entered.txt")))
        {
            await Task.Delay(20, timeout.Token);
            if (running.IsCompleted)
            {
                (await running).Succeeded.Should().BeTrue("the script should reach its loop");
            }
        }

        (await fixture.Service.PurgeAsync("one").WaitAsync(timeout.Token)).Should().BeTrue();
        (await running.WaitAsync(timeout.Token)).Succeeded.Should().BeFalse();
        Directory.Exists(directory).Should().BeFalse();
        fixture.Store.Exists("one").Should().BeFalse();
    }

    private static Task<AgentToolOutcome> Run(AgentFsiRepl repl, string id, string code)
    {
        return repl.ExecuteAsync(id, JsonSerializer.Serialize(new { code }), CancellationToken.None);
    }

    private sealed class IdleInterpreter : IAgentEffectInterpreter
    {
        public Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentEffectOutcome(AgentEffectDisposition.Deferred, null, false, null, "idle"));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "patchouli-workspace-tests-" + Guid.NewGuid().ToString("N"));
            Store = new AgentSessionStore(Path.Combine(Root, "journals"));
            Workspaces = new AgentWorkspaceStore(Store, Path.Combine(Root, "temp"));
            Repl = new AgentFsiRepl(Workspaces);
            Service = new AgentSessionService(Store, new IdleInterpreter(), fsi: Repl, workspaces: Workspaces);
        }

        public string Root { get; }
        public AgentSessionStore Store { get; }
        public AgentWorkspaceStore Workspaces { get; }
        public AgentFsiRepl Repl { get; }
        public AgentSessionService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Workspaces.Clear();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
