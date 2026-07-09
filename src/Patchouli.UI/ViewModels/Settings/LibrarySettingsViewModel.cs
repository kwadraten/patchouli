using Patchouli.Core.Results;
using Patchouli.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class LibrarySettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _status = "";
    private readonly ObservableCollection<string> _fileSearchRoots = new();

    public LibrarySettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        AddFileSearchRootCommand = new AsyncCommand(AddFileSearchRootAsync);
        LoadFileSearchRoots();
    }

    private void LoadFileSearchRoots()
    {
        var roots = string.IsNullOrWhiteSpace(_main.AppOptions.Runtime.FileSearchRoot)
            ? Array.Empty<string>()
            : _main.AppOptions.Runtime.FileSearchRoot.Split(';', StringSplitOptions.RemoveEmptyEntries);
        _fileSearchRoots.Clear();
        foreach (var root in roots)
        {
            _fileSearchRoots.Add(root);
        }
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
                var options = _main.AppOptions;
                _main.UpdateAppOptions(options with { Runtime = options.Runtime with { RememberLastDatabase = value } });
                if (value)
                {
                    var updated = _main.AppOptions;
                    _main.UpdateAppOptions(updated with { Runtime = updated.Runtime with { RuntimeDatabasePath = System.IO.Path.GetFullPath(_main.RuntimeDatabasePath) } });
                }
                Raise();
                Status = "已保存";
            }
        }
    }

    public void NotifyRuntimeDatabasePathChanged() => Raise(nameof(RuntimeDatabasePath));

    public string FileSearchRootInput { get; set; } = "";
    public ObservableCollection<string> FileSearchRoots => _fileSearchRoots;

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value)) _main.Report(value);
        }
    }

    public AsyncCommand AddFileSearchRootCommand { get; }

    private async Task AddFileSearchRootAsync()
    {
        if (string.IsNullOrWhiteSpace(FileSearchRootInput))
        {
            return;
        }

        Status = "正在登记并扫描文件搜索根...";
        try
        {
            var normalizedRoot = System.IO.Path.GetFullPath(FileSearchRootInput.Trim());
            var services = await _main.ServicesAsync();
            var added = await services.FileResolution.AddSearchRootAsync(normalizedRoot);
            if (added.IsFailure && added.ErrorCode != AppErrorCodes.InvalidState)
            {
                Status = added.ErrorMessage ?? "文件搜索根登记失败。";
                _main.Report(Status);
                return;
            }

            var options = _main.AppOptions;
            var currentRoots = string.IsNullOrWhiteSpace(options.Runtime.FileSearchRoot)
                ? Array.Empty<string>()
                : options.Runtime.FileSearchRoot.Split(';', StringSplitOptions.RemoveEmptyEntries);

            if (!currentRoots.Contains(normalizedRoot, StringComparer.OrdinalIgnoreCase))
            {
                var newRootString = string.Join(";", currentRoots.Append(normalizedRoot));
                _main.UpdateAppOptions(options with { Runtime = options.Runtime with { FileSearchRoot = newRootString } });
                LoadFileSearchRoots();

                FileSearchRootInput = "";
                Raise(nameof(FileSearchRootInput));
            }

            await _main.RefreshSidebarPathsAsync();
            Status = added.IsSuccess ? "文件搜索根已登记，扫描结果已记录。" : "文件搜索根已存在，已刷新状态。";
            _main.Report(Status);
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
            _main.Report(Status);
        }
    }
}
