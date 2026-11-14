using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.ManagedFiles;
using Patchouli.Infrastructure.Rendering;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class LocalFileManagementSettingsViewModel : SettingsSectionViewModelBase
{
    // Confirmation text for declaration-group downloads through the shared managed-file
    // dialog: generic managed-file wording — a row may merge FSX and plugin declarations.
    private static readonly ManagedFileDialogText DownloadText = new(
        "下载文件",
        "下载并修复文件",
        "「{0}」缺少必需的文件，需要下载后才能使用。",
        "「{0}」的必需文件缺失或校验不匹配，需要重新下载后才能使用。",
        "书库已切换，未执行文件下载；请在新书库中重新触发该操作。",
        "文件下载已取消。",
        "已取消文件下载。",
        OcrManagedFileDialogs.DesktopManagedFileInteraction.DeclinedCode,
        OcrManagedFileDialogs.DesktopManagedFileInteraction.CancelledCode);

    private readonly MainWindowViewModel _main;

    // One admitted download per canonical directory: the slot registers before any prepare or
    // prompt await, so a second click for the same row can never reach a second confirmation.
    // Other directories stay downloadable — the shared queue serializes the real work.
    private readonly Dictionary<string, Task> _admittedDownloads =
        new(ManagedFileService.PathComparer());

    private const long BytesPerMiB = 1024L * 1024;

    private long _loadGeneration;
    private long _renderCacheGeneration;
    private bool _isSyncingRenderCache;

    public LocalFileManagementSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        Locations = new ObservableCollection<ManagedLocationViewModel>();
        RefreshCommand = new AsyncCommand(() => LoadAsync());
        DownloadModelsCommand = new AsyncCommand(DownloadModelsAsync);
        ClearPageRenderCacheCommand = new AsyncCommand(ClearPageRenderCacheAsync);
        OpenPageRenderCacheCommand = new AsyncCommand(OpenPageRenderCacheAsync);
        SyncRenderCacheSettings(_main.AppOptions.RenderCache);
    }

    [ExcludeFromDerivedGeneration] public ObservableCollection<ManagedLocationViewModel> Locations { get; }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand DownloadModelsCommand { get; }
    public AsyncCommand ClearPageRenderCacheCommand { get; }
    public AsyncCommand OpenPageRenderCacheCommand { get; }

    // Mirrors the Host clamp; SettingsPage.axaml uses the same 32–16384 MiB spinner bounds.
    private const double MinRenderCacheLimitMiB = PageRenderService.MinDiskCacheByteLimit / (double)BytesPerMiB;
    private const double MaxRenderCacheLimitMiB = PageRenderService.MaxDiskCacheByteLimit / (double)BytesPerMiB;

    /// <summary>Whether the host exposes a page-render cache; the card is hidden otherwise.</summary>
    [ObservableProperty]
    public partial bool HasPageRenderCache { get; private set; }

    [ObservableProperty] public partial string PageRenderCacheDirectory { get; private set; } = string.Empty;

    [ObservableProperty] public partial long PageRenderCacheBytes { get; private set; }

    [ObservableProperty] public partial int PageRenderCacheFileCount { get; private set; }

    [ObservableProperty] public partial string PageRenderCacheSizeDisplay { get; private set; } = "0 B";

    [ObservableProperty] public partial string PageRenderCacheFileCountDisplay { get; private set; } = "0 个文件";

    /// <summary>Disk limit in MiB; edits persist through the settings store's debounced writer.</summary>
    [ObservableProperty]
    public partial double RenderCacheLimitMiB { get; set; }

    /// <summary>PNG quality (1–100) for newly written renders; existing files are kept.</summary>
    [ObservableProperty]
    public partial double RenderCachePngQuality { get; set; }

    partial void OnRenderCacheLimitMiBChanged(double value)
    {
        QueueRenderCacheSave();
    }

    partial void OnRenderCachePngQualityChanged(double value)
    {
        QueueRenderCacheSave();
    }

    /// <summary>
    /// Device-level setting written through the settings store's debounced writer: rapid spinner
    /// edits coalesce into one commit, and the committed "RenderCache" category reaches the Host
    /// (limit + sweep, quality) off the dispatcher via its settings observer.
    /// </summary>
    private void QueueRenderCacheSave()
    {
        if (_isSyncingRenderCache || double.IsNaN(RenderCacheLimitMiB) || double.IsNaN(RenderCachePngQuality))
        {
            return;
        }

        RenderCacheAppSettings requested = new RenderCacheAppSettings(
            (long)Math.Round(Math.Clamp(RenderCacheLimitMiB, MinRenderCacheLimitMiB, MaxRenderCacheLimitMiB)) *
            BytesPerMiB,
            (int)Math.Round(Math.Clamp(RenderCachePngQuality, 1, 100))).Normalize();
        if (requested == _main.AppOptions.RenderCache.Normalize())
        {
            return;
        }

        _main.UpdateAppOptionsDeferred(current => current with { RenderCache = requested }, "RenderCache");
    }

    private void SyncRenderCacheSettings(RenderCacheAppSettings settings)
    {
        RenderCacheAppSettings normalized = settings.Normalize();
        _isSyncingRenderCache = true;
        try
        {
            RenderCacheLimitMiB = Math.Round(normalized.DiskLimitBytes / (double)BytesPerMiB);
            RenderCachePngQuality = normalized.PngQuality;
        }
        finally
        {
            _isSyncingRenderCache = false;
        }
    }

    /// <summary>
    /// Reads the page-render cache row off the dispatcher (the usage scan is synchronous I/O) and
    /// applies it only while it is still the newest read for the same library context.
    /// </summary>
    internal async Task RefreshPageRenderCacheAsync(HostServices services, int libraryGeneration,
        CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _renderCacheGeneration);
        Result<PageRenderCacheDescription> described = await Task.Run(
            () => services.ManagedFileInventory.DescribePageRenderCacheAsync(cancellationToken),
            cancellationToken);
        if (generation != Interlocked.Read(ref _renderCacheGeneration) ||
            !_main.IsCurrentLibraryContext(services, libraryGeneration) ||
            cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (described.IsFailure)
        {
            HasPageRenderCache = false;
            return;
        }

        PageRenderCacheDescription cache = described.Value;
        HasPageRenderCache = true;
        PageRenderCacheDirectory = cache.Directory;
        PageRenderCacheBytes = cache.TotalBytes;
        PageRenderCacheFileCount = cache.FileCount;
        PageRenderCacheSizeDisplay = FormatBytes(cache.TotalBytes);
        PageRenderCacheFileCountDisplay = $"{cache.FileCount} 个文件";
    }

    internal async Task ClearPageRenderCacheAsync()
    {
        HostServices services = await _main.ServicesAsync();
        int libraryGeneration = _main.LibraryGeneration;
        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，请刷新后重试清理。");
            return;
        }

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "清理页面渲染缓存",
                $"将删除全部页面渲染缓存（约 {PageRenderCacheSizeDisplay}），页面会在查看时重新渲染。",
                "清理",
                confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，未执行清理。");
            return;
        }

        try
        {
            // The Host API serializes the delete with the LRU sweeper; never a UI-side recursive delete.
            Result cleared = await Task.Run(() =>
                services.ManagedFileInventory.ClearPageRenderCacheAsync(services.LifetimeToken));
            if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            await RefreshPageRenderCacheAsync(services, libraryGeneration);
            if (_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                SetStatus(cleared.IsSuccess ? "已清理页面渲染缓存。" : $"清理失败：{cleared.ErrorMessage}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                SetStatus($"清理失败：{exception.Message}");
            }
        }
    }

    private Task OpenPageRenderCacheAsync()
    {
        if (string.IsNullOrEmpty(PageRenderCacheDirectory))
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(PageRenderCacheDirectory);
        Process.Start(new ProcessStartInfo { FileName = PageRenderCacheDirectory, UseShellExecute = true });
        return Task.CompletedTask;
    }

    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            HostServices services = await _main.ServicesAsync();
            int libraryGeneration = _main.LibraryGeneration;
            OcrStorageLocations storage = services.OcrStorage;

            // Declaration reads and disk stats are real I/O — they stay off the dispatcher even
            // when callers or handlers would otherwise complete inline.
            IReadOnlyList<ManagedInventoryGroup> groups = await Task.Run(
                () => services.ManagedFileInventory.DescribeAsync(cancellationToken),
                cancellationToken);
            if (generation != Volatile.Read(ref _loadGeneration) ||
                !_main.IsCurrentLibraryContext(services, libraryGeneration) ||
                cancellationToken.IsCancellationRequested)
            {
                // A newer load or a library switch owns the collection — never apply stale data.
                return;
            }

            Locations.Clear();
            // Managed-file groups come from the unified inventory: actual FSX ManagedFile
            // declarations of every known extension package plus plugin [[files]] declarations —
            // arbitrary future owners appear through the same mechanism without a UI switch.
            foreach (ManagedInventoryGroup group in groups)
            {
                Locations.Add(new ManagedLocationViewModel(this, group.Name, group.Directory,
                    true, true, DownloadKindFor(group.DirectoryKind), group));
            }

            Locations.Add(new ManagedLocationViewModel(
                this,
                "MinerU OCR 临时文件",
                storage.MinerUWorkDirectory,
                false,
                true));
            Locations.Add(new ManagedLocationViewModel(
                this,
                "NDL古典籍OCR-Lite 工作临时文件",
                storage.NdlKotenWorkDirectory,
                false,
                true));
            Locations.Add(new ManagedLocationViewModel(
                this,
                "NDLOCR-Lite 工作临时文件",
                storage.NdlLiteWorkDirectory,
                false,
                true));
            Locations.Add(new ManagedLocationViewModel(
                this,
                "RapidOCR 工作临时文件",
                storage.RapidOcrWorkDirectory,
                false,
                true));

            foreach (ManagedLocationViewModel location in Locations)
            {
                await location.RefreshAsync(cancellationToken);
            }

            SyncRenderCacheSettings(_main.AppOptions.RenderCache);
            await RefreshPageRenderCacheAsync(services, libraryGeneration, cancellationToken);

            if (generation == Volatile.Read(ref _loadGeneration) &&
                _main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                SetStatus("本地文件管理已刷新。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus($"刷新失败：{exception.Message}");
        }
    }

    /// <summary>Legacy download-kind tag retained for rows/tests; the download itself is generic.</summary>
    private static ModelDownloadKind DownloadKindFor(string? directoryKind)
    {
        return directoryKind?.Trim().ToLowerInvariant() switch
        {
            "doclayout-yolo-models" or "doclayout-yolo" => ModelDownloadKind.DocLayoutYolo,
            "pp-doclayout-v3-models" or "pp-doclayout-v3" => ModelDownloadKind.PpDocLayoutV3,
            "ndl-koten-models" or "ndl-koten" => ModelDownloadKind.NdlKoten,
            "ndlocr-lite-models" or "ndlocr-lite" or "ndl-lite" => ModelDownloadKind.NdlLite,
            "rapidocr-models" or "rapidocr" => ModelDownloadKind.RapidOcr,
            _ => ModelDownloadKind.None
        };
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
            SetStatus("没有可下载的文件位置。");
            return;
        }

        await DownloadModelsAsync(location);
    }

    internal async Task DownloadModelsAsync(ManagedLocationViewModel location)
    {
        if (location.Group is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int libraryGeneration = _main.LibraryGeneration;
        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，请刷新后重试下载。");
            return;
        }

        string key = Path.GetFullPath(location.Path);
        if (ActiveDownload(location.Path) is not null)
        {
            SetStatus($"{location.Name}的下载已在进行中。");
            return;
        }

        // The slot registers before any await so a second click for this row can never reach a
        // second confirmation; only this directory is deduplicated, other rows stay live. It
        // completes only in the outer finally — after stats refresh — so a same-row caller
        // joins the whole operation, never an intermediate point.
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _admittedDownloads[key] = admitted.Task;
        try
        {
            // Re-read the group's declarations at click time so the confirmation and the
            // overwrite flags describe the real on-disk state, not the load-time snapshot. The
            // re-read is declaration+disk I/O and stays off the dispatcher; a group that
            // disappeared yields no download — never a stale fallback plan.
            Result<ManagedInventoryDownload> prepared = await Task.Run(() =>
                services.ManagedFileInventory.PrepareDownloadAsync(location.Path,
                    CancellationToken.None));
            if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            if (prepared.IsFailure)
            {
                // Prepare failures are actionable (e.g. conflicting duplicate declarations) —
                // surface the real message instead of a vague refresh hint.
                SetStatus($"下载失败：{prepared.ErrorMessage}");
                return;
            }

            ManagedInventoryDownload download = prepared.Value;
            if (download.Plan.IsEmpty)
            {
                SetStatus($"{location.Name}的文件已完整，无需下载。");
                return;
            }

            SetStatus($"正在下载 {location.Name}…");
            // The shared confirmation owns the prompt, the queue registration and the modal
            // runner — the single progress and per-file status surface.
            Result result = await ManagedFileDialogs.ConfirmAndDownloadAsync(_main, services,
                libraryGeneration, download.Plan, download.Download, DownloadText,
                CancellationToken.None);

            // The library may have switched while the dialog ran; never stamp stale status or
            // refresh rows belonging to another context.
            if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            if (result.IsSuccess)
            {
                await location.RefreshAsync();
                if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
                {
                    return;
                }
            }

            SetStatus(result.IsSuccess
                ? $"{location.Name}下载完成。"
                : $"下载未完成：{result.ErrorMessage}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                SetStatus($"下载失败：{exception.Message}");
            }
        }
        finally
        {
            _admittedDownloads.Remove(key);
            admitted.TrySetResult();
        }
    }

    private Task? ActiveDownload(string path)
    {
        string key = Path.GetFullPath(path);
        if (!_admittedDownloads.TryGetValue(key, out Task? running))
        {
            return null;
        }

        if (!running.IsCompleted)
        {
            return running;
        }

        _admittedDownloads.Remove(key);
        return null;
    }

    internal async Task ClearLocationAsync(ManagedLocationViewModel location)
    {
        if (!Directory.Exists(location.Path))
        {
            SetStatus("目录不存在或已为空。");
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int libraryGeneration = _main.LibraryGeneration;
        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，请刷新后重试清理。");
            return;
        }

        if (location.Group is { } declaredGroup)
        {
            await ClearDeclaredGroupAsync(services, libraryGeneration, location, declaredGroup);
            return;
        }

        // Legacy managed caches (temp/work rows) keep the whole-directory clear UX — they are
        // application-owned directories, never arbitrary user folders.
        ConfirmDialogResult? legacyChoice = await _main.Dialogs
            .ShowDialogAsync<ConfirmDialogResult>(
                new ConfirmDialogViewModel(
                    $"清理 {location.Name}",
                    $"将删除 {location.Path} 下的全部内容（约 {location.SizeDisplay}）。\n" +
                    "此操作不可恢复，缺失的模型可重新下载。",
                    "清理",
                    confirmDanger: true));
        if (legacyChoice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，未执行清理。");
            return;
        }

        try
        {
            await Task.Run(() => ClearDirectoryContents(location.Path));

            await location.RefreshAsync();
            SetStatus($"已清理 {location.Name}。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus($"清理失败：{exception.Message}");
        }
    }

    /// <summary>
    /// Clears a declaration-driven group: the only eligible set is the group's declared file
    /// targets that currently exist on disk — the confirmation lists them and the delete touches
    /// exactly those files. The directory itself, unrelated user files, subdirectories and
    /// anything reachable through a reparse point are never touched. The deletable set is
    /// re-derived after the prompt so a stale snapshot can never authorize a delete.
    /// </summary>
    private async Task ClearDeclaredGroupAsync(HostServices services, int libraryGeneration,
        ManagedLocationViewModel location, ManagedInventoryGroup declaredGroup)
    {
        IReadOnlyList<DeclaredTarget> targets =
            await Task.Run(() => DeclaredExistingTargets(declaredGroup));
        if (targets.Count == 0)
        {
            SetStatus($"{location.Name}没有可清理的声明文件。");
            await location.RefreshAsync();
            return;
        }

        StringBuilder message = new(
            $"将删除 {location.Name} 的 {targets.Count} 个声明文件" +
            $"（约 {FormatBytes(targets.Sum(target => target.Bytes))}）：\n");
        foreach (DeclaredTarget target in targets.Take(8))
        {
            message.Append($"\n• {target.Label}");
        }

        if (targets.Count > 8)
        {
            message.Append($"\n• …等共 {targets.Count} 个");
        }

        message.Append("\n\n此操作不可恢复，缺失的文件可重新下载；目录中的其他内容保持不变。");
        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel($"清理 {location.Name}", message.ToString(), "清理",
                confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，未执行清理。");
            return;
        }

        // Re-derive the deletable set after confirmation: a stale snapshot must never delete,
        // and only the freshly declared+verified targets are removed.
        ManagedInventoryGroup? fresh =
            (await Task.Run(() => services.ManagedFileInventory.DescribeAsync(CancellationToken.None)))
            .FirstOrDefault(candidate => string.Equals(candidate.Directory, location.Path,
                ManagedFileService.PathComparison()));
        if (fresh is null || !_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("目录的声明信息已变化，未执行清理。");
            return;
        }

        IReadOnlyList<DeclaredTarget> currentTargets = await Task.Run(() => DeclaredExistingTargets(fresh));
        // A changed declaration cannot broaden an earlier confirmation, nor authorize deleting
        // a newer user file that appeared at the same path while the prompt was open.
        targets = targets.Where(confirmed => currentTargets.Any(current =>
                string.Equals(current.Path, confirmed.Path, ManagedFileService.PathComparison()) &&
                current.Bytes == confirmed.Bytes && current.WriteUtcTicks == confirmed.WriteUtcTicks &&
                current.Sha256 == confirmed.Sha256))
            .ToArray();
        try
        {
            int deleted = await Task.Run(() => DeleteDeclaredTargets(targets, services.LifetimeToken));
            await location.RefreshAsync();
            SetStatus($"已清理 {location.Name}（删除 {deleted} 个声明文件）。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus($"清理失败：{exception.Message}");
        }
    }

    /// <summary>
    /// The exact declared file targets of a group that exist on disk right now — the only set a
    /// declaration-driven clear may ever delete. Directories and reparse points are excluded so
    /// a junctioned target is never followed and a subfolder is never removed.
    /// </summary>
    internal static IReadOnlyList<DeclaredTarget> DeclaredExistingTargets(ManagedInventoryGroup group)
    {
        List<DeclaredTarget> targets = [];
        HashSet<string> seen = new(ManagedFileService.PathComparer());
        foreach (ManagedFileDownload file in group.Files)
        {
            string destination = Path.GetFullPath(file.DestinationPath);
            if (!seen.Add(destination) || !File.Exists(destination))
            {
                continue;
            }

            try
            {
                FileInfo info = new(destination);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    HasReparseParent(info.Directory))
                {
                    continue;
                }

                using FileStream stream = File.OpenRead(destination);
                targets.Add(new DeclaredTarget(file.RelativePath, destination, info.Length)
                {
                    WriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = Convert.ToHexString(SHA256.HashData(stream))
                });
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        return targets;
    }

    /// <summary>
    /// Deletes exactly the listed declared targets — each re-checked at delete time so it is
    /// still a regular file, never a directory, never a reparse point. Entries that vanished or
    /// became inaccessible between check and delete are skipped; the set is never broadened.
    /// </summary>
    internal static int DeleteDeclaredTargets(IReadOnlyList<DeclaredTarget> targets,
        CancellationToken cancellationToken = default)
    {
        int deleted = 0;
        foreach (DeclaredTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo info = new(target.Path);
                FileAttributes attributes = File.GetAttributes(target.Path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    HasReparseParent(info.Directory) || info.Length != target.Bytes ||
                    (target.WriteUtcTicks is { } expectedWrite && info.LastWriteTimeUtc.Ticks != expectedWrite))
                {
                    continue;
                }

                if (target.Sha256 is { } expectedHash)
                {
                    using FileStream stream = File.OpenRead(target.Path);
                    if (Convert.ToHexString(SHA256.HashData(stream)) != expectedHash)
                    {
                        continue;
                    }
                }

                File.Delete(target.Path);
                deleted++;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        return deleted;
    }

    /// <summary>One declared file target eligible for clearing: label for the prompt, canonical
    /// path, and byte size for the summary.</summary>
    internal readonly record struct DeclaredTarget(string Label, string Path, long Bytes)
    {
        public long? WriteUtcTicks { get; init; }
        public string? Sha256 { get; init; }
    }

    private static bool HasReparseParent(DirectoryInfo? parent)
    {
        for (; parent is not null; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
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

public enum ModelDownloadKind
{
    None,
    DocLayoutYolo,
    PpDocLayoutV3,
    NdlKoten,
    NdlLite,
    RapidOcr
}

public sealed partial class ManagedLocationViewModel : ViewModelBase
{
    private readonly LocalFileManagementSettingsViewModel _parent;

    internal ManagedLocationViewModel(
        LocalFileManagementSettingsViewModel parent,
        string name,
        string path,
        bool canDownload,
        bool canClear,
        ModelDownloadKind downloadKind = ModelDownloadKind.None,
        ManagedInventoryGroup? group = null)
    {
        _parent = parent;
        Name = name;
        Path = path;
        CanDownload = canDownload;
        CanClear = canClear;
        DownloadKind = downloadKind;
        Group = group;
        DownloadCommand = new AsyncCommand(async () => await parent.DownloadModelsAsync(this));
        ClearCommand = new AsyncCommand(async () => await parent.ClearLocationAsync(this));
        OpenCommand = new AsyncCommand(async () => await parent.OpenLocationAsync(this));
    }

    public string Name { get; }
    public string Path { get; }
    public bool CanDownload { get; }
    public bool CanClear { get; }
    public ModelDownloadKind DownloadKind { get; }

    /// <summary>The declared managed-file group backing this row; null for plain temp/work directories.</summary>
    internal ManagedInventoryGroup? Group { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeDisplay))]
    public partial long SizeBytes { get; private set; }

    [ObservableProperty] public partial int ItemCount { get; private set; }

    [ExcludeFromDerivedGeneration]
    public string SizeDisplay => LocalFileManagementSettingsViewModel.FormatBytes(SizeBytes);

    public string ItemCountDisplay => $"{ItemCount} 项";

    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public AsyncCommand OpenCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(Path) || (File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
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
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (string file in Directory.EnumerateFiles(path, "*", options))
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
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(path, "*", options))
        {
            try
            {
                if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) !=
                    FileAttributes.ReparsePoint)
                {
                    count++;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }
    }
}
