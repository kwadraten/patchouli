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

public sealed class PageLayoutViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string DocumentInstanceId{get;set;}="";public string PageId{get;set;}="";public string RevisionId{get;set;}="";public string PageIndex{get;set;}="0";public string Text{get;set;}="";public string Output{get;set;}="";public ObservableCollection<string> RecentPages{get;}=new();public ObservableCollection<string> RecentLayoutRevisions{get;}=new();public AsyncCommand CreatePageCommand{get;}public AsyncCommand CreateRevisionCommand{get;}public AsyncCommand AddNodeCommand{get;}public AsyncCommand BuildTextCommand{get;}
    public PageLayoutViewModel(MainWindowViewModel m){_main=m;CreatePageCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Pages.CreatePageAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),int.Parse(PageIndex),null,null,null,0,CoordinateBasis.NormalizedPage,null,null,"ui-mvp-1",null);if(r.IsSuccess){PageId=r.Value.PageId.ToString();RecentPages.Add($"{r.Value.PageId} | {r.Value.PageIndex}");Raise(nameof(PageId));}Output=r.IsSuccess?$"Page: {r.Value.PageId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_page", Output);});CreateRevisionCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.CreateLayoutRevisionAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),LayoutRevisionSource.Manual,true);if(r.IsSuccess){RevisionId=r.Value.LayoutRevisionId.ToString();RecentLayoutRevisions.Add($"{r.Value.LayoutRevisionId} | current");Raise(nameof(RevisionId));}Output=r.IsSuccess?$"Revision: {r.Value.LayoutRevisionId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_layout_revision", Output);});AddNodeCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.AddNodeAsync(Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId),Patchouli.Core.Ids.PageId.Parse(PageId),null,LayoutNodeType.Paragraph,new NormalizedBBox(.1,.1,.8,.2),Text,TextPolicy.Own,1,LayoutNodeSource.Manual);Output=r.IsSuccess?$"Node: {r.Value.NodeId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});BuildTextCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.BuildPagePlainTextAsync(Patchouli.Core.Ids.PageId.Parse(PageId),Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId));Output=r.IsSuccess?r.Value.Text:$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
