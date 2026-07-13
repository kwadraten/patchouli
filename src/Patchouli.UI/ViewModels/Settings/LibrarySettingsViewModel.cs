using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Infrastructure.Files;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class LibrarySettingsViewModel : ViewModelBase, ISettingsSection
{
    private readonly MainWindowViewModel _main;
    private string _status = "";
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
        SaveExclusionPatternsCommand = new AsyncCommand(SaveAsync);
        _rememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
        _persistedExclusionPatternsText =
            string.Join(Environment.NewLine, _main.AppOptions.FileScanning.ExclusionPatterns);
        _exclusionPatternsText = _persistedExclusionPatternsText;
        LoadFileSearchRootsAsync().Observe(nameof(LibrarySettingsViewModel), nameof(LoadFileSearchRootsAsync));
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
            Status = roots.ErrorMessage ?? "无法读取文件搜索根。";
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

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value))
            {
                _main.Report(value);
            }
        }
    }

    public AsyncCommand AddFileSearchRootCommand { get; }
    public AsyncCommand RescanFileSearchRootsCommand { get; }
    public AsyncCommand SaveExclusionPatternsCommand { get; }

    public bool SupportsEditing => true;
    public bool IsDirty => _isDirty;
    public bool CanSave => _isDirty;
    public string SaveStateText => Status;
    public string? LastError { get; private set; }

    public Task DiscardAsync()
    {
        _rememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
        _exclusionPatternsText = _persistedExclusionPatternsText;
        _isDirty = false;
        Raise(nameof(RememberLastDatabase));
        Raise(nameof(ExclusionPatternsText));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Status = "已放弃更改";
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        Status = "正在保存...";
        string[] patterns = ExclusionPatternsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                                      StringSplitOptions.TrimEntries);
        if (!FileSearchRootAccess.TryValidateExclusionPatterns(patterns, out string? error))
        {
            Status = $"排除规则无效：{error}";
            LastError = error;
            Raise(nameof(LastError));
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
        Status = saved.IsSuccess ? "排除规则已保存。" : $"保存失败：{saved.ErrorMessage}";
        if (saved.IsSuccess)
        {
            _persistedExclusionPatternsText = _exclusionPatternsText;
            _isDirty = false;
            LastError = null;
        }
        else
        {
            LastError = saved.ErrorMessage;
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(LastError));
        await Task.CompletedTask;
    }

    private void MarkDirty()
    {
        _isDirty = true;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Status = "有未保存的更改";
    }

    private async Task AddFileSearchRootAsync()
    {
        if (SelectedFileSearchRoot is null)
        {
            return;
        }

        Status = "正在登记并扫描文件搜索根...";
        try
        {
            AppServices services = await _main.ServicesAsync();
            Result<FileSearchRoot> added = await services.FileResolution.AddSearchRootAsync(SelectedFileSearchRoot);
            if (added.IsFailure && added.ErrorCode != AppErrorCodes.InvalidState)
            {
                Status = added.ErrorMessage ?? "文件搜索根登记失败。";
                _main.Report(Status);
                return;
            }

            FileSearchRootInput = "";
            SelectedFileSearchRoot = null;
            Raise(nameof(FileSearchRootInput));
            Raise(nameof(SelectedFileSearchRoot));

            await LoadFileSearchRootsAsync();
            await _main.RefreshSidebarPathsAsync();
            await _main.RescanFileSearchRootsAsync("文件搜索根已登记，重新扫描完成。", true);
            Status = added.IsSuccess ? "文件搜索根已登记，扫描结果已记录。" : "文件搜索根已存在，已刷新状态。";
            _main.Report(Status);
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
            _main.Report(Status);
        }
    }

    private async Task RescanFileSearchRootsAsync()
    {
        Result<FileSearchRootRescanSummary> result = await _main.RescanFileSearchRootsAsync("手动重新扫描完成。", true);
        if (result.IsSuccess)
        {
            await LoadFileSearchRootsAsync();
            Status =
                $"手动重新扫描完成：新增 {result.Value.ImportedPdfCount} 个，已存在 {result.Value.SkippedKnownPdfCount} 个，失败 {result.Value.FailedPdfCount} 个。";
        }
    }

    internal async Task DeleteFileSearchRootAsync(FileSearchRootId rootId, string rootPath)
    {
        AppServices services = await _main.ServicesAsync();
        Result deleted = await services.FileResolution.DeleteSearchRootAsync(rootId);
        if (deleted.IsFailure)
        {
            Status = deleted.ErrorMessage ?? "文件搜索根删除失败。";
            return;
        }

        await LoadFileSearchRootsAsync();
        await _main.RefreshSidebarPathsAsync();
        Status = $"已删除文件搜索根：{rootPath}";
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
