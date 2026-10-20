using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Host.Composition;
using Patchouli.Llm;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class LocalFileManagementSettingsTests
{
    [Fact]
    public async Task Layout_models_use_the_same_download_controls_and_managed_directories_as_other_local_models()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel[] models = management.Locations.Where(row =>
                row.DownloadKind is ModelDownloadKind.DocLayoutYolo or ModelDownloadKind.PpDocLayoutV3).ToArray();

            models.Should().HaveCount(2);
            foreach (ManagedLocationViewModel model in models)
            {
                model.Path.Should().StartWith(services.OcrStorage.ModelsRoot + Path.DirectorySeparatorChar);
                model.CanDownload.Should().BeTrue();
                model.CanClear.Should().BeTrue();
            }

            string yoloPath = services.OcrStorage.GetLayoutModelPath(OcrLayoutModelIds.DocLayoutYolo);
            models.Single(row => row.DownloadKind == ModelDownloadKind.DocLayoutYolo).Path.Should()
                .Be(Path.GetDirectoryName(yoloPath));
            string paddlePath = services.OcrStorage.GetLayoutModelPath(OcrLayoutModelIds.PpDocLayoutV3);
            models.Single(row => row.DownloadKind == ModelDownloadKind.PpDocLayoutV3).Path.Should()
                .Be(Path.GetDirectoryName(paddlePath));
            return true;
        }, CancellationToken.None);
    }
}
