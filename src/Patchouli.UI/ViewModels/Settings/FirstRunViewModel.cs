using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Host.Import;
using Patchouli.Infrastructure.Workflows;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels;

public sealed partial class FirstRunViewModel : ViewModelBase
{
    private FirstRunWorkflow? _workflow;
    private PdfDiscoveryService? _discovery;
    private LibraryImportOrchestrator? _orchestrator;
    private readonly Func<string, Task<LibraryImportOrchestrator?>>? _openDatabase;
    private readonly IModalOperationRunner? _modalOperations;
    public Action<string>? OnError { get; set; }
    public Action<string>? OnProgress { get; set; }

    private FirstRunWorkflowState _state;
    private ExistingDatabaseSetup? _existingDatabaseSetup;

    private FirstRunWorkflowState State
    {
        get => _state;
        set
        {
            FirstRunWorkflowState newState = value;
            if (!string.IsNullOrWhiteSpace(newState.LastError))
            {
                OnError?.Invoke(newState.LastError);
                newState = new FirstRunWorkflowState(
                    newState.CurrentStep,
                    _state.ProgressText,
                    newState.SelectedPdfPath,
                    newState.CreatedLibraryId,
                    newState.CreatedItemId,
                    newState.CreatedFileAssetId,
                    newState.CreatedDocumentInstanceId,
                    newState.LastError,
                    newState.IsComplete);
            }

            if (EqualityComparer<FirstRunWorkflowState>.Default.Equals(_state, newState))
            {
                return;
            }

            _state = newState;
            if (!string.IsNullOrWhiteSpace(newState.ProgressText))
            {
                OnProgress?.Invoke(newState.ProgressText);
            }

            OnPropertyChanged(nameof(State));
            NotifyStateChanged();
        }
    }

    public FirstRunViewModel(FirstRunWorkflow workflow, PdfDiscoveryService discovery)
    {
        _workflow = workflow;
        _discovery = discovery;
        _state = new FirstRunWorkflowState(FirstRunStep.Library, "Create a library identity.", null, null, null, null,
            null, null, false);
        OpenDatabaseCommand = new AsyncCommand(OpenDatabaseAsync);
        CreateLibraryCommand = new AsyncCommand(CreateLibraryAsync);
        ScanCommand = new AsyncCommand(ScanDirectoryAsync);
        ImportCommand = new AsyncCommand(ImportPdfAsync);
        FinishSetupCommand = new AsyncCommand(FinishSetupAsync);
        CompleteCommand = FinishSetupCommand;
    }

    public FirstRunViewModel(
        Func<string, Task<LibraryImportOrchestrator?>> openDatabase,
        IModalOperationRunner? modalOperations = null,
        Func<Task>? complete = null)
    {
        _openDatabase = openDatabase;
        _modalOperations = modalOperations;
        _state = FirstRunWorkflowState.Initial();
        OpenDatabaseCommand = new AsyncCommand(OpenDatabaseAsync);
        CreateLibraryCommand = new AsyncCommand(CreateLibraryAsync);
        ScanCommand = new AsyncCommand(ScanDirectoryAsync);
        ImportCommand = new AsyncCommand(ImportPdfAsync);
        FinishSetupCommand = new AsyncCommand(FinishSetupAsync);
        CompleteCommand = new AsyncCommand(complete ?? FinishSetupAsync);
    }

    [ExcludeFromDerivedGeneration] public string CurrentStep => State.CurrentStep;

    [ExcludeFromDerivedGeneration] public string ProgressText => State.ProgressText;

    [ExcludeFromDerivedGeneration] public string? LastError => State.LastError;

    [ExcludeFromDerivedGeneration] public bool IsComplete => State.IsComplete;

    [ExcludeFromDerivedGeneration] public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    [ObservableProperty] public partial string? DatabasePath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateMode))]
    public partial bool IsImportMode { get; set; }

    public bool IsCreateMode
    {
        get => !IsImportMode;
        set
        {
            if (value)
            {
                IsImportMode = false;
            }
        }
    }

    public Controls.PathPickerMode DatabasePickerMode =>
        IsImportMode ? Controls.PathPickerMode.OpenFile : Controls.PathPickerMode.SaveFile;

    [ObservableProperty] public partial string LibraryName { get; set; } = "My Library";

    [ObservableProperty] public partial string ScanRoot { get; set; } = "";

    [ObservableProperty] public partial SelectedFileSearchRoot? SelectedScanRoot { get; set; }

    [ExcludeFromDerivedGeneration] public ObservableCollection<PdfCandidateViewModel> PdfCandidates { get; } = new();

    [ObservableProperty] public partial PdfCandidateViewModel? SelectedPdf { get; set; }

    [ObservableProperty] public partial string ItemTitle { get; set; } = "";

    [ObservableProperty] public partial string ItemAuthors { get; set; } = "";

    [ObservableProperty] public partial string MinerUToken { get; set; } = "";

    [ObservableProperty] public partial int ImportedPdfCount { get; set; }

    [ObservableProperty] public partial int FailedImportCount { get; set; }

    [ExcludeFromDerivedGeneration] public bool ShowInitStep => CurrentStep == FirstRunStep.Database;

    [ExcludeFromDerivedGeneration] public bool ShowLibraryStep => CurrentStep == FirstRunStep.Library;

    [ExcludeFromDerivedGeneration] public bool ShowScanStep => CurrentStep == FirstRunStep.Scan;

    [ExcludeFromDerivedGeneration] public bool ShowImportStep => false;

    [ExcludeFromDerivedGeneration] public bool ShowMinerUConfigStep => CurrentStep == FirstRunStep.MinerUConfig;

    [ExcludeFromDerivedGeneration] public bool ShowExtractStep => CurrentStep == FirstRunStep.Extract;

    [ExcludeFromDerivedGeneration] public bool ShowIndexStep => CurrentStep == FirstRunStep.Index;

    [ExcludeFromDerivedGeneration] public bool ShowVerifyStep => CurrentStep == FirstRunStep.McpVerify;

    [ExcludeFromDerivedGeneration] public bool ShowCompleteStep => CurrentStep == FirstRunStep.Complete;

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ExcludeFromDerivedGeneration]
    public int ProgressPercent => CurrentStep switch
    {
        FirstRunStep.Database => 25,
        FirstRunStep.Library => 50,
        FirstRunStep.Scan => 75,
        FirstRunStep.MinerUConfig => 90,
        FirstRunStep.Complete => 100,
        _ => 0
    };

    [ExcludeFromDerivedGeneration]
    public string StepProgressText => CurrentStep switch
    {
        FirstRunStep.Database => "Step 1 of 4",
        FirstRunStep.Library => "Step 2 of 4",
        FirstRunStep.Scan => "Step 3 of 4",
        FirstRunStep.MinerUConfig or FirstRunStep.Complete => "Step 4 of 4",
        _ => "Step 1 of 4"
    };

    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CreateLibraryCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand FinishSetupCommand { get; }
    public AsyncCommand CompleteCommand { get; }

    public async Task OpenDatabaseAsync()
    {
        if (_openDatabase is null || string.IsNullOrWhiteSpace(DatabasePath))
        {
            return;
        }

        _existingDatabaseSetup = null;

        if (IsImportMode)
        {
            if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0)
            {
                State = new FirstRunWorkflowState(FirstRunStep.Database, "所选数据库文件不存在或为空。", null, null, null, null, null,
                    "所选数据库文件不存在或为空。", false);
                return;
            }

            try
            {
                _existingDatabaseSetup = await ExistingDatabaseSetupInspector.InspectAsync(DatabasePath);
            }
            catch (Exception ex)
            {
                State = new FirstRunWorkflowState(FirstRunStep.Database, "无效的 Patchouli.Net 数据库格式。", null, null, null,
                    null, null, $"验证失败：{ex.Message}", false);
                return;
            }
        }

        IsBusy = true;
        try
        {
            _orchestrator = await _openDatabase(DatabasePath);
            State = _existingDatabaseSetup is null
                ? new FirstRunWorkflowState(FirstRunStep.Library, "数据库已就绪。请创建资料库身份。", null, null, null, null, null,
                    null, false)
                : ToWorkflowState(_existingDatabaseSetup);
        }
        catch (Exception ex)
        {
            State = new FirstRunWorkflowState(FirstRunStep.Database, "无法打开数据库。", null, null, null, null, null,
                ex.Message, false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CreateLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(LibraryName))
        {
            return;
        }

        LibraryImportOrchestrator? orchestrator = _orchestrator;
        FirstRunWorkflow? workflow = _workflow;
        if (orchestrator is null && workflow is null)
        {
            SetWorkflowMissingError();
            return;
        }

        IsBusy = true;
        try
        {
            State = orchestrator is not null
                ? await orchestrator.CreateFirstRunLibraryAsync(LibraryName)
                : await workflow!.CreateLibraryAsync(LibraryName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ScanDirectoryAsync()
    {
        if (SelectedScanRoot is null)
        {
            return;
        }

        LibraryImportOrchestrator? orchestrator = _orchestrator;
        FirstRunWorkflow? workflow = _workflow;
        if (orchestrator is null && workflow is null)
        {
            SetWorkflowMissingError();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            FirstRunImportResult result;
            if (orchestrator is not null)
            {
                result = _modalOperations is null
                    ? await orchestrator.ScanDirectoryAndImportAsync(SelectedScanRoot, _state.CreatedLibraryId)
                    : await _modalOperations.RunAsync(
                        new ModalOperationOptions(
                            "初次扫描与导入",
                            "正在扫描所选目录并导入 PDF 题录。",
                            true),
                        context => orchestrator.ScanDirectoryAndImportAsync(
                            SelectedScanRoot,
                            _state.CreatedLibraryId,
                            context.CancellationToken,
                            context.Report));
            }
            else
            {
                result = _modalOperations is null
                    ? await workflow!.ScanAndImportAsync(SelectedScanRoot, _state.CreatedLibraryId)
                    : await _modalOperations.RunAsync(
                        new ModalOperationOptions(
                            "初次扫描与导入",
                            "正在扫描所选目录并导入 PDF 题录。",
                            true),
                        context => workflow!.ScanAndImportAsync(
                            SelectedScanRoot,
                            _state.CreatedLibraryId,
                            context.CancellationToken,
                            context.Report));
            }

            State = result.State;
            PdfCandidates.Clear();
            foreach (PdfCandidate c in result.ScanResult.Candidates)
            {
                PdfCandidates.Add(new PdfCandidateViewModel(c));
            }

            ImportedPdfCount = result.ImportedCount;
            FailedImportCount = result.FailedCount;
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            State = new FirstRunWorkflowState(
                FirstRunStep.Scan,
                "扫描与导入已取消。",
                _state.SelectedPdfPath,
                _state.CreatedLibraryId,
                _state.CreatedItemId,
                _state.CreatedFileAssetId,
                _state.CreatedDocumentInstanceId,
                "操作已取消。",
                false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportPdfAsync()
    {
        if (SelectedPdf is null)
        {
            return;
        }

        LibraryImportOrchestrator? orchestrator = _orchestrator;
        FirstRunWorkflow? workflow = _workflow;
        if (orchestrator is null && workflow is null)
        {
            SetWorkflowMissingError();
            return;
        }

        IsBusy = true;
        try
        {
            PdfImportRequest request = new(SelectedPdf.Path, ItemTitle, ItemAuthors, null);
            if (orchestrator is not null)
            {
                State = _modalOperations is null
                    ? await orchestrator.ImportFirstRunPdfAsync(request)
                    : await _modalOperations.RunAsync(
                        new ModalOperationOptions(
                            "导入 PDF 题录",
                            "正在读取 PDF 并创建题录。",
                            true),
                        context => orchestrator.ImportFirstRunPdfAsync(request, context.CancellationToken));
            }
            else
            {
                State = _modalOperations is null
                    ? await workflow!.ImportPdfAsync(request)
                    : await _modalOperations.RunAsync(
                        new ModalOperationOptions(
                            "导入 PDF 题录",
                            "正在读取 PDF 并创建题录。",
                            true),
                        context => workflow!.ImportPdfAsync(request, context.CancellationToken));
            }
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            State = new FirstRunWorkflowState(
                FirstRunStep.Import,
                "PDF 导入已取消。",
                SelectedPdf.Path,
                _state.CreatedLibraryId,
                _state.CreatedItemId,
                _state.CreatedFileAssetId,
                _state.CreatedDocumentInstanceId,
                "操作已取消。",
                false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task FinishSetupAsync()
    {
        if (string.IsNullOrWhiteSpace(MinerUToken))
        {
            State = new FirstRunWorkflowState(
                FirstRunStep.MinerUConfig,
                "完成初始化前请输入 MinerU API token。",
                _state.SelectedPdfPath,
                _state.CreatedLibraryId,
                _state.CreatedItemId,
                _state.CreatedFileAssetId,
                _state.CreatedDocumentInstanceId,
                "完成初始化前需要 MinerU API token。",
                false);
            return Task.CompletedTask;
        }

        State = new FirstRunWorkflowState(
            FirstRunStep.Complete,
            "初始化完成。请在文献列表中选择一个已导入题录，并从右键菜单运行 MinerU OCR。",
            _state.SelectedPdfPath,
            _state.CreatedLibraryId,
            _state.CreatedItemId,
            _state.CreatedFileAssetId,
            _state.CreatedDocumentInstanceId,
            null,
            true);
        return Task.CompletedTask;
    }

    private static FirstRunWorkflowState ToWorkflowState(ExistingDatabaseSetup setup)
    {
        return new FirstRunWorkflowState(setup.CurrentStep, setup.ProgressText, null, setup.LibraryId, null, null,
            null, null, setup.IsComplete);
    }

    private void SetWorkflowMissingError()
    {
        State = new FirstRunWorkflowState(FirstRunStep.Database, "请先打开一个运行时数据库。", null, null, null, null, null,
            "请先打开一个运行时数据库。", false);
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(ShowInitStep));
        OnPropertyChanged(nameof(ShowLibraryStep));
        OnPropertyChanged(nameof(ShowScanStep));
        OnPropertyChanged(nameof(ShowImportStep));
        OnPropertyChanged(nameof(ShowMinerUConfigStep));
        OnPropertyChanged(nameof(ShowExtractStep));
        OnPropertyChanged(nameof(ShowIndexStep));
        OnPropertyChanged(nameof(ShowVerifyStep));
        OnPropertyChanged(nameof(ShowCompleteStep));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(StepProgressText));
    }
}
