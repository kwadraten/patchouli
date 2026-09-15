using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dapper;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

public sealed partial class FileDocumentViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    [ObservableProperty] public partial string FilePath { get; set; } = "";
    [ObservableProperty] public partial string ItemId { get; set; } = "";
    [ObservableProperty] public partial string FileAssetId { get; set; } = "";
    [ObservableProperty] public partial string InstanceType { get; set; } = "primary_scan";
    [ObservableProperty] public partial string Output { get; set; } = "";

    public ObservableCollection<string> RecentFileAssets { get; } = new();
    public ObservableCollection<string> RecentDocumentInstances { get; } = new();
    public AsyncCommand RegisterCommand { get; }
    public AsyncCommand AttachCommand { get; }
    public AsyncCommand ResolveCommand { get; }

    partial void OnFilePathChanged(string value)
    {
        RegisterCommand.NotifyCanExecuteChanged();
    }

    partial void OnItemIdChanged(string value)
    {
        AttachCommand.NotifyCanExecuteChanged();
    }

    partial void OnFileAssetIdChanged(string value)
    {
        ResolveCommand.NotifyCanExecuteChanged();
    }

    public FileDocumentViewModel(MainWindowViewModel main)
    {
        _main = main;
        RegisterCommand = new AsyncCommand(async () =>
        {
            Result<FileAsset> r = await (await _main.ServicesAsync()).Files.RegisterFileAsync(FilePath);
            if (r.IsSuccess)
            {
                FileAssetId = r.Value.FileAssetId.ToString();
                RecentFileAssets.Add($"{r.Value.FileAssetId} | {r.Value.FileName} ({r.Value.Status})");
            }

            Output = r.IsSuccess
                ? $"File asset: {r.Value.FileAssetId}\n{r.Value.Status}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            await _main.LogOperationAsync("register_file", Output);
        }, () => !string.IsNullOrWhiteSpace(FilePath));

        AttachCommand = new AsyncCommand(async () =>
        {
            FileAssetId? f = string.IsNullOrWhiteSpace(FileAssetId)
                ? (FileAssetId?)null
                : Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);
            Result<DocumentInstance> r =
                await (await _main.ServicesAsync()).Documents.AttachDocumentInstanceAsync(
                    Patchouli.Core.Ids.ItemId.Parse(ItemId), f, InstanceType);
            if (r.IsSuccess)
            {
                RecentDocumentInstances.Add($"{r.Value.DocumentInstanceId} | {r.Value.InstanceType}");
            }

            Output = r.IsSuccess
                ? $"Document: {r.Value.DocumentInstanceId}\nPrimary: {r.Value.IsPrimary}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            await _main.LogOperationAsync("attach_document_instance", Output);
        }, () => !string.IsNullOrWhiteSpace(ItemId));

        ResolveCommand = new AsyncCommand(ResolveAsync, () => !string.IsNullOrWhiteSpace(FileAssetId));
    }

    private async Task ResolveAsync()
    {
        HostServices services = await _main.ServicesAsync();
        FileAssetId fileAssetId = Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);
        Result<FileResolutionResult> result =
            await services.FileResolution.ResolveFileAsync(fileAssetId, ResolveFilePurpose.MaintenanceScan);
        if (result.IsSuccess && result.Value.Conflicts.Count > 0)
        {
            ConflictDescriptor conflict = result.Value.Conflicts[0];
            Result<ConflictResolutionResult> resolved = await _main.ResolveConflictAsync(conflict);
            if (resolved.IsFailure)
            {
                Output = $"ERROR {resolved.ErrorCode}: {resolved.ErrorMessage}";
                return;
            }

            Output = resolved.Value.WasExecuted
                ? $"冲突已按 {resolved.Value.Descriptor.SelectedAction} 处理。"
                : "冲突保持未解决。";
            return;
        }

        Output = result.IsSuccess
            ? $"{result.Value.Status}\n{result.Value.Confidence}\n{result.Value.RequiredAction}"
            : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
    }
}
