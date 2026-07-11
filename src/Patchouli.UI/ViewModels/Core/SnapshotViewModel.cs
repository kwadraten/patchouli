using System.Text.Json;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

public sealed class SnapshotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public SnapshotViewModel(MainWindowViewModel main)
    {
        _main = main;
        PublishCommand = new AsyncCommand(PublishAsync);
        ValidateCommand = new AsyncCommand(ValidateAsync);
        ImportCommand = new AsyncCommand(ImportAsync);
    }

    public string SyncRoot { get; set; } = "";
    public string DeviceId { get; set; } = "device-ui";
    public string ManifestPath { get; set; } = "";
    public string StagingRoot { get; set; } = "";
    public string LastSnapshotId { get; set; } = "";
    public string LastManifestPath { get; set; } = "";
    public string Output { get; set; } = "";
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand ValidateCommand { get; }
    public AsyncCommand ImportCommand { get; }

    private async Task PublishAsync()
    {
        var services = await _main.ServicesAsync();
        var result = await _main.ModalOperations.RunAsync(
            new ModalOperationOptions(
                "发布同步快照",
                "正在创建快照分片并校验内容。",
                CanCancel: true),
            context => services.SnapshotPublisher.PublishSnapshotAsync(
                new SnapshotPublishRequest(services.RuntimeDatabasePath, SyncRoot, DeviceId),
                context.CancellationToken));
        Output = Format(result);
        if (result.IsSuccess)
        {
            ManifestPath = result.Value.ManifestPath;
            LastManifestPath = result.Value.ManifestPath;
            LastSnapshotId = result.Value.SnapshotId;
        }
        RaiseAll();
        await _main.LogOperationAsync("publish_snapshot", Output);
    }

    private async Task ValidateAsync()
    {
        var services = await _main.ServicesAsync();
        var result = await _main.ModalOperations.RunAsync(
            new ModalOperationOptions(
                "验证同步快照",
                "正在校验 manifest 与内容分片。",
                CanCancel: true),
            async context =>
            {
                var validation = await services.SnapshotImporter.ValidateSnapshotAsync(ManifestPath, context.CancellationToken);
                return validation.IsFailure || validation.Value.IsValid
                    ? validation
                    : Patchouli.Core.Results.Result<SnapshotValidationResult>.Failure(
                        Patchouli.Core.Results.AppErrorCodes.ValidationFailed,
                        string.Join(" ", validation.Value.Errors));
            });
        Output = Format(result);
        Raise(nameof(Output));
    }

    private async Task ImportAsync()
    {
        var services = await _main.ServicesAsync();
        var result = await _main.ModalOperations.RunAsync(
            new ModalOperationOptions(
                "导入同步快照",
                "正在验证快照并导入 staging 数据库。",
                CanCancel: true),
            async context =>
            {
                var imported = await services.SnapshotImporter.ImportSnapshotToStagingAsync(
                    new SnapshotImportRequest(ManifestPath, StagingRoot),
                    context.CancellationToken);
                return imported.IsFailure || (imported.Value.IsValid && imported.Value.IsLibraryMatch && imported.Value.StagingDatabasePath is not null)
                    ? imported
                    : Patchouli.Core.Results.Result<SnapshotImportResult>.Failure(
                        Patchouli.Core.Results.AppErrorCodes.ValidationFailed,
                        string.Join(" ", imported.Value.Warnings));
            });
        Output = Format(result) + "\nImport does not replace active runtime DB.";
        Raise(nameof(Output));
        await _main.LogOperationAsync("import_snapshot_staging", Output);
    }

    private static string Format<T>(Patchouli.Core.Results.Result<T> result)
        => result.IsSuccess
            ? JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { WriteIndented = true })
            : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";

    private void RaiseAll()
    {
        Raise(nameof(Output));
        Raise(nameof(ManifestPath));
        Raise(nameof(LastManifestPath));
        Raise(nameof(LastSnapshotId));
    }
}
