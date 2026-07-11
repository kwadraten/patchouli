using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Search;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels;

public sealed class FileDocumentViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string FilePath{get;set;}="";public string ItemId{get;set;}="";public string FileAssetId{get;set;}="";public string InstanceType{get;set;}="primary_scan";public string Output{get;set;}="";public ObservableCollection<string> RecentFileAssets{get;}=new();public ObservableCollection<string> RecentDocumentInstances{get;}=new();public AsyncCommand RegisterCommand{get;}public AsyncCommand AttachCommand{get;}public AsyncCommand ResolveCommand{get;}
    public FileDocumentViewModel(MainWindowViewModel main){_main=main;RegisterCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Files.RegisterFileAsync(FilePath);if(r.IsSuccess){FileAssetId=r.Value.FileAssetId.ToString();RecentFileAssets.Add($"{r.Value.FileAssetId} | {r.Value.FileName} ({r.Value.Status})");Raise(nameof(FileAssetId));}Output=r.IsSuccess?$"File asset: {r.Value.FileAssetId}\n{r.Value.Status}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("register_file", Output);});AttachCommand=new(async()=>{var f=string.IsNullOrWhiteSpace(FileAssetId)?(Patchouli.Core.Ids.FileAssetId?)null:Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);var r=await (await _main.ServicesAsync()).Documents.AttachDocumentInstanceAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),f,InstanceType);if(r.IsSuccess)RecentDocumentInstances.Add($"{r.Value.DocumentInstanceId} | {r.Value.InstanceType}");Output=r.IsSuccess?$"Document: {r.Value.DocumentInstanceId}\nPrimary: {r.Value.IsPrimary}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("attach_document_instance", Output);});ResolveCommand=new(ResolveAsync);}

    private async Task ResolveAsync()
    {
        var services = await _main.ServicesAsync();
        var fileAssetId = Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);
        var result = await services.FileResolution.ResolveFileAsync(fileAssetId, ResolveFilePurpose.MaintenanceScan);
        if (result.IsSuccess && result.Value.Conflicts.Count > 0)
        {
            var conflict = result.Value.Conflicts[0];
            var options = result.Value.Candidates.Select(candidate => new ConflictDialogOption(
                candidate.Path,
                candidate.Path,
                $"{candidate.SizeBytes} bytes | {candidate.MtimeUtc:O} | {candidate.Confidence} | {candidate.Reason}"))
                .ToArray();
            var dialog = new ConflictResolutionDialogViewModel(conflict, options);
            var choice = await _main.Dialogs.ShowDialogAsync<ConflictDialogResult>(dialog);
            if (choice?.ActionId is "choose_candidate" or "confirm_changed_file" &&
                choice.OptionId is not null &&
                result.Value.Candidates.Any(candidate => string.Equals(candidate.Path, choice.OptionId, StringComparison.OrdinalIgnoreCase)))
            {
                var confirmed = await services.FileResolution.ConfirmMovedCandidateAsync(fileAssetId, choice.OptionId);
                Output = confirmed.IsSuccess ? "文件位置与指纹已确认。" : $"ERROR {confirmed.ErrorCode}: {confirmed.ErrorMessage}";
                Raise(nameof(Output));
                return;
            }
        }
        Output = result.IsSuccess ? $"{result.Value.Status}\n{result.Value.Confidence}\n{result.Value.RequiredAction}" : $"ERROR {result.ErrorCode}: {result.ErrorMessage}";
        Raise(nameof(Output));
    }
}
