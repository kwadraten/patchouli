using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Watching;
using Patchouli.Infrastructure.Files;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class LibrarySettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly ObservableCollection<FileSearchRootSettingsRowViewModel> _fileSearchRoots = new();
    private string _persistedExclusionPatternsText;
    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;

    public LibrarySettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        AddFileSearchRootCommand = new AsyncCommand(AddFileSearchRootAsync);
        RescanFileSearchRootsCommand = new AsyncCommand(RescanFileSearchRootsAsync);
        RememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
        _persistedExclusionPatternsText =
            string.Join(Environment.NewLine, _main.AppOptions.FileScanning.ExclusionPatterns);
        ExclusionPatternsText = _persistedExclusionPatternsText;
        _isConstructing = false;
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

        HostServices services = await _main.ServicesAsync();
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

    [ExcludeFromDerivedGeneration] public string RuntimeDatabasePath => _main.RuntimeDatabasePath;

    [ExcludeFromDerivedGeneration] public string DefaultSyncRootPath => _main.DefaultSyncRootPath;

    [ObservableProperty] public partial bool RememberLastDatabase { get; set; }

    partial void OnRememberLastDatabaseChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        MarkDirty();
    }

    public void NotifyRuntimeDatabasePathChanged()
    {
        Raise(nameof(RuntimeDatabasePath));
    }

    [ObservableProperty] public partial string FileSearchRootInput { get; set; } = "";

    [ObservableProperty] public partial string ExclusionPatternsText { get; set; } = "";

    partial void OnExclusionPatternsTextChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        MarkDirty();
    }

    [ObservableProperty] public partial SelectedFileSearchRoot? SelectedFileSearchRoot { get; set; }

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<FileSearchRootSettingsRowViewModel> FileSearchRoots => _fileSearchRoots;

    public AsyncCommand AddFileSearchRootCommand { get; }
    public AsyncCommand RescanFileSearchRootsCommand { get; }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty;

    public override Task DiscardAsync()
    {
        _isSyncing = true;
        try
        {
            RememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
            ExclusionPatternsText = _persistedExclusionPatternsText;
        }
        finally
        {
            _isSyncing = false;
        }

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

        AppRuntimeOptions runtime = _main.AppOptions.Runtime with { RememberLastDatabase = RememberLastDatabase };
        if (RememberLastDatabase)
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
            _persistedExclusionPatternsText = ExclusionPatternsText;
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
            HostServices services = await _main.ServicesAsync();
            SelectedFileSearchRoot selectedRoot = SelectedFileSearchRoot;
            // Registration traverses the whole directory tree (on macOS via native filesystem
            // calls); keep it off the UI thread.
            Result<FileSearchRoot> added =
                await Task.Run(() => services.FileResolution.AddSearchRootAsync(selectedRoot));
            await _main.LogOperationAsync("file-scan",
                $"Search root registration (trigger=add-root): {selectedRoot.DisplayPath} -> " +
                (added.IsSuccess ? "registered" : $"{added.ErrorCode}: {added.ErrorMessage}"));
            if (added.IsFailure && added.ErrorCode != AppErrorCodes.InvalidState)
            {
                SetStatus(added.ErrorMessage ?? "文件搜索根登记失败。");
                return;
            }

            FileSearchRootInput = "";
            SelectedFileSearchRoot = null;

            await LoadFileSearchRootsAsync();
            await _main.RefreshSidebarPathsAsync();
            await _main.RescanFileSearchRootsAsync("文件搜索根已登记，重新扫描完成。", true,
                trigger: "add-root");
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

        HostServices services = await _main.ServicesAsync();
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

public sealed partial class FileSearchRootSettingsRowViewModel : ViewModelBase
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
