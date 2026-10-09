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
}
