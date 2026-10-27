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
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Translation;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class LocalFileManagementSettingsViewModel : SettingsSectionViewModelBase
{
    // Cosmetic ordering only — keeps the familiar row order for the known managed-directory
    // aliases; unknown kinds and fallback directories sort after, by path. This is display
    // metadata, never an engine-id switch: rows and downloads come from FSX declarations.
    private static readonly string[] PreferredDirectoryKindOrder =
    [
        "doclayout-yolo-models", "pp-doclayout-v3-models",
        "ndl-koten-models", "ndlocr-lite-models", "rapidocr-models"
    ];

    private readonly MainWindowViewModel _main;
    private long _loadGeneration;

    public LocalFileManagementSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        Locations = new ObservableCollection<ManagedLocationViewModel>();
        RefreshCommand = new AsyncCommand(() => LoadAsync());
        DownloadModelsCommand = new AsyncCommand(DownloadModelsAsync);
    }

    [ExcludeFromDerivedGeneration] public ObservableCollection<ManagedLocationViewModel> Locations { get; }

    [ObservableProperty] public partial bool IsDownloading { get; private set; }

    [ObservableProperty] public partial double DownloadProgress { get; private set; }

    public bool CanDownloadModels => !IsDownloading;

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand DownloadModelsCommand { get; }

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
            IReadOnlyList<ManagedLocationFileGroup> groups = await Task.Run(
                () => LoadManagedFileGroupsAsync(services, cancellationToken), cancellationToken);
            if (generation != Volatile.Read(ref _loadGeneration) ||
                !_main.IsCurrentLibraryContext(services, libraryGeneration) ||
                cancellationToken.IsCancellationRequested)
            {
                // A newer load or a library switch owns the collection — never apply stale data.
                return;
            }

            Locations.Clear();
            // Model-file groups come from the actual FSX ManagedFile declarations of every
            // known extension package — arbitrary future packages appear through the same
            // mechanism without a per-engine switch.
            foreach (ManagedLocationFileGroup group in groups)
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

    /// <summary>
    ///     Every declared managed-file directory group, merged across extensions by canonical
    ///     destination directory so two packages sharing a model location produce one row.
    /// </summary>
    private async Task<IReadOnlyList<ManagedLocationFileGroup>> LoadManagedFileGroupsAsync(
        HostServices services, CancellationToken cancellationToken)
    {
        OcrStorageLocations storage = services.OcrStorage;
        Dictionary<string, ManagedLocationFileGroup> merged = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        foreach (string extensionId in CandidateExtensionIds(services))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtensionConfigurationDescription description;
            try
            {
                description = await services.TranslationExtensions
                    .GetConfigurationDescriptionAsync(extensionId, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            if (!description.HasTrustedSchema)
            {
                continue;
            }

            ConfigFieldDescriptor[] managedFields = description.Fields
                .Where(field => field.ManagedFiles is { Count: > 0 })
                .ToArray();
            if (managedFields.Length == 0)
            {
                continue;
            }

            ExtensionConfigSnapshot config;
            try
            {
                config = await services.TranslationExtensions.Manager
                    .ReadConfigAsync(extensionId, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                config = EmptyConfig();
            }

            IReadOnlyList<ExtensionManagedFileGroup> groups;
            ExtensionConfigSnapshot resolvedConfig;
            try
            {
                resolvedConfig = services.ExtensionFiles.ResolveDefaults(extensionId, managedFields,
                    config);
                groups = await services.ExtensionFiles
                    .DescribeGroupsAsync(extensionId, managedFields, config, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            foreach (ExtensionManagedFileGroup group in groups)
            {
                if (!merged.TryGetValue(group.Directory, out ManagedLocationFileGroup? target))
                {
                    string name = storage.ModelDirectoryDisplayNameFor(group.DirectoryKind) ??
                                  $"{description.Name} {group.FieldLabel}";
                    target = new ManagedLocationFileGroup(group.ExtensionId, group.DirectoryKind,
                        group.Directory, name);
                    merged[group.Directory] = target;
                    order.Add(group.Directory);
                }

                target.Merge(group, resolvedConfig);
            }
        }

        return order.OrderBy(directory =>
            {
                ManagedLocationFileGroup group = merged[directory];
                int rank = group.DirectoryKind is { } kind
                    ? Array.FindIndex(PreferredDirectoryKindOrder,
                        candidate => string.Equals(candidate, kind, StringComparison.OrdinalIgnoreCase))
                    : -1;
                return (rank < 0 ? PreferredDirectoryKindOrder.Length : rank, directory);
            }, Comparer<(int, string)>.Create(static (left, right) =>
            {
                int rank = left.Item1.CompareTo(right.Item1);
                return rank != 0 ? rank : StringComparer.OrdinalIgnoreCase.Compare(left.Item2, right.Item2);
            }))
            .Select(directory => merged[directory])
            .ToArray();
    }

    private static IEnumerable<string> CandidateExtensionIds(HostServices services)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in BuiltInOcrExtensions.PackageIds
                     .Concat(BuiltInPdfTextLayerExtensions.Packages.Select(package => package.Id))
                     .Concat(BuiltInTranslationExtensions.Packages.Select(package => package.Id))
                     .Concat(BuiltInMetadataExtensions.Packages.Select(package => package.Id))
                     .Concat(services.TranslationExtensions.ListWorkingPackages())
                     .Concat(services.TranslationExtensions.Manager.List().Select(info => info.Id)))
        {
            if (seen.Add(id))
            {
                yield return id;
            }
        }
    }

    private static ExtensionConfigSnapshot EmptyConfig()
    {
        return new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0);
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
            SetStatus("没有可下载的模型位置。");
            return;
        }

        await DownloadModelsAsync(location);
    }

    internal async Task DownloadModelsAsync(ManagedLocationViewModel location)
    {
        if (IsDownloading || (location.DownloadKind == ModelDownloadKind.None && location.Group is null))
        {
            return;
        }

        string title = location.DownloadKind switch
        {
            ModelDownloadKind.DocLayoutYolo => "下载 DocLayout-YOLO 版面模型",
            ModelDownloadKind.PpDocLayoutV3 => "下载 PP-DocLayoutV3 版面模型",
            ModelDownloadKind.NdlKoten => "下载 NDL古典籍OCR-Lite 模型",
            ModelDownloadKind.NdlLite => "下载 NDLOCR-Lite 模型",
            ModelDownloadKind.RapidOcr => "下载 RapidOCR 模型",
            _ => $"下载 {location.Name}"
        };

        HostServices services = await _main.ServicesAsync();
        int libraryGeneration = _main.LibraryGeneration;
        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，请刷新后重试下载。");
            return;
        }

        // Re-read the group's missing/invalid set at click time so the confirmation and the
        // overwrite flags describe the real on-disk state, not the load-time snapshot. The
        // re-read is declaration+disk I/O and stays off the dispatcher.
        ManagedLocationFileGroup? group = location.Group is null
            ? null
            : (await Task.Run(() => LoadManagedFileGroupsAsync(services, CancellationToken.None)))
            .FirstOrDefault(candidate => string.Equals(candidate.Directory, location.Path,
                StringComparison.OrdinalIgnoreCase)) ?? location.Group;
        IReadOnlyList<ExtensionManagedFileDownload> missing = group?.Missing ?? [];
        if (group is not null && missing.Count == 0)
        {
            SetStatus($"{location.Name}的文件已完整，无需下载。");
            return;
        }

        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，未执行下载。");
            return;
        }

        long totalBytes = group?.TotalExpectedBytes ?? 0;
        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                title,
                $"将从网络下载约 {FormatBytes(totalBytes)} 的模型与配置文件到：\n{location.Path}",
                "下载",
                confirmDanger: false));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        // The prompt may have been open across a library switch — the captured host instance
        // must still be the live one before any network/file work starts.
        if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("书库已切换，未执行下载。");
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        SetStatus($"正在下载 {location.Name}…");
        try
        {
            // Progress<T> captured this dispatcher; the download itself always runs on a
            // background task — a modal runner's inline fallback must never put network/disk
            // I/O on the UI thread.
            Progress<double> progress = new(value => DownloadProgress = value);
            Result result = group is null
                ? Result.Failure(AppErrorCodes.ValidationFailed,
                    $"没有可下载的声明文件：{location.Name}。")
                : await Task.Run(() => services.ExtensionFiles.DownloadAsync(
                    new ExtensionManagedFilesPlan(group.ExtensionId, location.Name,
                        group.ResolvedConfig ?? EmptyConfig(), missing), progress));
            if (result.IsFailure)
            {
                SetStatus($"下载失败：{result.ErrorMessage}");
                return;
            }

            if (!_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            await location.RefreshAsync();
            SetStatus($"{location.Name}下载完成。");
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
        ManagedLocationViewModel location, ManagedLocationFileGroup declaredGroup)
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
        ManagedLocationFileGroup? fresh =
            (await Task.Run(() => LoadManagedFileGroupsAsync(services, CancellationToken.None)))
            .FirstOrDefault(candidate => string.Equals(candidate.Directory, location.Path,
                StringComparison.OrdinalIgnoreCase));
        if (fresh is null || !_main.IsCurrentLibraryContext(services, libraryGeneration))
        {
            SetStatus("目录的声明信息已变化，未执行清理。");
            return;
        }

        IReadOnlyList<DeclaredTarget> currentTargets = await Task.Run(() => DeclaredExistingTargets(fresh));
        // A changed declaration cannot broaden an earlier confirmation, nor authorize deleting
        // a newer user file that appeared at the same path while the prompt was open.
        targets = targets.Where(confirmed => currentTargets.Any(current =>
                string.Equals(current.Path, confirmed.Path, StringComparison.OrdinalIgnoreCase) &&
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
    internal static IReadOnlyList<DeclaredTarget> DeclaredExistingTargets(ManagedLocationFileGroup group)
    {
        List<DeclaredTarget> targets = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionManagedFileDownload file in group.Files)
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

/// <summary>
/// One merged managed-file directory group backing a Local Files row: arbitrary extension
/// declarations contributing files into the same canonical directory. Not an engine registry —
/// it is built purely from FSX-declared groups.
/// </summary>
internal sealed class ManagedLocationFileGroup(
    string extensionId,
    string? directoryKind,
    string directory,
    string name)
{
    private readonly List<ExtensionManagedFileDownload> _files = [];
    private readonly List<ExtensionManagedFileDownload> _missing = [];

    public string ExtensionId => extensionId;
    public string? DirectoryKind => directoryKind;
    public string Directory => directory;
    public string Name => name;
    public IReadOnlyList<ExtensionManagedFileDownload> Files => _files;
    public IReadOnlyList<ExtensionManagedFileDownload> Missing => _missing;
    public ExtensionConfigSnapshot? ResolvedConfig { get; private set; }
    public long TotalExpectedBytes => _missing.Sum(file => file.ExpectedBytes ?? 0);

    public void Merge(ExtensionManagedFileGroup group, ExtensionConfigSnapshot resolvedConfig)
    {
        ResolvedConfig ??= resolvedConfig;
        foreach (ExtensionManagedFileDownload file in group.Files)
        {
            if (_files.All(existing => !string.Equals(existing.DestinationPath, file.DestinationPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _files.Add(file);
            }
        }

        foreach (ExtensionManagedFileDownload file in group.Missing)
        {
            if (_missing.All(existing => !string.Equals(existing.DestinationPath, file.DestinationPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _missing.Add(file);
            }
        }
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
        ManagedLocationFileGroup? group = null)
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

    /// <summary>The FSX-declared file group backing this row; null for plain temp/work directories.</summary>
    internal ManagedLocationFileGroup? Group { get; }

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
