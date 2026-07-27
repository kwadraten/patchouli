using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Infrastructure.Files;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class LibrarySettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly ObservableCollection<FileSearchRootSettingsRowViewModel> _fileSearchRoots = new();
    private bool _rememberLastDatabase;
    private string _exclusionPatternsText;
    private string _persistedExclusionPatternsText;
    private bool _isDirty;

    public LibrarySettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        AddFileSearchRootCommand = new AsyncCommand(AddFileSearchRootAsync);
        RescanFileSearchRootsCommand = new AsyncCommand(RescanFileSearchRootsAsync);
        _rememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
        _persistedExclusionPatternsText =
            string.Join(Environment.NewLine, _main.AppOptions.FileScanning.ExclusionPatterns);
        _exclusionPatternsText = _persistedExclusionPatternsText;
        LoadFileSearchRootsAsync().Observe(nameof(LibrarySettingsViewModel), nameof(LoadFileSearchRootsAsync));
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadFileSearchRootsAsync();
    }

    public async Task LoadFileSearchRootsAsync()
    {
        _fileSearchRoots.Clear();
        if (!_main.HasOpenRuntimeDatabase)
        {
            Raise(nameof(FileSearchRoots));
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<IReadOnlyList<FileSearchRoot>> roots = await services.FileResolution.ListSearchRootsAsync();
        if (roots.IsFailure)
        {
            SetStatus(roots.ErrorMessage ?? "无法读取文件搜索根。");
            return;
        }

        foreach (FileSearchRoot root in roots.Value)
        {
            _fileSearchRoots.Add(new FileSearchRootSettingsRowViewModel(this, root));
        }

        Raise(nameof(FileSearchRoots));
    }

    public string RuntimeDatabasePath => _main.RuntimeDatabasePath;
    public string DefaultSyncRootPath => _main.DefaultSyncRootPath;

    public bool RememberLastDatabase
    {
        get => _rememberLastDatabase;
        set
        {
            if (_rememberLastDatabase != value)
            {
                _rememberLastDatabase = value;
                Raise();
                MarkDirty();
            }
        }
    }

    public void NotifyRuntimeDatabasePathChanged()
    {
        Raise(nameof(RuntimeDatabasePath));
    }

    public string FileSearchRootInput { get; set; } = "";

    public string ExclusionPatternsText
    {
        get => _exclusionPatternsText;
        set
        {
            if (_exclusionPatternsText == value)
            {
                return;
            }

            _exclusionPatternsText = value;
            Raise();
            MarkDirty();
        }
    }

    public SelectedFileSearchRoot? SelectedFileSearchRoot { get; set; }
    public ObservableCollection<FileSearchRootSettingsRowViewModel> FileSearchRoots => _fileSearchRoots;

    public AsyncCommand AddFileSearchRootCommand { get; }
    public AsyncCommand RescanFileSearchRootsCommand { get; }

    public override bool SupportsEditing => true;
    public override bool IsDirty => _isDirty;
    public override bool CanSave => _isDirty;

    public override Task DiscardAsync()
    {
        _rememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
        _exclusionPatternsText = _persistedExclusionPatternsText;
        _isDirty = false;
        Raise(nameof(RememberLastDatabase));
        Raise(nameof(ExclusionPatternsText));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Clean;
        SetStatus("已放弃更改");
        return Task.CompletedTask;
    }

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        string[] patterns = ExclusionPatternsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                                      StringSplitOptions.TrimEntries);
        if (!FileSearchRootAccess.TryValidateExclusionPatterns(patterns, out string? error))
        {
            SaveState = SettingsSaveState.Failed;
            ValidationState = SettingsValidationState.Invalid;
            SetStatus($"排除规则无效：{error}");
            LastError = error;
            return;
        }

        AppRuntimeOptions runtime = _main.AppOptions.Runtime with { RememberLastDatabase = _rememberLastDatabase };
        if (_rememberLastDatabase)
        {
            runtime = runtime with { RuntimeDatabasePath = Path.GetFullPath(_main.RuntimeDatabasePath) };
        }

        SettingsSaveResult saved = _main.UpdateAppOptions(_main.AppOptions with
        {
            Runtime = runtime,
            FileScanning = new FileScanningAppSettings(patterns)
        });
        if (saved.IsSuccess)
        {
            _persistedExclusionPatternsText = _exclusionPatternsText;
            _isDirty = false;
            LastError = null;
            SaveState = SettingsSaveState.Saved;
            ValidationState = SettingsValidationState.Valid;
            SetStatus("已保存");
        }
        else
        {
            LastError = saved.ErrorMessage;
            SaveState = SettingsSaveState.Failed;
            SetStatus($"保存失败：{saved.ErrorMessage}");
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        await Task.CompletedTask;
    }

    private void SetStatus(string text)
    {
        Status = text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _main.Report(text);
        }
    }

    private void MarkDirty()
    {
        _isDirty = true;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    private async Task AddFileSearchRootAsync()
    {
        if (SelectedFileSearchRoot is null)
        {
            return;
        }

        SetStatus("正在登记并扫描文件搜索根...");
        try
        {
            AppServices services = await _main.ServicesAsync();
            Result<FileSearchRoot> added = await services.FileResolution.AddSearchRootAsync(SelectedFileSearchRoot);
            if (added.IsFailure && added.ErrorCode != AppErrorCodes.InvalidState)
            {
                SetStatus(added.ErrorMessage ?? "文件搜索根登记失败。");
                return;
            }

            FileSearchRootInput = "";
            SelectedFileSearchRoot = null;
            Raise(nameof(FileSearchRootInput));
            Raise(nameof(SelectedFileSearchRoot));

            await LoadFileSearchRootsAsync();
            await _main.RefreshSidebarPathsAsync();
            await _main.RescanFileSearchRootsAsync("文件搜索根已登记，重新扫描完成。", true);
            SetStatus(added.IsSuccess ? "文件搜索根已登记，扫描结果已记录。" : "文件搜索根已存在，已刷新状态。");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private async Task RescanFileSearchRootsAsync()
    {
        Result<FileSearchRootRescanSummary> result = await _main.RescanFileSearchRootsAsync("手动重新扫描完成。", true);
        if (result.IsSuccess)
        {
            await LoadFileSearchRootsAsync();
            SetStatus(
                $"手动重新扫描完成：新增 {result.Value.ImportedPdfCount} 个，已存在 {result.Value.SkippedKnownPdfCount} 个，失败 {result.Value.FailedPdfCount} 个。");
        }
    }

    internal async Task DeleteFileSearchRootAsync(FileSearchRootId rootId, string rootPath)
    {
        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "移除搜索目录",
                $"将停止跟踪目录：\n{rootPath}\n\n已导入的文献不受影响。",
                "移除",
                confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result deleted = await services.FileResolution.DeleteSearchRootAsync(rootId);
        if (deleted.IsFailure)
        {
            SetStatus(deleted.ErrorMessage ?? "文件搜索根删除失败。");
            return;
        }

        await LoadFileSearchRootsAsync();
        await _main.RefreshSidebarPathsAsync();
        SetStatus($"已删除文件搜索根：{rootPath}");
    }
}

public sealed class FileSearchRootSettingsRowViewModel : ViewModelBase
{
    private readonly LibrarySettingsViewModel _parent;

    public FileSearchRootSettingsRowViewModel(LibrarySettingsViewModel parent, FileSearchRoot root)
    {
        _parent = parent;
        RootId = root.RootId;
        RootPath = root.RootPath;
        DeleteCommand = new AsyncCommand(() => _parent.DeleteFileSearchRootAsync(RootId, RootPath));
    }

    public FileSearchRootId RootId { get; }
    public string RootPath { get; }
    public AsyncCommand DeleteCommand { get; }
}
