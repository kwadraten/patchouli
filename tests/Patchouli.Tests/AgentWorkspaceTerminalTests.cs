using Avalonia;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAssertions;
using Iciclecreek.Terminal;
using Patchouli.Core.Agents;
using Patchouli.Core.Results;
using Patchouli.Host.Agents;
using Patchouli.Host.Composition;
using Patchouli.Terminal;
using Patchouli.Testing;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Agents;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class AgentWorkspaceTerminalTests(AvaloniaSharedSessionFixture headless)
{
    [TerminalPtyFact]
    public async Task Native_and_shim_launches_receive_the_verified_workspace_temporary_directory()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await headless.Session.Dispatch<bool>(async () =>
        {
            using AgentTestDirectory directory = new();
            string pwsh = DefaultShellResolver.FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")!;
            string script = directory.CreateFile("check-temp.ps1", """
                                                                   param([string]$Receipt)
                                                                   $ErrorActionPreference = 'Stop'
                                                                   $probe = [IO.Path]::GetTempFileName()
                                                                   [IO.File]::Delete($probe)
                                                                   $paths = @($env:TEMP, $env:TMP, $env:TMPDIR,
                                                                       ([IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)))
                                                                   [IO.File]::WriteAllLines($Receipt, [string[]]$paths)
                                                                   """);
            string shim = directory.CreateFile("agent.cmd", $"@\"{pwsh}\" -NoLogo -NoProfile -File \"{script}\" %*");
            FakeAgentPlatformEnvironment environment = new() { HomeDirectory = directory.Combine("home") };
            FakeAgentWorkspaceLocator workspace = new() { Directory = directory.Combine("library", "agent-workspace") };
            ScriptedProcessRunner runner = new() { Handler = _ => ScriptedProcessRunner.Output("2.1.2") };
            AgentSkillLocations locations = new(environment.HomeDirectory, directory.Combine("state"),
                new Dictionary<string, string>());
            AgentLaunchService service = new(new AgentDetectionService(runner, environment), workspace,
                new AgentSkillService(locations));
            string[] executables = OperatingSystem.IsWindows() ? [pwsh, shim] : [pwsh];
            foreach (string executable in executables)
            {
                environment.ExistingFiles.Add(executable);
                string receipt = directory.Combine($"receipt-{Path.GetFileName(executable)}.txt");
                Result<AgentLaunchSpec> prepared = await service.PrepareLaunchAsync(
                    AgentIntegrationAppSettings.Default().WithCustomPath("opencode", executable),
                    new AgentLaunchContext(Environment: new Dictionary<string, string>
                    {
                        ["TEMP"] = directory.Combine("missing-temp"),
                        ["TMP"] = directory.Combine("missing-tmp"),
                        ["TMPDIR"] = directory.Combine("missing-tmpdir")
                    }), timeout.Token);
                prepared.IsSuccess.Should().BeTrue(prepared.ErrorMessage);
                AgentLaunchSpec spec = prepared.Value with
                {
                    Arguments = executable == pwsh ? ["-NoLogo", "-NoProfile", "-File", script, receipt] : [receipt]
                };
                TerminalSessionOptions options = (TerminalSessionOptions)typeof(AgentWorkspaceViewModel)
                    .GetMethod("CreateTerminalOptions", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [spec])!;
                EnsureTerminalTheme();
                using EmbeddedTerminal terminal = new();
                Window window = new() { Content = terminal, Width = 900, Height = 600 };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    await terminal.StartAsync(options, timeout.Token);
                    (await terminal.WaitForExitAsync(TimeSpan.FromSeconds(10), timeout.Token)).Should().BeTrue();
                    terminal.ExitCode.Should().Be(0);
                    string expected = Path.Combine(workspace.Directory, ".tmp");
                    (await File.ReadAllLinesAsync(receipt, timeout.Token)).Should()
                        .Equal(expected, expected, expected, expected);
                }
                finally
                {
                    terminal.Stop();
                    terminal.WaitForExit(TimeSpan.FromSeconds(5));
                    window.Content = null;
                    window.Close();
                }
            }

            return true;
        }, timeout.Token);
    }

    [TerminalPtyFact]
    public async Task Presets_and_generic_document_references_paste_directly_without_submitting()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("terminal-paste")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            (await services.Items.CreateItemAsync("document", "幕末における対馬と英露")).IsSuccess.Should().BeTrue();
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            AgentWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Content = page, Width = 1200, Height = 760 };
            string received = Path.Combine(Path.GetDirectoryName(main.RuntimeDatabasePath)!, "received.txt");
            try
            {
                window.Show();
                window.UpdateLayout();
                await StartReaderAsync(workspace, received, timeout.Token);
                window.Width = 1500;
                window.Height = 900;
                window.Measure(new Size(window.Width, window.Height));
                window.Arrange(new Rect(0, 0, window.Width, window.Height));
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                TerminalSize resized = workspace.Terminal!.CurrentSize;
                await workspace.OpenItemPickerCommand.ExecuteAsync();
                AgentItemReferenceViewModel item = workspace.FilteredItems.Single();
                string preset = workspace.Presets.Single(entry => entry.Key == "outline").Template;

                await workspace.ApplyPresetAsync("outline");
                await workspace.CommitSelectedItemAsync();

                workspace.IsItemPickerOpen.Should().BeFalse();
                File.Exists(received).Should().BeFalse("pasting must not submit the input line");
                await workspace.Terminal!.WriteInputAsync("\r", timeout.Token);
                (await workspace.Terminal.WaitForExitAsync(TimeSpan.FromSeconds(10), timeout.Token)).Should().BeTrue();
                (await File.ReadAllTextAsync(received, timeout.Token)).Should().Be(preset + item.ReferenceText);
                (await File.ReadAllTextAsync(received + ".size", timeout.Token))
                    .Should().Be($"{resized.Cols},{resized.Rows}", "the child PTY must follow window resizing");
                item.ReferenceText.Should().Be($"幕末における対馬と英露 (规范 URI: {item.CanonicalUri})");

                window.UpdateLayout();
                ContentControl host = page.FindControl<ContentControl>("TerminalHost")!;
                host.Bounds.Width.Should().BeGreaterThan(1180);
                host.Bounds.Height.Should().BeGreaterThan(690);
                workspace.Terminal.Bounds.Width.Should().BeGreaterThan(1180);
                workspace.Terminal.TerminalControl.Bounds.Width.Should().BeGreaterThan(1180);
                workspace.Terminal.CurrentSize.Cols.Should().BeGreaterThan(100);
                workspace.Terminal.CurrentSize.Rows.Should().BeGreaterThan(TerminalSessionOptions.DefaultInitialRows);
                string previews = Path.Combine(RepositoryTestEnvironment.TestRoot, "agent-terminal-previews");
                Directory.CreateDirectory(previews);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                frame!.Save(Path.Combine(previews, "workspace.png"));
            }
            finally
            {
                await workspace.ShutdownAsync();
                window.Content = null;
                window.Close();
            }

            return true;
        }, timeout.Token);
    }

    [TerminalPtyFact]
    public async Task Switching_and_closing_require_confirmation_and_never_retain_a_background_process()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            PatchouliAppSettings current = PatchouliAppSettings.Load(settings.Path);
            (current with { Agents = new AgentIntegrationAppSettings(false, "opencode") }).Save(settings.Path)
                .IsSuccess.Should().BeTrue();
            ChoiceDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("terminal-lifetime")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            await workspace.RefreshAsync(false);
            workspace.OnPageAttached();
            Window window = new() { Content = workspace.Terminal, Width = 900, Height = 600 };
            string received = Path.Combine(Path.GetDirectoryName(main.RuntimeDatabasePath)!, "received.txt");
            try
            {
                window.Show();
                window.UpdateLayout();
                await StartReaderAsync(workspace, received, timeout.Token);
                EmbeddedTerminal original = workspace.Terminal!;

                await workspace.SwitchAgentAsync("claude-acp");
                original.IsRunning.Should().BeTrue();
                workspace.SelectedAgent!.Id.Should().Be("opencode");
                (await workspace.ConfirmCloseAsync()).Should().BeFalse();
                original.IsRunning.Should().BeTrue();
                dialogs.Shown.Should().HaveCount(2).And.OnlyContain(dialog =>
                    !dialog.HasDiscard && dialog.Message.Contains("未完成的任务"));

                dialogs.Choice = ConfirmDialogResult.Confirm;
                await workspace.SwitchAgentAsync("claude-acp");
                original.IsRunning.Should().BeFalse();
                workspace.SelectedAgent!.Id.Should().Be("claude-acp");
                PatchouliAppSettings.Load(settings.Path).Agents.SelectedAgentId.Should().Be("claude-acp");

                workspace.OnPageAttached();
                window.Content = workspace.Terminal;
                window.UpdateLayout();
                await StartReaderAsync(workspace, received, timeout.Token);
                EmbeddedTerminal replacement = workspace.Terminal!;
                (await workspace.ConfirmCloseAsync()).Should().BeTrue();
                replacement.IsRunning.Should().BeFalse();
                workspace.OnPageClosed();
                workspace.Terminal.Should().BeNull();
                workspace.HasActiveSession.Should().BeFalse();
            }
            finally
            {
                await workspace.ShutdownAsync();
                window.Content = null;
                window.Close();
            }

            return true;
        }, timeout.Token);
    }

    [TerminalPtyFact]
    public async Task Declining_a_terminal_link_confirmation_writes_nothing_to_the_pty()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            ChoiceDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            Window window = new() { Content = workspace.Terminal, Width = 900, Height = 600 };
            string received = Path.Combine(Path.GetDirectoryName(settings.Path)!, "received.txt");
            try
            {
                window.Show();
                window.UpdateLayout();
                await StartReaderAsync(workspace, received, timeout.Token);

                RaiseUrlClicked(workspace.Terminal!, "https://example.org/declined");
                (await WaitUntilAsync(() => dialogs.Shown.Count == 1, timeout.Token)).Should().BeTrue(
                    "an external link prompts before anything else can happen");
                dialogs.Shown[0].Message.Should().Contain("https://example.org/declined");
                File.Exists(received).Should().BeFalse("nothing may reach the PTY while the dialog is up");

                await workspace.Terminal!.WriteInputAsync("\r", timeout.Token);
                (await workspace.Terminal.WaitForExitAsync(TimeSpan.FromSeconds(10), timeout.Token))
                    .Should().BeTrue();
                (await File.ReadAllTextAsync(received, timeout.Token)).Should().BeEmpty(
                    "declining the confirmation must leave the shell input line untouched");
            }
            finally
            {
                await workspace.ShutdownAsync();
                window.Content = null;
                window.Close();
            }

            return true;
        }, timeout.Token);
    }

    [TerminalPtyFact]
    public async Task Mcp_config_write_restarts_same_agent_only_after_successful_confirmation_and_write()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            ConfigDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("mcp-auto-restart")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            Window window = new() { Content = workspace.Terminal, Width = 900, Height = 600 };
            string directory = services.AgentWorkspace.EnsureWorkspaceDirectory();
            string target = Path.Combine(directory, "opencode.json");
            string receipt = Path.Combine(directory, "restarted.txt");
            try
            {
                window.Show();
                window.UpdateLayout();
                await StartReaderAsync(workspace, Path.Combine(directory, "initial.txt"), timeout.Token);
                EmbeddedTerminal terminal = workspace.Terminal!;
                int originalPid = terminal.Pid;
                string executable = DefaultShellResolver.FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")!;
                string script =
                    $"[IO.File]::WriteAllText('{receipt.Replace("'", "''")}', $env:RESTART_TEST); [Console]::ReadLine()";
                AgentLaunchSpec spec = new("opencode", "OpenCode", executable,
                    ["-NoLogo", "-NoProfile", "-Command", script], directory,
                    new Dictionary<string, string> { ["RESTART_TEST"] = "preserved-environment" }, false, null, target);
                typeof(AgentWorkspaceViewModel).GetField("_services",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(workspace, services);
                typeof(AgentWorkspaceViewModel).GetField("_launchSpec",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(workspace, spec);

                dialogs.Confirm = false;
                await workspace.WriteMcpConfigCommand.ExecuteAsync();
                terminal.Pid.Should().Be(originalPid);
                terminal.IsRunning.Should().BeTrue();
                File.Exists(target).Should().BeFalse();

                dialogs.Confirm = true;
                Directory.CreateDirectory(target);
                await workspace.WriteMcpConfigCommand.ExecuteAsync();
                terminal.Pid.Should().Be(originalPid);
                terminal.IsRunning.Should().BeTrue();
                main.Status.Should().Contain("写入 MCP 配置失败");
                Directory.Delete(target);

                await workspace.WriteMcpConfigCommand.ExecuteAsync();
                workspace.Terminal.Should().BeSameAs(terminal);
                terminal.IsRunning.Should().BeTrue();
                terminal.Pid.Should().NotBe(originalPid);
                while (!File.Exists(receipt))
                {
                    await Task.Delay(20, timeout.Token);
                }

                (await File.ReadAllTextAsync(receipt, timeout.Token)).Should().Be("preserved-environment");
                (await File.ReadAllTextAsync(target, timeout.Token)).Should().Contain("mcp");
                workspace.McpStatusText.Should().Contain("已自动重启");
                workspace.IsBusy.Should().BeFalse();
                dialogs.Shown.Should().HaveCount(3);
                dialogs.Shown.Should().OnlyContain(dialog => dialog.PermissionNotes.Any(note => note.Contains("自动重启")));
            }
            finally
            {
                await workspace.ShutdownAsync();
                window.Content = null;
                window.Close();
            }

            return true;
        }, timeout.Token);
    }

    private sealed class ConfigDialogs : IDialogService
    {
        public bool Confirm { get; set; }
        public List<AgentMcpConfigDialogViewModel> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add((AgentMcpConfigDialogViewModel)viewModel);
            return Task.FromResult(Confirm is TResult result ? result : default);
        }
    }

    private static async Task StartReaderAsync(AgentWorkspaceViewModel workspace, string received,
        CancellationToken cancellationToken)
    {
        EnsureTerminalTheme();
        Window window = (Window)TopLevel.GetTopLevel(workspace.Terminal!)!;
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        string executable = DefaultShellResolver.FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
                            ?? throw new InvalidOperationException("PowerShell is required by this PTY test.");
        TerminalSize expected = workspace.Terminal!.CurrentSize;
        string sizePath = (received + ".size").Replace("'", "''");
        string reportSize =
            $"[IO.File]::WriteAllText('{sizePath}', ([string][Console]::WindowWidth + ',' + [Console]::WindowHeight)); ";
        string script = reportSize + "[Console]::WriteLine('READY_FOR_PASTE'); $line = [Console]::ReadLine(); " +
                        $"[IO.File]::WriteAllText('{received.Replace("'", "''")}', $line); " + reportSize;
        await workspace.Terminal!.StartAsync(new TerminalSessionOptions
        {
            Executable = executable,
            Arguments = ["-NoLogo", "-NoProfile", "-Command", script],
            WorkingDirectory = Path.GetDirectoryName(received)
        }, cancellationToken);
        while (!workspace.Terminal.CaptureViewportText().Contains("READY_FOR_PASTE", StringComparison.Ordinal))
        {
            await Task.Delay(20, cancellationToken);
        }

        (await File.ReadAllTextAsync(received + ".size", cancellationToken))
            .Should().Be($"{expected.Cols},{expected.Rows}", "launch must preserve the grid calculated by layout");
        workspace.OnPageAttached();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Input);
    }

    private static void EnsureTerminalTheme()
    {
        // The terminal package caches theme registration globally, while Dispatch creates a new App.
        Uri theme = new("avares://Iciclecreek.Avalonia.Terminal/Themes/Generic.axaml");
        if (!Application.Current!.Styles.OfType<StyleInclude>().Any(style => style.Source == theme))
        {
            Application.Current.Styles.Insert(0, new StyleInclude(theme) { Source = theme });
        }
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return true;
            }

            await Task.Delay(25, cancellationToken);
        }

        return condition();
    }

    private sealed class ChoiceDialogs : IDialogService
    {
        public ConfirmDialogResult Choice { get; set; } = ConfirmDialogResult.Cancel;
        public List<ConfirmDialogViewModel> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is ConfirmDialogViewModel dialog)
            {
                Shown.Add(dialog);
            }

            return Task.FromResult(Choice is TResult result ? result : default);
        }
    }
}
