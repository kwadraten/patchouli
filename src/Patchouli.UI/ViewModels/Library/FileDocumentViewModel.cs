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

namespace Patchouli.UI.ViewModels;

public sealed class FileDocumentViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string FilePath{get;set;}="";public string ItemId{get;set;}="";public string FileAssetId{get;set;}="";public string InstanceType{get;set;}="primary_scan";public string Output{get;set;}="";public ObservableCollection<string> RecentFileAssets{get;}=new();public ObservableCollection<string> RecentDocumentInstances{get;}=new();public AsyncCommand RegisterCommand{get;}public AsyncCommand AttachCommand{get;}public AsyncCommand ResolveCommand{get;}
    public FileDocumentViewModel(MainWindowViewModel main){_main=main;RegisterCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Files.RegisterFileAsync(FilePath);if(r.IsSuccess){FileAssetId=r.Value.FileAssetId.ToString();RecentFileAssets.Add($"{r.Value.FileAssetId} | {r.Value.FileName} ({r.Value.Status})");Raise(nameof(FileAssetId));}Output=r.IsSuccess?$"File asset: {r.Value.FileAssetId}\n{r.Value.Status}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("register_file", Output);});AttachCommand=new(async()=>{var f=string.IsNullOrWhiteSpace(FileAssetId)?(Patchouli.Core.Ids.FileAssetId?)null:Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);var r=await (await _main.ServicesAsync()).Documents.AttachDocumentInstanceAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),f,InstanceType);if(r.IsSuccess)RecentDocumentInstances.Add($"{r.Value.DocumentInstanceId} | {r.Value.InstanceType}");Output=r.IsSuccess?$"Document: {r.Value.DocumentInstanceId}\nPrimary: {r.Value.IsPrimary}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("attach_document_instance", Output);});ResolveCommand=new(async()=>{var r=await (await _main.ServicesAsync()).FileResolution.ResolveFileAsync(Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId),ResolveFilePurpose.MaintenanceScan);Output=r.IsSuccess?$"{r.Value.Status}\n{r.Value.Confidence}\n{r.Value.RequiredAction}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
