using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>Issue #53 foreground-activation preference: persisted default true, a false value only
/// suppresses window activation for external protocol links, and minimized/tray windows get
/// attention feedback instead of silent background navigation. CAYW remains independent.</summary>
[Collection("RetainedTabUI")]
public sealed class ProtocolActivationPreferenceTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Default_is_foreground_and_the_preference_roundtrips_through_the_settings_store()
    {
        UiPreferences.Default().ActivateWindowOnProtocolOpen.Should().BeTrue();

        await using (MainWindowViewModel main = new(settingsPath: _settings.Path))
        {
            main.Settings.VirtualLinkSettings.ActivateWindowOnProtocolOpen.Should().BeTrue();
            main.Settings.VirtualLinkSettings.ActivateWindowOnProtocolOpen = false;
            main.AppOptions.Ui.ActivateWindowOnProtocolOpen.Should().BeFalse();
            await main.SettingsStore.FlushAsync();
        }

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Ui.ActivateWindowOnProtocolOpen.Should().BeFalse();

        PatchouliAppSettings fieldSaved = reloaded with
        {
            Ui = reloaded.Ui with { ReadingFontSize = 17 }
        };
        (await fieldSaved.SaveFieldLevelAsync(_settings.Path,
            new HashSet<string>(StringComparer.Ordinal) { "Ui" })).IsSuccess.Should().BeTrue();
        PatchouliAppSettings afterFieldSave = PatchouliAppSettings.Load(_settings.Path);
        afterFieldSave.Ui.ActivateWindowOnProtocolOpen.Should().BeFalse(
            "a field-level Ui save must write the new preference");
        afterFieldSave.Ui.ReadingFontSize.Should().Be(17);
    }

    [Fact]
    public async Task False_keeps_a_minimized_window_minimized_and_requests_attention()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: _settings.Path);
            MainWindow window = new(main);
            int attentionRequests = 0;
            WindowAttentionService.TestHook = _ => attentionRequests++;
            try
            {
                window.Show();
                window.WindowState = WindowState.Minimized;
                main.UpdateAppOptions(main.AppOptions with
                {
                    Ui = main.AppOptions.Ui with { ActivateWindowOnProtocolOpen = false }
                });

                window.OpenProtocolUri("patchouli://library");
                Dispatcher.UIThread.RunJobs();

                attentionRequests.Should().Be(1);
                window.WindowState.Should().Be(WindowState.Minimized,
                    "the background preference never activates the primary window");
            }
            finally
            {
                WindowAttentionService.TestHook = null;
                window.Close();
                await main.ShutdownAsync();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Default_true_activates_the_window_and_CAYW_stays_independent()
    {
        string bridge = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "CitationInterop",
            "DesktopCitationBridge.cs"));
        bridge.Should().NotContain("ActivateWindowOnProtocolOpen");
        string mainWindow = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI",
            "MainWindow.axaml.cs"));
        mainWindow.Should().Contain("ActivateWindowOnProtocolOpen")
            .And.Contain("WindowAttentionService.RequestAttention");

        await headless.Session.Dispatch<int>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: _settings.Path);
            MainWindow window = new(main);
            int attentionRequests = 0;
            WindowAttentionService.TestHook = _ => attentionRequests++;
            try
            {
                window.Show();
                window.WindowState = WindowState.Minimized;
                main.AppOptions.Ui.ActivateWindowOnProtocolOpen.Should().BeTrue();

                window.OpenProtocolUri("patchouli://library");
                Dispatcher.UIThread.RunJobs();

                window.WindowState.Should().Be(WindowState.Normal);
                attentionRequests.Should().Be(0);
            }
            finally
            {
                WindowAttentionService.TestHook = null;
                window.Close();
                await main.ShutdownAsync();
            }

            return 0;
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
