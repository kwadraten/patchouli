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

public sealed class SnapshotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string SyncRoot{get;set;}="";public string DeviceId{get;set;}="device-ui";public string ManifestPath{get;set;}="";public string StagingRoot{get;set;}="";public string LastSnapshotId{get;set;}="";public string LastManifestPath{get;set;}="";public string Output{get;set;}="";public AsyncCommand PublishCommand{get;}public AsyncCommand ValidateCommand{get;}public AsyncCommand ImportCommand{get;}
    public SnapshotViewModel(MainWindowViewModel m){_main=m;PublishCommand=new(async()=>{var s=await _main.ServicesAsync();var r=await s.SnapshotPublisher.PublishSnapshotAsync(new SnapshotPublishRequest(s.RuntimeDatabasePath,SyncRoot,DeviceId));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";if(r.IsSuccess){ManifestPath=r.Value.ManifestPath;LastManifestPath=r.Value.ManifestPath;LastSnapshotId=r.Value.SnapshotId;}Raise(nameof(Output));Raise(nameof(ManifestPath));Raise(nameof(LastManifestPath));Raise(nameof(LastSnapshotId));await _main.LogOperationAsync("publish_snapshot", Output);});ValidateCommand=new(async()=>{var r=await (await _main.ServicesAsync()).SnapshotImporter.ValidateSnapshotAsync(ManifestPath);Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});ImportCommand=new(async()=>{var s=await _main.ServicesAsync();var r=await s.SnapshotImporter.ImportSnapshotToStagingAsync(new SnapshotImportRequest(ManifestPath,StagingRoot));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true})+"\nImport does not replace active runtime DB.":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("import_snapshot_staging", Output);});}
}
