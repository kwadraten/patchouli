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

public sealed class LibrarySettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _status = "";
    private readonly ObservableCollection<FileSearchRootSettingsRowViewModel> _fileSearchRoots = new();

    public LibrarySettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        AddFileSearchRootCommand = new AsyncCommand(AddFileSearchRootAsync);
        RescanFileSearchRootsCommand = new AsyncCommand(RescanFileSearchRootsAsync);
        SaveExclusionPatternsCommand = new AsyncCommand(SaveExclusionPatternsAsync);
        ExclusionPatternsText = string.Join(Environment.NewLine, _main.AppOptions.FileScanning.ExclusionPatterns);
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
        get => _main.AppOptions.Runtime.RememberLastDatabase;
        set
        {
            if (_main.AppOptions.Runtime.RememberLastDatabase != value)
            {
                PatchouliAppSettings options = _main.AppOptions;
                AppRuntimeOptions runtime = options.Runtime with { RememberLastDatabase = value };
                if (value)
                {
                    runtime = runtime with { RuntimeDatabasePath = Path.GetFullPath(_main.RuntimeDatabasePath) };
                }

                SettingsSaveResult saved = _main.UpdateAppOptions(options with { Runtime = runtime });
                Status = saved.IsSuccess ? "已保存" : $"保存失败：{saved.ErrorMessage}";
                if (saved.IsSuccess)
                {
                    Raise();
                }
            }
        }
    }

    public void NotifyRuntimeDatabasePathChanged()
    {
        Raise(nameof(RuntimeDatabasePath));
    }

    public string FileSearchRootInput { get; set; } = "";
    public string ExclusionPatternsText { get; set; }
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

    private async Task SaveExclusionPatternsAsync()
    {
        string[] patterns = ExclusionPatternsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                                                                      StringSplitOptions.TrimEntries);
        if (!FileSearchRootAccess.TryValidateExclusionPatterns(patterns, out string? error))
        {
            Status = $"排除规则无效：{error}";
            return;
        }

        SettingsSaveResult saved = _main.UpdateAppOptions(_main.AppOptions with
        {
            FileScanning = new FileScanningAppSettings(patterns)
        });
        Status = saved.IsSuccess ? "排除规则已保存。" : $"保存失败：{saved.ErrorMessage}";
        await Task.CompletedTask;
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
