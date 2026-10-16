using System.Text.Json;
using FluentAssertions;
using Patchouli.Host.Agent;

namespace Patchouli.Tests;

public sealed class AgentFsiReplTests
{
    [Fact]
    public async Task Console_output_is_isolated_and_system_commands_return_their_output()
    {
        await using AgentFsiRepl repl = new();
        AgentToolOutcome[] parallel = await Task.WhenAll(
            Run(repl, "one", "System.Console.WriteLine(\"only-one\")\nSystem.Console.Error.WriteLine(\"error-one\")"),
            Run(repl, "two", "System.Console.WriteLine(\"only-two\")"));
        parallel[0].Payload.Should().Contain("only-one").And.Contain("error-one").And.NotContain("only-two");
        parallel[1].Payload.Should().Contain("only-two").And.NotContain("only-one").And.NotContain("error-one");
        AgentToolOutcome command = await Run(repl, "one", """
                                                          let options =
                                                              if System.OperatingSystem.IsWindows() then
                                                                  System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo fsi-command-output")
                                                              else
                                                                  System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"echo fsi-command-output\"")
                                                          options.UseShellExecute <- false
                                                          options.CreateNoWindow <- true
                                                          options.RedirectStandardOutput <- true
                                                          let child = System.Diagnostics.Process.Start(options)
                                                          System.Console.WriteLine(child.StandardOutput.ReadToEnd())
                                                          child.WaitForExit()
                                                          child.Dispose()
                                                          """);
        command.Succeeded.Should().BeTrue(command.Payload);
        command.Payload.Should().Contain("fsi-command-output");
    }

    [Fact]
    public async Task Purging_a_repl_discards_bindings_and_next_call_starts_a_fresh_worker()
    {
        await using AgentFsiRepl repl = new();
        (await Run(repl, "one", "let privateValue = 17")).Succeeded.Should().BeTrue();
        await repl.ForgetAsync("one");
        (await Run(repl, "one", "privateValue")).ErrorCode.Should().Be("FSI_EVALUATION_FAILED");
        (await Run(repl, "one", "printfn \"fresh-worker\"")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Fsi_is_available_without_setup_and_preserves_isolated_bindings()
    {
        await using AgentFsiRepl repl = new();
        (await Run(repl, "one", "let answer = 41\nprintfn \"first-output\"")).Succeeded.Should().BeTrue();
        AgentToolOutcome second = await Run(repl, "one", "printfn \"next=%d\" (answer + 1)");
        second.Succeeded.Should().BeTrue(second.Payload);
        second.Payload.Should().Contain("next=42").And.NotContain("first-output");
        (await Run(repl, "two", "printfn \"%d\" answer")).Succeeded.Should().BeFalse();
        (await Run(repl, "one", "printfn \"%d\" answer")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Same_session_contexts_have_isolated_bindings_and_each_context_survives_activations()
    {
        string sessionsRoot = Path.Combine(Path.GetTempPath(),
            "patchouli-fsi-context-sessions-" + Guid.NewGuid().ToString("N"));
        AgentSessionStore sessions = new(sessionsRoot);
        AgentSessionLaunchParameters launch = AgentSessionLaunchParameters.Create(
            AgentSessionService.ChatWorkflowUri, new Dictionary<string, string>());
        await sessions.CreateSessionAsync(launch, CancellationToken.None);
        AgentWorkspaceStore workspaces = new(sessions);
        await using AgentFsiRepl repl = new(workspaces);
        try
        {
            (await Run(repl, launch.SessionId, "context-a", "let answer = 41")).Succeeded.Should().BeTrue();
            AgentToolOutcome otherContext = await Run(repl, launch.SessionId, "context-b", "answer");
            otherContext.Succeeded.Should().BeFalse();
            otherContext.ErrorCode.Should().Be("FSI_EVALUATION_FAILED");

            (await Run(repl, launch.SessionId, "context-b", "let answer = 7")).Succeeded.Should().BeTrue();
            AgentToolOutcome continuedA =
                await Run(repl, launch.SessionId, "context-a", "printfn \"a=%d\" (answer + 1)");
            AgentToolOutcome continuedB = await Run(repl, launch.SessionId, "context-b", "printfn \"b=%d\" answer");
            continuedA.Succeeded.Should().BeTrue(continuedA.Payload);
            continuedA.Payload.Should().Contain("a=42").And.NotContain("b=7");
            continuedB.Succeeded.Should().BeTrue(continuedB.Payload);
            continuedB.Payload.Should().Contain("b=7").And.NotContain("a=42");
        }
        finally
        {
            await repl.ForgetAsync(launch.SessionId);
            workspaces.Delete(launch.SessionId);
            if (Directory.Exists(sessionsRoot))
            {
                Directory.Delete(sessionsRoot, true);
            }
        }
    }

    [Fact]
    public async Task Repl_executes_dotnet_io_and_recovers_after_compile_failure()
    {
        await using AgentFsiRepl repl = new();
        string path = Path.Combine(Path.GetTempPath(), "patchouli-repl-" + Guid.NewGuid().ToString("N"));
        try
        {
            string literal = "@\"" + path.Replace("\"", "\"\"") + "\"";
            AgentToolOutcome written = await Run(repl, "io", $"System.IO.File.WriteAllText({literal}, \"executed\")");
            written.Succeeded.Should().BeTrue(written.Payload);
            File.ReadAllText(path).Should().Be("executed");
            (await Run(repl, "io", "let broken : int = \"text\"")).ErrorCode.Should().Be("FSI_EVALUATION_FAILED");
            (await Run(repl, "io", "printfn \"recovered\"")).Payload.Should().Contain("recovered");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"code\":0}")]
    public async Task Invalid_arguments_are_tool_failures(string arguments)
    {
        await using AgentFsiRepl repl = new();
        (await repl.ExecuteAsync("one", arguments, CancellationToken.None)).ErrorCode.Should().Be("INVALID_ARGUMENT");
    }

    private static Task<AgentToolOutcome> Run(AgentFsiRepl repl, string sessionId, string code)
    {
        return repl.ExecuteAsync(sessionId, JsonSerializer.Serialize(new { code }), CancellationToken.None);
    }

    private static Task<AgentToolOutcome> Run(AgentFsiRepl repl, string sessionId, string contextId, string code)
    {
        return repl.ExecuteAsync(sessionId, contextId, JsonSerializer.Serialize(new { code }), null,
            CancellationToken.None);
    }
}
