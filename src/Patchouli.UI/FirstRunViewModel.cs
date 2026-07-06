using System.Collections.ObjectModel;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Workflows;

namespace Patchouli.UI;

public sealed class FirstRunViewModel : ViewModelBase
{
    private FirstRunWorkflow? _workflow;
    private PdfDiscoveryService? _discovery;
    private readonly Func<string, Task<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>>? _openDatabase;
    public Action<string>? OnError { get; set; }
    private FirstRunWorkflowState State
    {
        get => _state;
        set
        {
            var newState = value;
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
            _state = newState;
        }
    }

    private FirstRunWorkflowState _state;
    private ExistingDatabaseSetupState? _existingDatabaseSetup;

    public FirstRunViewModel(FirstRunWorkflow workflow, PdfDiscoveryService discovery)
    {
        _workflow = workflow;
        _discovery = discovery;
        _state = new FirstRunWorkflowState(FirstRunStep.Library, "Create a library identity.", null, null, null, null, null, null, false);
        OpenDatabaseCommand = new AsyncCommand(OpenDatabaseAsync);
        CreateLibraryCommand = new AsyncCommand(CreateLibraryAsync);
        ScanCommand = new AsyncCommand(ScanDirectoryAsync);
        ImportCommand = new AsyncCommand(ImportPdfAsync);
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
        FinishSetupCommand = new AsyncCommand(FinishSetupAsync);
    }

    public string CurrentStep => _state.CurrentStep;
    public string ProgressText => _state.ProgressText;
    public string? LastError => _state.LastError;
    public bool IsComplete => _state.IsComplete;
    public bool HasError => !string.IsNullOrWhiteSpace(_state.LastError);

    private string? _databasePath = "";
    private bool _isImportMode;
    public bool IsImportMode
    {
        get => _isImportMode;
        set
        {
            if (_isImportMode != value)
            {
                _isImportMode = value;
                Raise(nameof(IsImportMode));
                Raise(nameof(DatabasePickerMode));
            }
        }
    }

    public Controls.PathPickerMode DatabasePickerMode => _isImportMode ? Controls.PathPickerMode.OpenFile : Controls.PathPickerMode.SaveFile;

    public string? DatabasePath
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

    public int ProgressPercent
    {
        get
        {
            if (_state.CurrentStep == FirstRunStep.Database) return 25;
            if (_state.CurrentStep == FirstRunStep.Library) return 50;
            if (_state.CurrentStep == FirstRunStep.Scan) return 75;
            if (_state.CurrentStep == FirstRunStep.MinerUConfig) return 90;
            if (_state.CurrentStep == FirstRunStep.Complete) return 100;
            return 0;
        }
    }

    public string StepProgressText
    {
        get
        {
            if (_state.CurrentStep == FirstRunStep.Database) return "Step 1 of 4";
            if (_state.CurrentStep == FirstRunStep.Library) return "Step 2 of 4";
            if (_state.CurrentStep == FirstRunStep.Scan) return "Step 3 of 4";
            if (_state.CurrentStep == FirstRunStep.MinerUConfig) return "Step 4 of 4";
            if (_state.CurrentStep == FirstRunStep.Complete) return "Step 4 of 4";
            return "Step 1 of 4";
        }
    }

    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CreateLibraryCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand FinishSetupCommand { get; }

    public async Task OpenDatabaseAsync()
    {
        if (_openDatabase is null || string.IsNullOrWhiteSpace(DatabasePath)) return;
        _existingDatabaseSetup = null;

        if (IsImportMode)
        {
            if (!System.IO.File.Exists(DatabasePath) || new System.IO.FileInfo(DatabasePath).Length == 0)
            {
                State = new FirstRunWorkflowState(FirstRunStep.Database, "所选数据库文件不存在或为空。", null, null, null, null, null, "所选数据库文件不存在或为空。", false);
                RaiseAll();
                return;
            }
            try
            {
                _existingDatabaseSetup = await InspectExistingDatabaseAsync(DatabasePath);
            }
            catch (Exception ex)
            {
                State = new FirstRunWorkflowState(FirstRunStep.Database, "无效的 Patchouli 数据库格式。", null, null, null, null, null, $"验证失败：{ex.Message}", false);
                RaiseAll();
                return;
            }
        }

        IsBusy = true; Raise(nameof(IsBusy));
        try
        {
            var opened = await _openDatabase(DatabasePath);
            _workflow = opened.Workflow;
            _discovery = opened.Discovery;
            State = _existingDatabaseSetup is null
                ? new FirstRunWorkflowState(FirstRunStep.Library, "数据库已就绪。请创建资料库身份。", null, null, null, null, null, null, false)
                : _existingDatabaseSetup.ToWorkflowState();
        }
        catch (Exception ex)
        {
            State = new FirstRunWorkflowState(FirstRunStep.Database, "无法打开数据库。", null, null, null, null, null, ex.Message, false);
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
            State = await _workflow.CreateLibraryAsync(LibraryName);
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
            State = await _workflow.ScanDirectoryAsync(ScanRoot);
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
                    new PdfImportRequest(candidate.Path, null, null, candidate.PageCount),
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

            State = ImportedPdfCount > 0
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
                    "扫描到了 PDF 文件，但没有任何文件成功导入。",
                    null,
                    null,
                    null,
                    null,
                    null,
                    "没有任何 PDF 文件被成功导入。",
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
            State = await _workflow.ImportPdfAsync(request);
        }
        finally { IsBusy = false; Raise(nameof(IsBusy)); }
        RaiseAll();
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
            RaiseAll();
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
        RaiseAll();
        return Task.CompletedTask;
    }

    private static async Task<ExistingDatabaseSetupState> InspectExistingDatabaseAsync(string databasePath)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync();

        if (!await TableExistsAsync(conn, "library_metadata"))
            throw new InvalidOperationException("缺少 library_metadata 表。");

        var library = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<LibraryMetadataRow>(
            conn,
            "select library_id as LibraryId, display_name as DisplayName from library_metadata limit 1;");
        if (library is null || string.IsNullOrWhiteSpace(library.LibraryId) || string.IsNullOrWhiteSpace(library.DisplayName))
            throw new InvalidOperationException("缺少 library_metadata 资料库身份数据。");

        var hasSearchRoots = await CountRowsIfTableExistsAsync(conn, "file_search_roots") > 0;
        var hasOcrPresets = await CountRowsIfTableExistsAsync(conn, "ocr_presets") > 0;

        var skipped = new List<string> { $"已检测到资料库「{library.DisplayName}」，跳过资料库身份步骤" };
        if (hasSearchRoots) skipped.Add("已检测到 file_search_roots，跳过文件搜索根配置步骤");
        if (hasOcrPresets) skipped.Add("已检测到 ocr_presets，跳过 OCR Preset 配置步骤");

        var missing = new List<string>();
        if (!hasSearchRoots) missing.Add("缺少 file_search_roots，请在向导中选择 PDF 扫描目录");
        if (!hasOcrPresets) missing.Add("缺少 ocr_presets，请在向导中完成 OCR Preset 配置");

        var step = !hasSearchRoots
            ? FirstRunStep.Scan
            : !hasOcrPresets
                ? FirstRunStep.MinerUConfig
                : FirstRunStep.Complete;

        return new ExistingDatabaseSetupState(
            library.LibraryId,
            step,
            string.Join("；", skipped.Concat(missing)),
            step == FirstRunStep.Complete);
    }

    private static async Task<bool> TableExistsAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string tableName)
    {
        var count = await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            conn,
            "select count(1) from sqlite_master where type = 'table' and name = @TableName;",
            new { TableName = tableName });
        return count > 0;
    }

    private static async Task<int> CountRowsIfTableExistsAsync(Microsoft.Data.Sqlite.SqliteConnection conn, string tableName)
    {
        if (!await TableExistsAsync(conn, tableName))
            return 0;

        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, $"select count(1) from {tableName};");
    }

    private sealed class LibraryMetadataRow
    {
        public string LibraryId { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    private sealed record ExistingDatabaseSetupState(
        string LibraryId,
        string CurrentStep,
        string ProgressText,
        bool IsComplete)
    {
        public FirstRunWorkflowState ToWorkflowState() =>
            new(CurrentStep, ProgressText, null, LibraryId, null, null, null, null, IsComplete);
    }

    private void SetWorkflowMissingError()
    {
        State = new FirstRunWorkflowState(FirstRunStep.Database, "请先打开一个运行时数据库。", null, null, null, null, null, "请先打开一个运行时数据库。", false);
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
        Raise(nameof(ProgressPercent));
        Raise(nameof(StepProgressText));
    }
}
