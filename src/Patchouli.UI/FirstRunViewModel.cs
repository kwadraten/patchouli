using System.Collections.ObjectModel;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Workflows;

namespace Patchouli.UI;

public sealed class FirstRunViewModel : ViewModelBase
{
    private FirstRunWorkflow? _workflow;
    private PdfDiscoveryService? _discovery;
    private readonly Func<string, Task<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>>? _openDatabase;
    private FirstRunWorkflowState _state;

    public FirstRunViewModel(FirstRunWorkflow workflow, PdfDiscoveryService discovery)
    {
        _workflow = workflow;
        _discovery = discovery;
        _state = new FirstRunWorkflowState(FirstRunStep.Library, "Create a library identity.", null, null, null, null, null, null, false);
        OpenDatabaseCommand = new AsyncCommand(OpenDatabaseAsync);
        CreateLibraryCommand = new AsyncCommand(CreateLibraryAsync);
        ScanCommand = new AsyncCommand(ScanDirectoryAsync);
        ImportCommand = new AsyncCommand(ImportPdfAsync);
        RunMinerUCommand = new AsyncCommand(RunMinerUExtractionAsync);
        FinishSetupCommand = new AsyncCommand(FinishSetupAsync);
    }

    public FirstRunViewModel(Func<string, Task<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>> openDatabase)
    {
        _openDatabase = openDatabase;
        _state = FirstRunWorkflowState.Initial();
        OpenDatabaseCommand = new AsyncCommand(OpenDatabaseAsync);
        CreateLibraryCommand = new AsyncCommand(CreateLibraryAsync);
        ScanCommand = new AsyncCommand(ScanDirectoryAsync);
        ImportCommand = new AsyncCommand(ImportPdfAsync);
        RunMinerUCommand = new AsyncCommand(RunMinerUExtractionAsync);
        FinishSetupCommand = new AsyncCommand(FinishSetupAsync);
    }

    public string CurrentStep => _state.CurrentStep;
    public string ProgressText => _state.ProgressText;
    public string? LastError => _state.LastError;
    public bool IsComplete => _state.IsComplete;
    public bool HasError => !string.IsNullOrWhiteSpace(_state.LastError);

    private string _databasePath = "";
    public string DatabasePath
    {
        get => _databasePath;
        set
        {
            if (_databasePath == value) return;
            _databasePath = value;
            Raise();
        }
    }

    public string LibraryName { get; set; } = "My Library";

    private string _scanRoot = "";
    public string ScanRoot
    {
        get => _scanRoot;
        set
        {
            if (_scanRoot == value) return;
            _scanRoot = value;
            Raise();
        }
    }

    public ObservableCollection<PdfCandidateViewModel> PdfCandidates { get; } = new();
    public PdfCandidateViewModel? SelectedPdf { get; set; }
    public string ItemTitle { get; set; } = "";
    public string ItemAuthors { get; set; } = "";
    public string MinerUToken { get; set; } = "";
    public int ImportedPdfCount { get; set; }
    public int FailedImportCount { get; set; }

    public bool ShowInitStep => _state.CurrentStep == FirstRunStep.Database;
    public bool ShowLibraryStep => _state.CurrentStep == FirstRunStep.Library;
    public bool ShowScanStep => _state.CurrentStep == FirstRunStep.Scan;
    public bool ShowImportStep => false;
    public bool ShowMinerUConfigStep => _state.CurrentStep == FirstRunStep.MinerUConfig;
    public bool ShowExtractStep => _state.CurrentStep == FirstRunStep.Extract;
    public bool ShowIndexStep => _state.CurrentStep == FirstRunStep.Index;
    public bool ShowVerifyStep => _state.CurrentStep == FirstRunStep.McpVerify;
    public bool ShowCompleteStep => _state.CurrentStep == FirstRunStep.Complete;
    public bool IsBusy { get; set; }

    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CreateLibraryCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand RunMinerUCommand { get; }
    public AsyncCommand FinishSetupCommand { get; }

    public async Task OpenDatabaseAsync()
    {
        if (_openDatabase is null || string.IsNullOrWhiteSpace(DatabasePath)) return;
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            var opened = await _openDatabase(DatabasePath);
            _workflow = opened.Workflow;
            _discovery = opened.Discovery;
            _state = new FirstRunWorkflowState(FirstRunStep.Library, "Database ready. Create a library identity.", null, null, null, null, null, null, false);
        }
        catch (Exception ex)
        {
            _state = new FirstRunWorkflowState(FirstRunStep.Database, "Could not open database.", null, null, null, null, null, ex.Message, false);
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    public async Task CreateLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(LibraryName)) return;
        if (_workflow is null) { SetWorkflowMissingError(); return; }
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            _state = await _workflow.CreateLibraryAsync(LibraryName);
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    public async Task ScanDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(ScanRoot)) return;
        if (_workflow is null || _discovery is null) { SetWorkflowMissingError(); return; }
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            var libraryId = _state.CreatedLibraryId;
            _state = await _workflow.ScanDirectoryAsync(ScanRoot);
            var scanResult = await _discovery.ScanDirectoryAsync(ScanRoot);
            PdfCandidates.Clear();
            foreach (var c in scanResult.Candidates)
                PdfCandidates.Add(new PdfCandidateViewModel(c));
            Raise(nameof(PdfCandidates));

            if (scanResult.Candidates.Count == 0)
                return;

            ImportedPdfCount = 0;
            FailedImportCount = 0;
            string? lastDocumentInstanceId = null;
            string? lastPdfPath = null;

            foreach (var candidate in scanResult.Candidates)
            {
                var importState = await _workflow.ImportPdfAsync(
                    new PdfImportRequest(candidate.Path, null, null, candidate.PageCount ?? 1),
                    cancellationToken: default);

                if (string.IsNullOrWhiteSpace(importState.LastError))
                {
                    ImportedPdfCount++;
                    lastDocumentInstanceId = importState.CreatedDocumentInstanceId;
                    lastPdfPath = candidate.Path;
                }
                else
                {
                    FailedImportCount++;
                }
            }

            _state = ImportedPdfCount > 0
                ? new FirstRunWorkflowState(
                    FirstRunStep.MinerUConfig,
                    FailedImportCount == 0
                        ? $"已导入 {ImportedPdfCount} 个 PDF 题录。配置 MinerU token 后，请从题录右键菜单运行 OCR。"
                        : $"已导入 {ImportedPdfCount} 个 PDF 题录；{FailedImportCount} 个文件未能导入。配置 MinerU token 后，请从题录右键菜单运行 OCR。",
                    lastPdfPath,
                    libraryId,
                    null,
                    null,
                    lastDocumentInstanceId,
                    null,
                    false)
                : new FirstRunWorkflowState(
                    FirstRunStep.Scan,
                    "PDF files were found, but none could be imported.",
                    null,
                    libraryId,
                    null,
                    null,
                    null,
                    "No scanned PDF could be imported.",
                    false);

            Raise(nameof(ImportedPdfCount));
            Raise(nameof(FailedImportCount));
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    public async Task ImportPdfAsync()
    {
        if (SelectedPdf is null) return;
        if (_workflow is null) { SetWorkflowMissingError(); return; }
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            var request = new PdfImportRequest(SelectedPdf.Path, ItemTitle, ItemAuthors, null);
            _state = await _workflow.ImportPdfAsync(request);
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    public async Task RunMinerUExtractionAsync()
    {
        if (string.IsNullOrWhiteSpace(MinerUToken))
        {
            _state = new FirstRunWorkflowState(
                FirstRunStep.MinerUConfig,
                "运行 OCR 前请输入 MinerU API token。",
                _state.SelectedPdfPath,
                _state.CreatedLibraryId,
                _state.CreatedItemId,
                _state.CreatedFileAssetId,
                _state.CreatedDocumentInstanceId,
                "开始 OCR 前需要 MinerU API token。",
                false);
            RaiseAll();
            return;
        }

        if (string.IsNullOrWhiteSpace(_state.CreatedDocumentInstanceId)) return;
        if (_workflow is null) { SetWorkflowMissingError(); return; }
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "Patchouli", "mineru-cache");
            var config = new MinerUConfiguration(MinerUToken, null, null, true, true, true);
            _state = await _workflow.RunMinerUExtractionAsync(
                config, SelectedPdf?.Path ?? "", cacheDir, _state.CreatedDocumentInstanceId);
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    public Task FinishSetupAsync()
    {
        if (string.IsNullOrWhiteSpace(MinerUToken))
        {
            _state = new FirstRunWorkflowState(
                FirstRunStep.MinerUConfig,
                "完成初始化前请输入 MinerU API token。",
                _state.SelectedPdfPath,
                _state.CreatedLibraryId,
                _state.CreatedItemId,
                _state.CreatedFileAssetId,
                _state.CreatedDocumentInstanceId,
                "完成初始化前需要 MinerU API token。",
                false);
            RaiseAll();
            return Task.CompletedTask;
        }

        _state = new FirstRunWorkflowState(
            FirstRunStep.Complete,
            "初始化完成。请在文献列表中选择一个已导入题录，并从右键菜单运行 MinerU OCR。",
            _state.SelectedPdfPath,
            _state.CreatedLibraryId,
            _state.CreatedItemId,
            _state.CreatedFileAssetId,
            _state.CreatedDocumentInstanceId,
            null,
            true);
        RaiseAll();
        return Task.CompletedTask;
    }

    private void SetWorkflowMissingError()
    {
        _state = new FirstRunWorkflowState(FirstRunStep.Database, "Open a runtime database first.", null, null, null, null, null, "Open a runtime database first.", false);
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(CurrentStep));
        Raise(nameof(ProgressText));
        Raise(nameof(LastError));
        Raise(nameof(HasError));
        Raise(nameof(IsComplete));
        Raise(nameof(ImportedPdfCount));
        Raise(nameof(FailedImportCount));
        Raise(nameof(ShowInitStep));
        Raise(nameof(ShowLibraryStep));
        Raise(nameof(ShowScanStep));
        Raise(nameof(ShowImportStep));
        Raise(nameof(ShowMinerUConfigStep));
        Raise(nameof(ShowExtractStep));
        Raise(nameof(ShowIndexStep));
        Raise(nameof(ShowVerifyStep));
        Raise(nameof(ShowCompleteStep));
    }
}
