using System.Collections.ObjectModel;
using LiteratureApp.Core.Import;
using LiteratureApp.Infrastructure.Workflows;

namespace LiteratureApp.UI;

public sealed class FirstRunViewModel : ViewModelBase
{
    private readonly FirstRunWorkflow _workflow;
    private readonly PdfDiscoveryService _discovery;
    private FirstRunWorkflowState _state;

    public FirstRunViewModel(FirstRunWorkflow workflow, PdfDiscoveryService discovery)
    {
        _workflow = workflow;
        _discovery = discovery;
        _state = FirstRunWorkflowState.Initial();
        CreateLibraryCommand = new AsyncCommand(CreateLibraryAsync);
        ScanCommand = new AsyncCommand(ScanDirectoryAsync);
        ImportCommand = new AsyncCommand(ImportPdfAsync);
        RunMinerUCommand = new AsyncCommand(RunMinerUExtractionAsync);
    }

    public string CurrentStep => _state.CurrentStep;
    public string ProgressText => _state.ProgressText;
    public string? LastError => _state.LastError;
    public bool IsComplete => _state.IsComplete;
    public bool HasError => !string.IsNullOrWhiteSpace(_state.LastError);

    public string DatabasePath { get; set; } = "";
    public string LibraryName { get; set; } = "My Library";
    public string ScanRoot { get; set; } = "";
    public ObservableCollection<PdfCandidateViewModel> PdfCandidates { get; } = new();
    public PdfCandidateViewModel? SelectedPdf { get; set; }
    public string ItemTitle { get; set; } = "";
    public string ItemAuthors { get; set; } = "";
    public string MinerUToken { get; set; } = "";

    public bool ShowInitStep => _state.CurrentStep == FirstRunStep.Database;
    public bool ShowLibraryStep => _state.CurrentStep == FirstRunStep.Library;
    public bool ShowScanStep => _state.CurrentStep == FirstRunStep.Scan;
    public bool ShowImportStep => _state.CurrentStep == FirstRunStep.Import;
    public bool ShowMinerUConfigStep => _state.CurrentStep == FirstRunStep.MinerUConfig;
    public bool ShowExtractStep => _state.CurrentStep == FirstRunStep.Extract;
    public bool ShowIndexStep => _state.CurrentStep == FirstRunStep.Index;
    public bool ShowVerifyStep => _state.CurrentStep == FirstRunStep.McpVerify;
    public bool ShowCompleteStep => _state.CurrentStep == FirstRunStep.Complete;
    public bool IsBusy { get; set; }

    public AsyncCommand CreateLibraryCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand RunMinerUCommand { get; }

    public async Task CreateLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(LibraryName)) return;
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
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            _state = await _workflow.ScanDirectoryAsync(ScanRoot);
            var scanResult = await _discovery.ScanDirectoryAsync(ScanRoot);
            PdfCandidates.Clear();
            foreach (var c in scanResult.Candidates)
                PdfCandidates.Add(new PdfCandidateViewModel(c));
            Raise(nameof(PdfCandidates));
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    public async Task ImportPdfAsync()
    {
        if (SelectedPdf is null) return;
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
        if (string.IsNullOrWhiteSpace(MinerUToken) || string.IsNullOrWhiteSpace(_state.CreatedDocumentInstanceId)) return;
        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), "LiteratureApp", "mineru-cache");
            var config = new MinerUConfiguration(MinerUToken, null, null, true, true, true);
            _state = await _workflow.RunMinerUExtractionAsync(
                config, SelectedPdf?.Path ?? "", cacheDir, _state.CreatedDocumentInstanceId);
            var docId = _state.CreatedDocumentInstanceId;
            if (!HasError && docId is not null)
            {
                _state = await _workflow.RebuildSearchIndexAsync(docId);
                if (!HasError)
                    _state = await _workflow.VerifyMcpSearchAsync(docId);
            }
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(CurrentStep));
        Raise(nameof(ProgressText));
        Raise(nameof(LastError));
        Raise(nameof(HasError));
        Raise(nameof(IsComplete));
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
