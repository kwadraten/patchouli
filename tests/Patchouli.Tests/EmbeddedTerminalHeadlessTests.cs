using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAssertions;
using Iciclecreek.Terminal;
using Patchouli.Terminal;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: headless smoke tests for the PTY-backed <see cref="EmbeddedTerminal"/>. Every test is
/// bounded by a 30 second <see cref="CancellationTokenSource"/>; the terminal is always stopped and
/// disposed in a finally block so a failure cannot leave a child process or PTY host behind. The
/// shared session keeps the terminal's dispatcher timer on the thread that owns the control.
/// </summary>
[Collection("RetainedTabUI")]
public sealed class EmbeddedTerminalHeadlessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private readonly AvaloniaSharedSessionFixture _headless;

    public EmbeddedTerminalHeadlessTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public async Task A_url_click_forwards_once_per_activation_with_the_actual_target()
    {
        await _headless.Session.Dispatch<bool>(() =>
        {
            EmbeddedTerminal terminal = new();
            List<(object? Sender, string Url, bool FromSequence)> received = [];
            terminal.LinkActivated += (sender, args) => received.Add((sender, args.Url, args.FromSequence));
            try
            {
                // Two sequential gestures: each UrlClicked report is forwarded exactly once, and the
                // payload carries the actual target (for OSC 8 this need not match the display text).
                RaiseUrlClicked(terminal, "https://example.org/paper?doi=10.1000/test");
                RaiseUrlClicked(terminal, "patchouli://items/00000000-0000-0000-0000-000000000001.yml",
                    true);
            }
            finally
            {
                terminal.Dispose();
            }

            received.Should().HaveCount(2);
            received[0].Sender.Should().BeSameAs(terminal);
            received[0].Url.Should().Be("https://example.org/paper?doi=10.1000/test");
            received[0].FromSequence.Should().BeFalse();
            received[1].Sender.Should().BeSameAs(terminal);
            received[1].Url.Should().Be("patchouli://items/00000000-0000-0000-0000-000000000001.yml");
            received[1].FromSequence.Should().BeTrue();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_disposed_terminal_no_longer_forwards_url_clicks()
    {
        await _headless.Session.Dispatch<bool>(() =>
        {
            EmbeddedTerminal terminal = new();
            int received = 0;
            terminal.LinkActivated += (_, _) => received++;
            terminal.Dispose();

            RaiseUrlClicked(terminal, "https://example.org/paper");

            received.Should().Be(0, "Dispose unsubscribes from the control and gates forwarding");
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Simulates a Ctrl+click the way the terminal control reports it: UrlClicked is a field-like
    /// event on <see cref="TerminalControl"/>, so a test raises it through the compiler-generated
    /// backing delegate. A missing delegate means nobody is subscribed, which is itself observable.
    /// </summary>
    private static void RaiseUrlClicked(EmbeddedTerminal terminal, string url, bool fromSequence = false)
    {
        EventHandler<UrlClickedEventArgs>? handlers = typeof(TerminalControl)
            .GetField("UrlClicked", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(terminal.TerminalControl) as EventHandler<UrlClickedEventArgs>;
        handlers?.Invoke(terminal.TerminalControl, new UrlClickedEventArgs(url, fromSequence));
    }

    [TerminalPtyFact]
    public async Task The_default_shell_echoes_a_command_and_stop_terminates_its_process()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await _headless.Session.Dispatch(async () =>
        {
            using AgentTestDirectory directory = new();
            EmbeddedTerminal terminal = new();
            StringBuilder output = new();
            terminal.TextOutputReceived += (_, args) =>
            {
                lock (output)
                {
                    output.Append(args.Text);
                }
            };
            Window window = new() { Content = terminal, Width = 900, Height = 600 };
            try
            {
                ShowAndLayout(window);
                ShellCommand shell = DefaultShellResolver.Resolve();
                await terminal.StartAsync(new TerminalSessionOptions
                {
                    Executable = shell.Executable,
                    Arguments = shell.Arguments,
                    WorkingDirectory = directory.Path,
                    InitialCols = 80,
                    InitialRows = 24
                }, cancellation.Token);

                terminal.IsRunning.Should().BeTrue();
                terminal.Pid.Should().BeGreaterThan(0);
                terminal.SessionId.Should().BeGreaterThan(0);

                await terminal.WriteInputAsync(MarkerCommand(shell) + "\r", cancellation.Token);

                bool printed = await WaitUntilAsync(
                    () => TextContains(output, "PATCHOULI_42"), cancellation.Token);
                printed.Should().BeTrue(
                    $"the shell should execute the command; captured output was: {Snapshot(output)}");

                int processId = terminal.Pid;
                terminal.Stop();
                (await terminal.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellation.Token))
                    .Should().BeTrue("Stop must terminate the hosted process");
                terminal.IsRunning.Should().BeFalse();
                (await WaitUntilAsync(() => !ProcessExists(processId), cancellation.Token,
                        TimeSpan.FromSeconds(5)))
                    .Should().BeTrue("the operating-system process must be gone after Stop");
            }
            finally
            {
                CloseTerminal(terminal, window);
            }
        }, cancellation.Token);
    }

    [TerminalPtyFact]
    public async Task Chinese_utf8_output_is_decoded_across_reads()
    {
        string? pwsh = DefaultShellResolver.FindOnPath(
            OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null)
        {
            return;
        }

        using CancellationTokenSource cancellation = new(TestTimeout);
        await _headless.Session.Dispatch(async () =>
        {
            const string marker = "中文终端测试：书目检索";
            EmbeddedTerminal terminal = new();
            StringBuilder output = new();
            terminal.TextOutputReceived += (_, args) =>
            {
                lock (output)
                {
                    output.Append(args.Text);
                }
            };
            Window window = new() { Content = terminal, Width = 900, Height = 600 };
            try
            {
                ShowAndLayout(window);
                await terminal.StartAsync(new TerminalSessionOptions
                {
                    Executable = pwsh,
                    Arguments =
                    [
                        "-NoLogo",
                        "-NoProfile",
                        "-Command",
                        $"[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); Write-Output '{marker}'"
                    ],
                    InitialCols = 80,
                    InitialRows = 24
                }, cancellation.Token);

                bool printed = await WaitUntilAsync(
                    () => TextContains(output, marker), cancellation.Token);
                printed.Should().BeTrue(
                    $"UTF-8 Chinese output must survive chunked decoding; captured output was: {Snapshot(output)}");
                (await terminal.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellation.Token))
                    .Should().BeTrue();
            }
            finally
            {
                CloseTerminal(terminal, window);
            }
        }, cancellation.Token);
    }

    [TerminalPtyFact]
    public async Task The_configured_working_directory_is_the_shell_working_directory()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await _headless.Session.Dispatch(async () =>
        {
            using AgentTestDirectory directory = new();
            EmbeddedTerminal terminal = new();
            StringBuilder output = new();
            terminal.TextOutputReceived += (_, args) =>
            {
                lock (output)
                {
                    output.Append(args.Text);
                }
            };
            Window window = new() { Content = terminal, Width = 900, Height = 600 };
            try
            {
                ShowAndLayout(window);
                bool windows = OperatingSystem.IsWindows();
                await terminal.StartAsync(new TerminalSessionOptions
                {
                    Executable = windows ? "cmd.exe" : "/bin/sh",
                    Arguments = windows ? ["/d", "/s", "/c", "cd"] : ["-c", "pwd"],
                    WorkingDirectory = directory.Path,
                    InitialCols = 80,
                    InitialRows = 24
                }, cancellation.Token);

                string expected = Path.GetFullPath(directory.Path);
                bool printed = await WaitUntilAsync(
                    () => TextContains(output, expected), cancellation.Token);
                printed.Should().BeTrue(
                    $"the PTY child should start in {expected}; captured output was: {Snapshot(output)}");
            }
            finally
            {
                CloseTerminal(terminal, window);
            }
        }, cancellation.Token);
    }

    [TerminalPtyFact]
    public async Task Starting_a_second_process_while_one_is_running_is_rejected()
    {
        using CancellationTokenSource cancellation = new(TestTimeout);
        await _headless.Session.Dispatch(async () =>
        {
            EmbeddedTerminal terminal = new();
            Window window = new() { Content = terminal, Width = 900, Height = 600 };
            try
            {
                ShowAndLayout(window);
                ShellCommand shell = DefaultShellResolver.Resolve();
                TerminalSessionOptions options = new()
                {
                    Executable = shell.Executable,
                    Arguments = shell.Arguments,
                    InitialCols = 80,
                    InitialRows = 24
                };
                await terminal.StartAsync(options, cancellation.Token);

                Task secondStart = terminal.StartAsync(options, cancellation.Token);
                Exception? exception = await Record.ExceptionAsync(() => secondStart);
                exception.Should().BeOfType<InvalidOperationException>();
                terminal.IsRunning.Should().BeTrue("the rejected start must not disturb the live session");
            }
            finally
            {
                CloseTerminal(terminal, window);
            }
        }, cancellation.Token);
    }

    private static void CloseTerminal(EmbeddedTerminal terminal, Window window)
    {
        terminal.Stop();
        terminal.WaitForExit(TimeSpan.FromSeconds(10));
        terminal.Dispose();
        window.Close();

        // Let the throttled render flush (a dispatcher timer) run on the thread that owns the
        // control. RunJobs alone does not advance timers, so run a bounded nested main loop, then
        // stop any timer the terminal library left registered.
        using (CancellationTokenSource drain = new(TimeSpan.FromMilliseconds(250)))
        {
            try
            {
                Dispatcher.UIThread.MainLoop(drain.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        StopUnitTestTimers();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Iciclecreek's terminal keeps a render-throttle <see cref="DispatcherTimer"/> alive after the
    /// control is disposed. Avalonia exposes no public API to clear it, so use the same timer
    /// snapshot hook the headless test session uses. Without this, the timer executes while the next
    /// test session resets the dispatcher and throws a cross-thread access exception.
    /// </summary>
    private static void StopUnitTestTimers()
    {
        MethodInfo? snapshot = typeof(Dispatcher).GetMethod("SnapshotTimersForUnitTests",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (snapshot?.Invoke(null, null) is not IEnumerable timers)
        {
            return;
        }

        foreach (DispatcherTimer timer in timers)
        {
            timer.Stop();
        }
    }

    private static void ShowAndLayout(Window window)
    {
        window.Show();
        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static string MarkerCommand(ShellCommand shell)
    {
        string name = Path.GetFileNameWithoutExtension(shell.Executable);
        if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            return "Write-Output ('PATCHOULI_' + (6*7))";
        }

        if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            return "for /f \"delims=\" %i in ('echo 42') do @echo PATCHOULI_%i";
        }

        return "printf 'PATCHOULI_%s\\n' \"$((6*7))\"";
    }

    private static bool TextContains(StringBuilder output, string text)
    {
        lock (output)
        {
            return output.ToString().Contains(text, StringComparison.Ordinal);
        }
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            string text = output.ToString();
            return text.Length <= 2000 ? text : text[^2000..];
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TestTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, cancellationToken);
        }

        return condition();
    }

    private static bool ProcessExists(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
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
}
