using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Infrastructure.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: <see cref="SystemProcessRunner"/> captures stdout/stderr, reports non-zero exits and
/// enforces the per-request timeout and cancellation without leaving a child process behind.
/// </summary>
public sealed class SystemProcessRunnerTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();
    private readonly SystemProcessRunner _runner = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task Standard_output_is_captured()
    {
        AgentProcessResult result = await _runner.RunAsync(Command("echo patchouli-runner-ok"));

        result.Started.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(0);
        result.Succeeded.Should().BeTrue();
        result.StandardOutput.Should().Contain("patchouli-runner-ok");
        result.StandardError.Should().BeEmpty();
        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Standard_error_is_captured()
    {
        AgentProcessResult result = await _runner.RunAsync(Command("echo patchouli-runner-err 1>&2"));

        result.Succeeded.Should().BeTrue();
        result.StandardError.Should().Contain("patchouli-runner-err");
        result.StandardOutput.Should().NotContain("patchouli-runner-err");
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_reported_without_a_failure_reason()
    {
        AgentProcessResult result = await _runner.RunAsync(Command("exit 3"));

        result.Started.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(3);
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task A_timeout_kills_the_child_and_reports_the_elapsed_timeout()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));

        AgentProcessResult result = await _runner.RunAsync(
            Command(SlowCommand(), TimeSpan.FromMilliseconds(300)), cancellation.Token);

        result.Started.Should().BeTrue();
        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        result.Failure.Should().Contain("超时");
    }

    [Fact]
    public async Task Cancellation_kills_the_child_and_throws()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));
        CancellationToken cancellationToken = cancellation.Token;

        Func<Task> act = async () => await _runner.RunAsync(Command(SlowCommand()), cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_pre_cancelled_token_throws_before_starting_a_process()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        CancellationToken cancellationToken = cancellation.Token;

        Func<Task> act = async () => await _runner.RunAsync(Command("echo never"), cancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_blank_executable_name_is_reported_as_not_started()
    {
        AgentProcessResult result = await _runner.RunAsync(new AgentProcessRequest("  ", []));

        result.Started.Should().BeFalse();
        result.Failure.Should().Be("可执行文件名为空。");
    }

    [Fact]
    public async Task A_missing_executable_is_reported_as_not_started()
    {
        AgentProcessResult result = await _runner.RunAsync(
            new AgentProcessRequest(_directory.Combine("missing-agent.exe"), ["--version"]));

        result.Started.Should().BeFalse();
        result.Failure.Should().NotBeNullOrWhiteSpace();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_working_directory_is_applied_to_the_child()
    {
        string workingDirectory = Directory.CreateDirectory(_directory.Combine("workdir")).FullName;

        AgentProcessResult result = await _runner.RunAsync(
            Command(OperatingSystem.IsWindows() ? "cd" : "pwd", workingDirectory: workingDirectory));

        result.Succeeded.Should().BeTrue();
        result.StandardOutput.Trim().Should().Be(Path.GetFullPath(workingDirectory));
    }

    [Fact]
    public async Task Environment_overrides_are_visible_to_the_child()
    {
        string name = "PATCHOULI_RUNNER_" + Guid.NewGuid().ToString("N");
        IReadOnlyDictionary<string, string?> environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [name] = "value-from-test"
        };
        string echo = OperatingSystem.IsWindows() ? $"echo %{name}%" : $"printf '%s\\n' \"${name}\"";

        AgentProcessResult result = await _runner.RunAsync(Command(echo, environment: environment));

        result.Succeeded.Should().BeTrue();
        result.StandardOutput.Should().Contain("value-from-test");
    }

    [Fact]
    public async Task A_null_environment_value_removes_the_variable_from_the_child()
    {
        string name = "PATCHOULI_RUNNER_REMOVE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "outer-value");
        try
        {
            IReadOnlyDictionary<string, string?> environment =
                new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = null };
            string echo = OperatingSystem.IsWindows()
                ? $"echo [%{name}%]"
                : $"printf '[%s]\\n' \"${{{name}:-}}\"";

            AgentProcessResult result = await _runner.RunAsync(Command(echo, environment: environment));

            result.Succeeded.Should().BeTrue();
            result.StandardOutput.Should().NotContain("outer-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static AgentProcessRequest Command(string command, TimeSpan? timeout = null,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        return OperatingSystem.IsWindows()
            ? new AgentProcessRequest("cmd.exe", ["/d", "/s", "/c", command], workingDirectory, environment,
                timeout)
            : new AgentProcessRequest("/bin/sh", ["-c", command], workingDirectory, environment, timeout);
    }

    private static string SlowCommand()
    {
        return OperatingSystem.IsWindows()
            ? "ping -n 20 127.0.0.1 >nul"
            : "sleep 5";
    }
}
