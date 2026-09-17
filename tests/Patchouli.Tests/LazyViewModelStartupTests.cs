using System.Reflection;
using FluentAssertions;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class LazyViewModelStartupTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public void Main_window_constructs_optional_pages_only_when_they_are_first_requested()
    {
        using MainWindowViewModel main = new(settingsPath: _settings.Path);

        ReadField(main, "_settingsViewModel").Should().BeNull();
        ReadField(main, "_searchEvidenceViewModel").Should().BeNull();
        ReadField(main, "_ocrQueueViewModel").Should().BeNull();
        ReadField(main, "_snapshot").Should().BeNull();
        ReadField(main, "_about").Should().BeNull();

        _ = main.Settings;
        _ = main.SearchEvidence;
        _ = main.OcrQueue;
        _ = main.Snapshot;
        _ = main.About;

        ReadField(main, "_settingsViewModel").Should().NotBeNull();
        ReadField(main, "_searchEvidenceViewModel").Should().NotBeNull();
        ReadField(main, "_ocrQueueViewModel").Should().NotBeNull();
        ReadField(main, "_snapshot").Should().NotBeNull();
        ReadField(main, "_about").Should().NotBeNull();
    }

    [Fact]
    public void Toolbar_search_state_does_not_construct_the_search_page()
    {
        using MainWindowViewModel main = new(settingsPath: _settings.Path);

        main.ToolbarSearchQuery = "patchouli";
        main.ToolbarSearchMode = main.ToolbarSearchModeOptions[0];

        ReadField(main, "_searchEvidenceViewModel").Should().BeNull();
        main.SearchEvidence.Query.Should().Be("patchouli");
        main.SearchEvidence.SelectedMode.Should().Be(main.ToolbarSearchModeOptions[0]);
    }

    private static object? ReadField(MainWindowViewModel viewModel, string name)
    {
        return typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel);
    }
}
