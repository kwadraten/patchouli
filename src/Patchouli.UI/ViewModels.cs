using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.UI;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class AsyncCommand : System.Windows.Input.ICommand
{
    private readonly Func<Task> _run; public AsyncCommand(Func<Task> run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } } public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await _run();
    public Task ExecuteAsync() => _run();
}
public sealed class MainWindowViewModel : ViewModelBase
{
    private AppServices? _services;
    private McpHttpServer? _mcpServer;
    private readonly bool _autoStartMcpServer;
    private readonly int _mcpPort;
    private readonly PatchouliAppSettings _settings;
    private string _runtimeDatabasePath;
    private bool _showInspectorPane = true;
    public string RuntimeDatabasePath { get => _runtimeDatabasePath; set { _runtimeDatabasePath=value; Raise(); } }
    public string Status { get; set; } = "请选择运行数据库路径，然后创建或打开资料库。";
    public string McpEndpoint { get; private set; } = $"http://localhost:{McpServerOptions.DefaultPort}";
    public string McpStatusText { get; private set; } = "MCP: 未启动";
    public string McpStatusDetail { get; private set; } = "等待运行数据库打开。";
    public IBrush McpStatusBrush { get; private set; } = Brushes.Gray;
    public string VersionInfo => $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion} | {RuntimeDatabasePath}";
    public string StatusBarVersion => $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion}";
    public IClipboardService Clipboard { get; }
    public IAppLogger Logger { get; }
    public LibraryShellViewModel Shell { get; }
    public FirstRunViewModel FirstRun { get; private set; }
    public bool IsFirstRunVisible { get; set; }
    public bool IsLibraryVisible => !IsFirstRunVisible;
    public bool IsSearchEnabled => !IsFirstRunVisible;
    public bool ShowInspectorPane { get => _showInspectorPane; set { if (_showInspectorPane == value) return; _showInspectorPane = value; Raise(); Raise(nameof(IsInspectorVisible)); } }
    public bool IsInspectorVisible => IsLibraryVisible && ShowInspectorPane;
    public bool ShowSelectedDocumentTab => IsLibraryVisible && Shell.SelectedItem is not null;
    public LibraryViewModel Library { get; } public BibliographyViewModel Bibliography { get; } public FileDocumentViewModel FileDocument { get; } public PageLayoutViewModel PageLayout { get; } public MockOcrViewModel MockOcr { get; } public OcrQueueViewModel OcrQueue { get; } public PdfRenderViewModel PdfRender { get; } public PdfPreviewViewModel PdfPreview { get; } public SearchEvidenceViewModel SearchEvidence { get; } public SearchProfileViewModel SearchProfiles { get; } public McpPreviewViewModel McpPreview { get; } public SnapshotViewModel Snapshot { get; } public SnapshotBranchViewModel SnapshotBranch { get; }
    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CompleteFirstRunCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public AsyncCommand OpenOcrQueueCommand { get; }
    public AsyncCommand CreateItemMenuCommand { get; }
    public AsyncCommand EditSelectedItemCommand { get; }
    public AsyncCommand RunSelectedItemOcrCommand { get; }
    public AsyncCommand RebuildSearchIndexCommand { get; }
    public AsyncCommand ExportEvidenceMarkdownCommand { get; }
    public AsyncCommand ToggleInspectorPaneCommand { get; }
    public AsyncCommand ShowAboutCommand { get; }
    public AsyncCommand ShowLicenseCommand { get; }
    public MainWindowViewModel(IClipboardService? clipboard = null, IAppLogger? logger = null, bool autoStartMcpServer = false, int mcpPort = McpServerOptions.DefaultPort)
    {
        _settings=PatchouliAppSettings.Load();
        _runtimeDatabasePath=_settings.Runtime.RuntimeDatabasePath;
        _autoStartMcpServer=autoStartMcpServer;
        _mcpPort=mcpPort;
        McpEndpoint=$"http://localhost:{mcpPort}";
        Clipboard=clipboard??new AvaloniaClipboardService();
        Logger=logger??new SimpleFileLogger(_settings.Runtime.LogDirectory);
        PdfPreview=new(this); Shell=new(this); Library=new(this); Bibliography=new(this); FileDocument=new(this); PageLayout=new(this); MockOcr=new(this); OcrQueue=new(this); PdfRender=new(this); SearchEvidence=new(this); SearchProfiles=new(this); McpPreview=new(this); Snapshot=new(this); SnapshotBranch=new(this);
        Shell.MinerUToken=_settings.MinerU.Token;
        OpenDatabaseCommand=new(async()=>{await StopMcpServerAsync("正在切换运行数据库。");_services=await AppServices.CreateAsync(RuntimeDatabasePath,_settings); await LoadPersistedMinerUTokenAsync(); Status=$"数据库已就绪：{RuntimeDatabasePath}";Raise(nameof(Status));Raise(nameof(VersionInfo));Raise(nameof(StatusBarVersion));if(_autoStartMcpServer)await StartMcpServerAsync(_services);});
        FirstRun=CreateFirstRunViewModel();
        CompleteFirstRunCommand=new(CompleteFirstRunAsync);
        OpenSettingsCommand = new(() => ShowPlaceholderAsync("设置页面将在后续任务中接入。"));
        OpenOcrQueueCommand = new(() => ShowPlaceholderAsync("OCR 队列页面将在后续任务中接入。"));
        CreateItemMenuCommand = new(async () => { await Shell.SwitchToLibraryListAsync(); await ShowPlaceholderAsync("新建题录标签页将在后续任务中接入。"); });
        EditSelectedItemCommand = new(EditSelectedItemAsync);
        RunSelectedItemOcrCommand = new(RunSelectedItemOcrAsync);
        RebuildSearchIndexCommand = new(() => ShowPlaceholderAsync("重建 FTS 索引入口将在后续任务中接入。"));
        ExportEvidenceMarkdownCommand = new(() => ShowPlaceholderAsync("证据 Markdown 导出入口将在后续任务中接入。"));
        ToggleInspectorPaneCommand = new(() => { ShowInspectorPane = !ShowInspectorPane; return Task.CompletedTask; });
        ShowAboutCommand = new(() => ShowPlaceholderAsync(StatusBarVersion));
        ShowLicenseCommand = new(() => ShowPlaceholderAsync("许可证页面将在后续任务中接入。"));
    }
    public async Task<AppServices> ServicesAsync()
    {
        if (_services is not null) return _services;

        _services = await AppServices.CreateAsync(RuntimeDatabasePath, _settings);
        await LoadPersistedMinerUTokenAsync();
        if (_autoStartMcpServer)
        {
            await StartMcpServerAsync(_services);
        }

        return _services;
    }
    public async Task StartMcpServerAsync()
    {
        var services = await ServicesAsync();
        await StartMcpServerAsync(services);
    }
    public async Task StopMcpServerAsync(string detail = "MCP HTTP 服务已停止。")
    {
        if (_mcpServer is not null)
        {
            await _mcpServer.DisposeAsync();
            _mcpServer = null;
        }

        SetMcpStatus("MCP: 未启动", detail, Brushes.Gray);
    }
    private async Task StartMcpServerAsync(AppServices services)
    {
        if (_mcpServer?.IsRunning == true)
        {
            SetMcpStatus("MCP: 运行中", $"服务地址：{McpEndpoint}", Brushes.LimeGreen);
            return;
        }

        await StopMcpServerAsync("MCP HTTP 服务正在启动。");
        SetMcpStatus("MCP: 启动中", $"正在监听 {McpEndpoint}", Brushes.Goldenrod);
        var server = new McpHttpServer(new McpProtocolHandler(services.Mcp, services.ConnectionFactory), _mcpPort);
        try
        {
            await server.StartAsync();
            _mcpServer = server;
            McpEndpoint = server.Endpoint;
            Raise(nameof(McpEndpoint));
            SetMcpStatus("MCP: 运行中", $"服务地址：{server.Endpoint}", Brushes.LimeGreen);
            await LogOperationAsync("mcp_http_start", $"MCP HTTP server listening on {server.Endpoint}");
        }
        catch (Exception ex)
        {
            await server.DisposeAsync();
            var message = McpOutputSanitizer.Sanitize(ex.Message);
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            await LogOperationAsync("mcp_http_start_failed", message);
        }
    }
    private void SetMcpStatus(string text, string detail, IBrush brush)
    {
        McpStatusText = text;
        McpStatusDetail = detail;
        McpStatusBrush = brush;
        Raise(nameof(McpStatusText));
        Raise(nameof(McpStatusDetail));
        Raise(nameof(McpStatusBrush));
    }
    public void Report(string message) { Status=message; Raise(nameof(Status)); }
    public Task ShowInlineFirstRunAsync()
    {
        FirstRun=CreateFirstRunViewModel();
        FirstRun.DatabasePath=RuntimeDatabasePath;
        IsFirstRunVisible=true;
        Raise(nameof(FirstRun));
        Raise(nameof(IsFirstRunVisible));
        Raise(nameof(IsLibraryVisible));
        Raise(nameof(IsSearchEnabled));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(ShowSelectedDocumentTab));
        return Task.CompletedTask;
    }
    public async Task HideInlineFirstRunAsync()
    {
        IsFirstRunVisible=false;
        Raise(nameof(IsFirstRunVisible));
        Raise(nameof(IsLibraryVisible));
        Raise(nameof(IsSearchEnabled));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(ShowSelectedDocumentTab));
        await Shell.RefreshItemsAsync();
    }
    private FirstRunViewModel CreateFirstRunViewModel() => new(OpenFirstRunDatabaseAsync) { DatabasePath = RuntimeDatabasePath, MinerUToken = Shell.MinerUToken };
    private async Task<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)> OpenFirstRunDatabaseAsync(string path)
    {
        RuntimeDatabasePath=path;
        await OpenDatabaseCommand.ExecuteAsync();
        var services=await ServicesAsync();
        return (services.FirstRunWorkflow, services.PdfDiscovery);
    }
    private async Task CompleteFirstRunAsync()
    {
        await FirstRun.FinishSetupCommand.ExecuteAsync();
        if (!FirstRun.IsComplete) return;
        var persisted = await SaveMinerUTokenAsync(FirstRun.MinerUToken);
        if (!persisted) return;
        Shell.MinerUToken=FirstRun.MinerUToken.Trim();
        Report("初始化完成。请选择题录，并通过右键菜单运行 MinerU OCR。");
        await HideInlineFirstRunAsync();
    }
    public MinerUConfiguration CreateMinerUConfiguration(string token) => _settings.MinerU.ToConfiguration(token);
    public async Task<string> GetPersistedMinerUTokenAsync()
    {
        var services = await ServicesAsync();
        var secret = await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU);
        return secret.IsSuccess ? secret.Value : "";
    }
    public async Task<bool> SaveMinerUTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var services = await ServicesAsync();
        var saved = await services.Credentials.SaveOrUpdateProviderCredentialAsync(ProviderIds.MinerU, "MinerU API token", token.Trim());
        if (saved.IsFailure) { Report(saved.ErrorMessage ?? "无法保存 MinerU API token。"); return false; }
        return true;
    }
    private async Task LoadPersistedMinerUTokenAsync()
    {
        var token = await GetPersistedMinerUTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) token = _settings.MinerU.Token;
        Shell.MinerUToken=token;
        FirstRun.MinerUToken=token;
        Shell.NotifyMinerUTokenChanged();
    }
    public void RaiseShellSelectionChanged() => Raise(nameof(ShowSelectedDocumentTab));
    public async Task LogOperationAsync(string operation, string message)
    {
        try { await Logger.LogAsync(operation, message); }
        catch { /* Logging is diagnostic only; a file-system failure must not block the UI. */ }
    }
    private Task ShowPlaceholderAsync(string message)
    {
        Report(message);
        return Task.CompletedTask;
    }

    private async Task EditSelectedItemAsync()
    {
        if (Shell.SelectedItem is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        await Shell.SelectedItem.EditMetadataCommand.ExecuteAsync();
    }

    private async Task RunSelectedItemOcrAsync()
    {
        if (Shell.SelectedItem is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        await Shell.SelectedItem.RunOcrCommand.ExecuteAsync();
    }
}
public sealed class LibraryViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string DisplayName {get;set;}="My Library"; public string RenameTo {get;set;}=""; public string Details {get;set;}="";
    public AsyncCommand CreateCommand {get;} public AsyncCommand RefreshCommand{get;} public AsyncCommand RenameCommand{get;}
    public LibraryViewModel(MainWindowViewModel main){_main=main;CreateCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Library.CreateLibraryAsync(DisplayName);Details=r.IsSuccess?$"{r.Value.DisplayName}\n{r.Value.LibraryId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Details));_main.Report(Details);await _main.LogOperationAsync("create_library", Details);});RefreshCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Library.GetCurrentLibraryAsync();Details=r.IsSuccess?$"{r.Value.DisplayName}\n{r.Value.LibraryId}\nSchema {r.Value.SchemaVersion}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Details));});RenameCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Library.RenameLibraryAsync(RenameTo);Details=r.IsSuccess?$"Renamed: {r.Value.DisplayName}\n{r.Value.LibraryId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Details));await _main.LogOperationAsync("rename_library", Details);});}
}
public sealed class BibliographyViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string ItemType{get;set;}="book";public string Title{get;set;}="";public string Subtitle{get;set;}="";public string ItemId{get;set;}="";public string Scheme{get;set;}="DOI";public string IdentifierValue{get;set;}="";public string Output{get;set;}="";public ObservableCollection<string> RecentItems{get;}=new();public AsyncCommand CreateItemCommand{get;}public AsyncCommand AddIdentifierCommand{get;}
    public BibliographyViewModel(MainWindowViewModel main){_main=main;CreateItemCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Items.CreateItemAsync(ItemType,Title,Subtitle);if(r.IsSuccess){ItemId=r.Value.ItemId.ToString();RecentItems.Add($"{r.Value.ItemId} | {r.Value.Title}");Raise(nameof(ItemId));}Output=r.IsSuccess?$"Item: {r.Value.ItemId}\n{r.Value.Title}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_item", Output);});AddIdentifierCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Items.AddIdentifierAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),Scheme,IdentifierValue,null);Output=r.IsSuccess?$"Identifier: {r.Value.Scheme} {r.Value.Value}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class FileDocumentViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string FilePath{get;set;}="";public string ItemId{get;set;}="";public string FileAssetId{get;set;}="";public string InstanceType{get;set;}="primary_scan";public string Output{get;set;}="";public ObservableCollection<string> RecentFileAssets{get;}=new();public ObservableCollection<string> RecentDocumentInstances{get;}=new();public AsyncCommand RegisterCommand{get;}public AsyncCommand AttachCommand{get;}public AsyncCommand ResolveCommand{get;}
    public FileDocumentViewModel(MainWindowViewModel main){_main=main;RegisterCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Files.RegisterFileAsync(FilePath);if(r.IsSuccess){FileAssetId=r.Value.FileAssetId.ToString();RecentFileAssets.Add($"{r.Value.FileAssetId} | {r.Value.FileName} ({r.Value.Status})");Raise(nameof(FileAssetId));}Output=r.IsSuccess?$"File asset: {r.Value.FileAssetId}\n{r.Value.Status}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("register_file", Output);});AttachCommand=new(async()=>{var f=string.IsNullOrWhiteSpace(FileAssetId)?(Patchouli.Core.Ids.FileAssetId?)null:Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);var r=await (await _main.ServicesAsync()).Documents.AttachDocumentInstanceAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),f,InstanceType);if(r.IsSuccess)RecentDocumentInstances.Add($"{r.Value.DocumentInstanceId} | {r.Value.InstanceType}");Output=r.IsSuccess?$"Document: {r.Value.DocumentInstanceId}\nPrimary: {r.Value.IsPrimary}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("attach_document_instance", Output);});ResolveCommand=new(async()=>{var r=await (await _main.ServicesAsync()).FileResolution.ResolveFileAsync(Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId),ResolveFilePurpose.MaintenanceScan);Output=r.IsSuccess?$"{r.Value.Status}\n{r.Value.Confidence}\n{r.Value.RequiredAction}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class PageLayoutViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string DocumentInstanceId{get;set;}="";public string PageId{get;set;}="";public string RevisionId{get;set;}="";public string PageIndex{get;set;}="0";public string Text{get;set;}="";public string Output{get;set;}="";public ObservableCollection<string> RecentPages{get;}=new();public ObservableCollection<string> RecentLayoutRevisions{get;}=new();public AsyncCommand CreatePageCommand{get;}public AsyncCommand CreateRevisionCommand{get;}public AsyncCommand AddNodeCommand{get;}public AsyncCommand BuildTextCommand{get;}
    public PageLayoutViewModel(MainWindowViewModel m){_main=m;CreatePageCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Pages.CreatePageAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),int.Parse(PageIndex),null,null,null,0,CoordinateBasis.NormalizedPage,null,null,"ui-mvp-1",null);if(r.IsSuccess){PageId=r.Value.PageId.ToString();RecentPages.Add($"{r.Value.PageId} | {r.Value.PageIndex}");Raise(nameof(PageId));}Output=r.IsSuccess?$"Page: {r.Value.PageId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_page", Output);});CreateRevisionCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.CreateLayoutRevisionAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),LayoutRevisionSource.Manual,true);if(r.IsSuccess){RevisionId=r.Value.LayoutRevisionId.ToString();RecentLayoutRevisions.Add($"{r.Value.LayoutRevisionId} | current");Raise(nameof(RevisionId));}Output=r.IsSuccess?$"Revision: {r.Value.LayoutRevisionId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_layout_revision", Output);});AddNodeCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.AddNodeAsync(Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId),Patchouli.Core.Ids.PageId.Parse(PageId),null,LayoutNodeType.Paragraph,new NormalizedBBox(.1,.1,.8,.2),Text,TextPolicy.Own,1,LayoutNodeSource.Manual);Output=r.IsSuccess?$"Node: {r.Value.NodeId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});BuildTextCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.BuildPagePlainTextAsync(Patchouli.Core.Ids.PageId.Parse(PageId),Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId));Output=r.IsSuccess?r.Value.Text:$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class MockOcrViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string Name{get;set;}="Mock preset";public string PresetId{get;set;}="";public string DocumentInstanceId{get;set;}="";public string PageIds{get;set;}="";public string ImagePageId{get;set;}="";public string ImagePath{get;set;}="";public string RunId{get;set;}="";public string NewModelPath{get;set;}="";public bool ApplyOnSuccess{get;set;}=true;public string ParametersJson{get;set;}="{}";public string Output{get;set;}="";public string Capabilities{get;set;}="";public ObservableCollection<string> RecentRuns{get;}=new();public AsyncCommand CreatePresetCommand{get;}public AsyncCommand RunCommand{get;}public AsyncCommand RunImageCommand{get;}public AsyncCommand ShowRunCommand{get;}public AsyncCommand AdoptCommand{get;}public AsyncCommand CancelCommand{get;}public AsyncCommand ShowCapabilitiesCommand{get;}public AsyncCommand CheckEnvironmentCommand{get;}public AsyncCommand RebindModelPathCommand{get;}
    public MockOcrViewModel(MainWindowViewModel m){_main=m;CreatePresetCommand=new(async()=>{var r=await (await _main.ServicesAsync()).OcrPresets.CreatePresetAsync(Name,null,OcrEngineIds.Mock,OcrModelIds.MockBasic,null,ParametersJson,ApplyOnSuccess);if(r.IsSuccess){PresetId=r.Value.PresetId.ToString();Raise(nameof(PresetId));}Output=r.IsSuccess?$"Preset: {r.Value.PresetId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});RunCommand=new(async()=>{var pages=PageIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(Patchouli.Core.Ids.PageId.Parse).ToArray();var r=await (await _main.ServicesAsync()).Ocr.RunPresetOnPagesAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),OcrPresetId.Parse(PresetId),pages);if(r.IsSuccess){RunId=r.Value.OcrRunId.ToString();RecentRuns.Add($"{r.Value.OcrRunId} | {r.Value.State}");Raise(nameof(RunId));}Output=r.IsSuccess?$"Run: {r.Value.OcrRunId}\n{r.Value.State}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("run_mock_ocr", Output);});RunImageCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Ocr.RunPresetOnImagePageAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),OcrPresetId.Parse(PresetId),Patchouli.Core.Ids.PageId.Parse(ImagePageId),ImagePath);if(r.IsSuccess){RunId=r.Value.OcrRunId.ToString();RecentRuns.Add($"{r.Value.OcrRunId} | {r.Value.State}");Raise(nameof(RunId));}Output=r.IsSuccess?$"Image OCR run: {r.Value.OcrRunId}\n{r.Value.State}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("run_local_image_ocr", Output);});ShowRunCommand=new(async()=>{var s=await _main.ServicesAsync();var run=await s.Ocr.GetRunAsync(OcrRunId.Parse(RunId));var pages=await s.Ocr.ListPageResultsAsync(OcrRunId.Parse(RunId));Output=run.IsSuccess?$"{run.Value.State}\n"+string.Join("\n",pages.Value.Select(p=>$"{p.PageId}: {p.State} {p.ErrorCode} {p.ErrorMessage}")):$"ERROR {run.ErrorCode}: {run.ErrorMessage}";Raise(nameof(Output));});AdoptCommand=new(async()=>{var selected=string.IsNullOrWhiteSpace(PageIds)?null:PageIds.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(Patchouli.Core.Ids.PageId.Parse).ToArray();var r=await (await _main.ServicesAsync()).Ocr.AdoptCandidateRunAsync(OcrRunId.Parse(RunId),selected);Output=r.IsSuccess?$"Adopted: {r.Value.AdoptedRevisionId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});CancelCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Ocr.CancelRunAsync(OcrRunId.Parse(RunId));Output=r.IsSuccess?"Run cancelled.":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});ShowCapabilitiesCommand=new(async()=>{Capabilities=string.Join("\n",(await _main.ServicesAsync()).OcrAdapters.ListCapabilities().Select(c=>$"{c.EngineId}: {c.DisplayName}; requires model path={c.RequiresModelPath}; {c.Notes}"));Raise(nameof(Capabilities));});CheckEnvironmentCommand=new(async()=>{var s=await _main.ServicesAsync();var version=await s.OcrPresets.GetCurrentVersionAsync(OcrPresetId.Parse(PresetId));if(version.IsFailure){Output=$"ERROR {version.ErrorCode}: {version.ErrorMessage}";}else{var check=await s.OcrAdapters.CheckEngineAsync(version.Value.EngineId,version.Value);Output=check.IsSuccess?$"{check.Value.Status}\n{check.Value.Message}\nAction: {check.Value.RequiredAction}":$"ERROR {check.ErrorCode}: {check.ErrorMessage}";}Raise(nameof(Output));});RebindModelPathCommand=new(async()=>{var r=await (await _main.ServicesAsync()).OcrPresets.RebindModelPathAsync(OcrPresetId.Parse(PresetId),NewModelPath);Output=r.IsSuccess?$"Rebound model path. New preset version: {r.Value.PresetVersionId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class SearchEvidenceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string DocumentInstanceId{get;set;}="";public string Query{get;set;}="";public string UnitId{get;set;}="";public string EvidenceRef{get;set;}="";public string Markdown{get;set;}="";public string Output{get;set;}="";public ObservableCollection<string> SearchUnits{get;}=new();public AsyncCommand RebuildCommand{get;}public AsyncCommand SearchCommand{get;}public AsyncCommand CreateEvidenceCommand{get;}public AsyncCommand MarkdownCommand{get;}public AsyncCommand CopyMarkdownCommand{get;}
    public SearchEvidenceViewModel(MainWindowViewModel m){_main=m;RebuildCommand=new(async()=>{var s=await _main.ServicesAsync();var a=await s.SearchUnits.RebuildForDocumentInstanceAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));var b=await s.SearchIndex.RebuildFtsForDocumentInstanceAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));Output=a.IsSuccess&&b.IsSuccess?"Search units and FTS rebuilt.":$"ERROR {a.ErrorCode??b.ErrorCode}";Raise(nameof(Output));await _main.LogOperationAsync("rebuild_search_fts", Output);});SearchCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Search.SearchLibraryAsync(new SearchRequest(Query));if(r.IsSuccess){SearchUnits.Clear();foreach(var u in r.Value.Results.SelectMany(x=>x.MatchedUnits)){SearchUnits.Add($"{u.UnitId} | {u.Text}");}if(r.Value.Results.SelectMany(x=>x.MatchedUnits).FirstOrDefault() is { } first){UnitId=first.UnitId.ToString();Raise(nameof(UnitId));}}Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});CreateEvidenceCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Evidence.CreateFromSearchUnitAsync(Patchouli.Core.Ids.SearchUnitId.Parse(UnitId));Output=r.IsSuccess?r.Value.EvidenceRefId:$"ERROR {r.ErrorCode}: {r.ErrorMessage}";if(r.IsSuccess){EvidenceRef=r.Value.EvidenceRefId;var markdown=await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(EvidenceRef);if(markdown.IsSuccess)Markdown=markdown.Value.Markdown;}Raise(nameof(Output));Raise(nameof(EvidenceRef));Raise(nameof(Markdown));await _main.LogOperationAsync("create_evidence_ref", Output);});MarkdownCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(EvidenceRef);Markdown=r.IsSuccess?r.Value.Markdown:"";Output=r.IsSuccess?Markdown:$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Markdown));Raise(nameof(Output));});CopyMarkdownCommand=new(async()=>{if(string.IsNullOrWhiteSpace(Markdown)){Output="ERROR validation_failed: Generate Evidence Markdown first.";}else{try{await _main.Clipboard.SetTextAsync(Markdown);Output="Copied Evidence Markdown";}catch(Exception ex){Output=$"ERROR clipboard_unavailable: {ex.Message}";}}Raise(nameof(Output));await _main.LogOperationAsync("copy_evidence_markdown", Output);});}
}
public sealed class McpPreviewViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string Query{get;set;}="";public string Output{get;set;}="";public string Safety{get;set;}="";public string SpecificPath{get;set;}="";public string SpecificSecret{get;set;}="";public AsyncCommand SearchCommand{get;}public AsyncCommand SafetyCommand{get;}
    public McpPreviewViewModel(MainWindowViewModel m){_main=m;SearchCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Mcp.SearchLibraryAsync(new McpSearchLibraryRequest(Query));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});SafetyCommand=new(()=>{var tokens=new[]{"original_path","resolved_path","file://","/Users/","model_path","cache"}.ToList();if(!string.IsNullOrWhiteSpace(SpecificPath))tokens.Add(SpecificPath);if(!string.IsNullOrWhiteSpace(SpecificSecret))tokens.Add(SpecificSecret);var hit=tokens.FirstOrDefault(x=>Output.Contains(x,StringComparison.Ordinal));Safety=hit is null?"No obvious local path or secret exposure detected.":$"Warning: output contains sensitive token: {hit}";Raise(nameof(Safety));return Task.CompletedTask;});}
}
public sealed class SnapshotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string SyncRoot{get;set;}="";public string DeviceId{get;set;}="device-ui";public string ManifestPath{get;set;}="";public string StagingRoot{get;set;}="";public string LastSnapshotId{get;set;}="";public string LastManifestPath{get;set;}="";public string Output{get;set;}="";public AsyncCommand PublishCommand{get;}public AsyncCommand ValidateCommand{get;}public AsyncCommand ImportCommand{get;}
    public SnapshotViewModel(MainWindowViewModel m){_main=m;PublishCommand=new(async()=>{var s=await _main.ServicesAsync();var r=await s.SnapshotPublisher.PublishSnapshotAsync(new SnapshotPublishRequest(s.RuntimeDatabasePath,SyncRoot,DeviceId));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";if(r.IsSuccess){ManifestPath=r.Value.ManifestPath;LastManifestPath=r.Value.ManifestPath;LastSnapshotId=r.Value.SnapshotId;}Raise(nameof(Output));Raise(nameof(ManifestPath));Raise(nameof(LastManifestPath));Raise(nameof(LastSnapshotId));await _main.LogOperationAsync("publish_snapshot", Output);});ValidateCommand=new(async()=>{var r=await (await _main.ServicesAsync()).SnapshotImporter.ValidateSnapshotAsync(ManifestPath);Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});ImportCommand=new(async()=>{var s=await _main.ServicesAsync();var r=await s.SnapshotImporter.ImportSnapshotToStagingAsync(new SnapshotImportRequest(ManifestPath,StagingRoot));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true})+"\nImport does not replace active runtime DB.":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("import_snapshot_staging", Output);});}
}
