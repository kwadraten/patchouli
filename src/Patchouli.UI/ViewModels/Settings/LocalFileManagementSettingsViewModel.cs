using System.Collections.ObjectModel;
using System.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class LocalFileManagementSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isDownloading;
    private double _downloadProgress;

    public LocalFileManagementSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        Locations = new ObservableCollection<ManagedLocationViewModel>();
        RefreshCommand = new AsyncCommand(() => LoadAsync());
        DownloadModelsCommand = new AsyncCommand(DownloadModelsAsync);
    }

    public ObservableCollection<ManagedLocationViewModel> Locations { get; }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (_isDownloading != value)
            {
                _isDownloading = value;
                Raise();
                Raise(nameof(CanDownloadModels));
            }
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (Math.Abs(_downloadProgress - value) > 0.001)
            {
                _downloadProgress = value;
                Raise();
            }
        }
    }

    public bool CanDownloadModels => !IsDownloading;

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand DownloadModelsCommand { get; }

    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Locations.Clear();
            AppServices services = await _main.ServicesAsync();
            OcrStorageLocations storage = services.OcrStorage;

            Locations.Add(new ManagedLocationViewModel(
                this,
                "NDL Koten 模型文件",
                storage.NdlKotenModelsDirectory,
                true,
                true));
            Locations.Add(new ManagedLocationViewModel(
                this,
                "MinerU OCR 临时文件",
                storage.MinerUWorkDirectory,
                false,
                true));
            Locations.Add(new ManagedLocationViewModel(
                this,
                "NDL Koten 工作临时文件",
                storage.NdlKotenWorkDirectory,
                false,
                true));

            foreach (ManagedLocationViewModel location in Locations)
            {
                await location.RefreshAsync(cancellationToken);
            }

            SetStatus("本地文件管理已刷新。");
        }
        catch (Exception exception)
        {
            SetStatus($"刷新失败：{exception.Message}");
        }
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return Task.CompletedTask;
    }

    private async Task DownloadModelsAsync()
    {
        ManagedLocationViewModel? location = Locations.FirstOrDefault(static l => l.CanDownload);
        if (location is null)
        {
            SetStatus("没有可下载的模型位置。");
            return;
        }

        await DownloadModelsAsync(location);
    }

    internal async Task DownloadModelsAsync(ManagedLocationViewModel location)
    {
        if (IsDownloading)
        {
            return;
        }

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "下载 NDL Koten 模型",
                $"将从 GitHub 下载约 {FormatBytes(NdlKotenModelFiles.Files.Sum(static f => f.ExpectedBytes))} 的模型与配置文件到：\n{location.Path}\n\n{NdlKotenModelFiles.Attribution}",
                "下载",
                confirmDanger: false));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        SetStatus("正在下载 NDL Koten 模型…");
        try
        {
            AppServices services = await _main.ServicesAsync();
            Progress<double> progress = new(value => DownloadProgress = value);
            Result result = await services.NdlKotenModelDownload.DownloadAllAsync(progress);
            if (result.IsFailure)
            {
                SetStatus($"下载失败：{result.ErrorMessage}");
                return;
            }

            await location.RefreshAsync();
            SetStatus("NDL Koten 模型下载完成。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("下载已取消。");
        }
        catch (Exception exception)
        {
            SetStatus($"下载失败：{exception.Message}");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    internal async Task ClearLocationAsync(ManagedLocationViewModel location)
    {
        if (!Directory.Exists(location.Path))
        {
            SetStatus("目录不存在或已为空。");
            return;
        }

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                $"清理 {location.Name}",
                $"将删除 {location.Path} 下的全部内容（约 {location.SizeDisplay}）。\n此操作不可恢复，缺失的模型可重新下载。",
                "清理",
                confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        try
        {
            await Task.Run(() => ClearDirectoryContents(location.Path));
            await location.RefreshAsync();
            SetStatus($"已清理 {location.Name}。");
        }
        catch (Exception exception)
        {
            SetStatus($"清理失败：{exception.Message}");
        }
    }

    internal async Task OpenLocationAsync(ManagedLocationViewModel location)
    {
        if (!Directory.Exists(location.Path))
        {
            Directory.CreateDirectory(location.Path);
        }

        Process.Start(new ProcessStartInfo { FileName = location.Path, UseShellExecute = true });
        await Task.CompletedTask;
    }

    private static void ClearDirectoryContents(string path)
    {
        foreach (string file in Directory.GetFiles(path))
        {
            File.Delete(file);
        }

        foreach (string directory in Directory.GetDirectories(path))
        {
            Directory.Delete(directory, true);
        }
    }

    internal static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        if (bytes >= gb)
        {
            return $"{bytes / (double)gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / (double)mb:0.00} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / (double)kb:0.00} KB";
        }

        return $"{bytes} B";
    }

    private void SetStatus(string message)
    {
        Status = message;
        _main.Report(message);
    }
}

public sealed class ManagedLocationViewModel : ViewModelBase
{
    private readonly LocalFileManagementSettingsViewModel _parent;
    private long _sizeBytes;
    private int _itemCount;

    public ManagedLocationViewModel(
        LocalFileManagementSettingsViewModel parent,
        string name,
        string path,
        bool canDownload,
        bool canClear)
    {
        _parent = parent;
        Name = name;
        Path = path;
        CanDownload = canDownload;
        CanClear = canClear;
        DownloadCommand = new AsyncCommand(async () => await parent.DownloadModelsAsync(this));
        ClearCommand = new AsyncCommand(async () => await parent.ClearLocationAsync(this));
        OpenCommand = new AsyncCommand(async () => await parent.OpenLocationAsync(this));
    }

    public string Name { get; }
    public string Path { get; }
    public bool CanDownload { get; }
    public bool CanClear { get; }

    public long SizeBytes
    {
        get => _sizeBytes;
        private set
        {
            if (_sizeBytes != value)
            {
                _sizeBytes = value;
                Raise();
                Raise(nameof(SizeDisplay));
            }
        }
    }

    public int ItemCount
    {
        get => _itemCount;
        private set
        {
            if (_itemCount != value)
            {
                _itemCount = value;
                Raise();
                Raise(nameof(ItemCountDisplay));
            }
        }
    }

    public string SizeDisplay => LocalFileManagementSettingsViewModel.FormatBytes(SizeBytes);

    public string ItemCountDisplay => $"{_itemCount} 项";

    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public AsyncCommand OpenCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(Path))
            {
                SizeBytes = 0;
                ItemCount = 0;
                return;
            }

            long totalSize = 0;
            int count = 0;
            Enumerate(Path, ref totalSize, ref count);
            SizeBytes = totalSize;
            ItemCount = count;
        }, cancellationToken);
    }

    private static void Enumerate(string path, ref long totalSize, ref int count)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                FileInfo info = new(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                {
                    totalSize += info.Length;
                    count++;
                }
            }
            catch
            {
                // best-effort enumeration
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) !=
                    FileAttributes.ReparsePoint)
                {
                    count++;
                }
            }
            catch
            {
                // best-effort enumeration
            }
        }
    }
}
