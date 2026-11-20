using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class FirstRunRecoveryTests
{
    [Theory]
    [InlineData(0, FirstRunStep.Library)]
    [InlineData(1, FirstRunStep.Scan)]
    [InlineData(2, FirstRunStep.MinerUConfig)]
    [InlineData(3, FirstRunStep.MinerUConfig)]
    [InlineData(4, FirstRunStep.Complete)]
    public async Task Startup_resumes_the_first_unfinished_page(int completedSteps, string expectedStep)
    {
        using TemporaryAppSettingsFile profile = new();
        PatchouliAppSettings settings = PatchouliAppSettings.Load(profile.Path);
        settings = settings with { Sync = settings.Sync with { DeviceId = Guid.NewGuid().ToString("D") } };
        settings.Save(profile.Path).IsSuccess.Should().BeTrue();
        HostServices services =
            await HostServices.CreateAsync(settings.Runtime.RuntimeDatabasePath, settings, profile.Path);
        try
        {
            if (completedSteps >= 1)
            {
                (await services.Library.CreateLibraryAsync("Recovery test")).IsSuccess.Should().BeTrue();
            }

            if (completedSteps >= 2)
            {
                string pdfRoot = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(profile.Path)!, "pdf"))
                    .FullName;
                Result<FileSearchRoot> root = await services.FileResolution.AddSearchRootAsync(
                    new SelectedFileSearchRoot(
                        pdfRoot, "file", FileSearchRootAuthorizationKinds.None,
                        null, null, DateTimeOffset.UtcNow));
                root.IsSuccess.Should().BeTrue(root.ErrorMessage);
            }

            if (completedSteps >= 3)
            {
                (await services.OcrPresets.CreatePresetAsync("MinerU", null, OcrEngineIds.MinerU,
                    OcrModelIds.MinerUDefault, null, "{}", true)).IsSuccess.Should().BeTrue();
            }

            if (completedSteps >= 4)
            {
                (await services.Credentials.SaveAsync(ProviderIds.MinerU, "Test token", "test-only-token")).IsSuccess
                    .Should().BeTrue();
            }
        }
        finally
        {
            await services.ShutdownAsync();
        }

        await using MainWindowViewModel viewModel = new(settingsPath: profile.Path, autoStartMcpServer: false);
        await viewModel.RunStartupAsync(false);
        viewModel.IsFirstRunVisible.Should().Be(expectedStep != FirstRunStep.Complete);
        if (expectedStep != FirstRunStep.Complete)
        {
            viewModel.FirstRun.CurrentStep.Should().Be(expectedStep);
            if (expectedStep == FirstRunStep.Library)
            {
                await viewModel.FirstRun.CreateLibraryCommand.ExecuteAsync();
                viewModel.FirstRun.CurrentStep.Should().Be(FirstRunStep.Scan);
            }
        }
    }

    [Fact]
    public async Task New_profile_starts_at_the_database_page()
    {
        using TemporaryAppSettingsFile profile = new();
        await using MainWindowViewModel viewModel = new(settingsPath: profile.Path, autoStartMcpServer: false);
        await viewModel.RunStartupAsync(false);
        viewModel.IsFirstRunVisible.Should().BeTrue();
        viewModel.FirstRun.CurrentStep.Should().Be(FirstRunStep.Database);
    }

    [Fact]
    public async Task Initial_startup_without_a_database_shows_first_run_without_composing_a_library()
    {
        using TemporaryAppSettingsFile profile = new();
        string defaultDatabase = PatchouliAppSettings.Load(profile.Path).Runtime.RuntimeDatabasePath;
        await using MainWindowViewModel viewModel = new(settingsPath: profile.Path, autoStartMcpServer: false);

        await viewModel.RunInitialStartupAsync(false);

        viewModel.IsFirstRunVisible.Should().BeTrue();
        viewModel.IsStartupLoadingVisible.Should().BeFalse();
        viewModel.FirstRun.CurrentStep.Should().Be(FirstRunStep.Database);
        viewModel.HasOpenRuntimeDatabase.Should().BeFalse("no session is opened before the user picks a location");
        File.Exists(defaultDatabase).Should().BeFalse("the fast path must not create a database at the default path");
    }

    [Fact]
    public async Task Deferred_first_run_opens_the_session_at_the_picked_location_and_keeps_its_page()
    {
        using TemporaryAppSettingsFile profile = new();
        string picked = profile.CreateDatabasePath("picked");
        await using MainWindowViewModel viewModel = new(settingsPath: profile.Path, autoStartMcpServer: false);
        await viewModel.RunInitialStartupAsync(false);
        FirstRunViewModel firstRun = viewModel.FirstRun;

        firstRun.DatabasePath = picked;
        await firstRun.OpenDatabaseCommand.ExecuteAsync();

        viewModel.HasOpenRuntimeDatabase.Should().BeTrue();
        viewModel.RuntimeDatabasePath.Should().Be(picked);
        viewModel.IsFirstRunVisible.Should().BeTrue();
        viewModel.FirstRun.Should().BeSameAs(firstRun);
        firstRun.CurrentStep.Should().Be(FirstRunStep.Library);
        await firstRun.CreateLibraryCommand.ExecuteAsync();
        firstRun.CurrentStep.Should().Be(FirstRunStep.Scan);
    }

    [Fact]
    public async Task Lazy_service_request_during_deferred_first_run_keeps_the_page_and_its_input()
    {
        using TemporaryAppSettingsFile profile = new();
        await using MainWindowViewModel viewModel = new(settingsPath: profile.Path, autoStartMcpServer: false);
        await viewModel.RunInitialStartupAsync(false);
        FirstRunViewModel firstRun = viewModel.FirstRun;
        firstRun.LibraryName = "Typed before a lazy open";

        await viewModel.ServicesAsync(false);

        viewModel.IsFirstRunVisible.Should().BeTrue();
        viewModel.FirstRun.Should().BeSameAs(firstRun);
        firstRun.LibraryName.Should().Be("Typed before a lazy open");
    }

    [Fact]
    public async Task Initial_startup_with_an_existing_database_still_resumes_unfinished_setup()
    {
        using TemporaryAppSettingsFile profile = new();
        PatchouliAppSettings settings = PatchouliAppSettings.Load(profile.Path);
        HostServices services =
            await HostServices.CreateAsync(settings.Runtime.RuntimeDatabasePath, settings, profile.Path);
        await services.ShutdownAsync();

        await using MainWindowViewModel viewModel = new(settingsPath: profile.Path, autoStartMcpServer: false);
        await viewModel.RunInitialStartupAsync(false);

        viewModel.HasOpenRuntimeDatabase.Should().BeTrue();
        viewModel.IsFirstRunVisible.Should().BeTrue();
        viewModel.FirstRun.CurrentStep.Should().Be(FirstRunStep.Library);
    }
}
