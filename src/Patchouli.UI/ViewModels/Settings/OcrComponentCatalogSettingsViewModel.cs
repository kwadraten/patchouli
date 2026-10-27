using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Views;
using Patchouli.UI.Views.Settings;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>How a listed engine/provider row maps to an extension generation and package source.</summary>
public enum OcrComponentStatus
{
    /// <summary>Built-in component (MinerU): no extension package, own configuration dialog.</summary>
    BuiltIn,

    /// <summary>An active generation exists and its fingerprint matches the displayed source.</summary>
    Active,

    /// <summary>An active generation exists but the working/display source fingerprint differs.</summary>
    ActiveSourceChanged,

    /// <summary>The package exists (bundled, working or custom) but was never activated.</summary>
    Unactivated,

    /// <summary>The package source cannot be read or its entry metadata is invalid.</summary>
    SourceInvalid,

    /// <summary>The registry definition has no extension package bound (non-MinerU).</summary>
    MissingPackage
}

/// <summary>
/// Registry-backed component catalog for the screenshot revision: one list per
/// <see cref="ExtensionKind"/> (OCR image engines or independent PDF text-layer providers).
/// Every read is declarative metadata — <see cref="IOcrEngineCatalog"/> listings, generation
/// table rows and package sources — never an activation, check, native session or download.
/// </summary>
public sealed partial class OcrComponentCatalogSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private long _loadGeneration;

    public OcrComponentCatalogSettingsViewModel(MainWindowViewModel main, ExtensionKind kind)
    {
        ArgumentNullException.ThrowIfNull(main);
        if (kind is not (ExtensionKind.Ocr or ExtensionKind.PdfTextLayer))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "The component catalog only lists OCR engines or PDF text-layer providers.");
        }

        _main = main;
        Kind = kind;
        Title = kind == ExtensionKind.Ocr ? "OCR 引擎" : "文本层提取算法";
        Description = kind == ExtensionKind.Ocr
            ? "预设编排从这里选择图像 OCR 引擎；齿轮打开引擎扩展的配置与源码，MinerU 为内置引擎。"
            : "独立的 PDF 文本层提取提供器，与图像 OCR 引擎解耦；齿轮打开提供器扩展的配置与源码。";
        RefreshCommand = new AsyncCommand(() => LoadAsync());
    }

    public ExtensionKind Kind { get; }
    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<OcrComponentRowViewModel> Components { get; } = [];
    public AsyncCommand RefreshCommand { get; }

    /// <summary>Test seam: invoked instead of showing the real MinerU dialog window.</summary>
    internal Func<MinerUConfigurationDialogViewModel, Task>? MinerUDialogShower { get; set; }

    /// <summary>Test seam: invoked instead of showing the real generic editor window.</summary>
    internal Func<ExtensionSettingsViewModel, Task>? ComponentEditorShower { get; set; }

    /// <summary>Test seam: resolves the owner window for the generic editor dialog.</summary>
    internal Func<Window?>? OwnerWindowResolver { get; set; }

    public override bool SupportsEditing => false;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _loadGeneration);
        Status = $"正在读取{Title}…";
        try
        {
            HostServices services = await _main.ServicesAsync();
            int libraryGeneration = _main.LibraryGeneration;
            List<OcrComponentRowViewModel> rows =
                await Task.Run(() => BuildRowsAsync(services, cancellationToken), cancellationToken);
            if (generation != Volatile.Read(ref _loadGeneration) ||
                !_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                return;
            }

            Components.Clear();
            foreach (OcrComponentRowViewModel row in rows)
            {
                Components.Add(row);
            }

            LastError = null;
            Status = $"{Title}已加载（{Components.Count} 项）。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "ocr-component-catalog", nameof(LoadAsync));
            LastError = exception.Message;
            Status = $"{Title}加载失败：{exception.Message}";
        }
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return LoadAsync();
    }

    internal Task ConfigureAsync(OcrComponentRowViewModel row)
    {
        if (row.IsBuiltIn)
        {
            return OpenMinerUDialogAsync();
        }

        if (row.ExtensionId is { } extensionId)
        {
            return OpenComponentEditorAsync(extensionId, false);
        }

        Status = $"{row.Name} 没有关联的扩展包，无法打开配置。";
        return Task.CompletedTask;
    }

    internal Task ShowSourceAsync(OcrComponentRowViewModel row)
    {
        if (row.ExtensionId is { } extensionId)
        {
            return OpenComponentEditorAsync(extensionId, true);
        }

        Status = $"{row.Name} 没有扩展源码。";
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the real MinerU dialog as an owned window (the same pattern the generic editor
    /// uses — it needs no <see cref="Services.IDialogService"/> registration, which belongs to
    /// root composition).
    /// </summary>
    private async Task OpenMinerUDialogAsync()
    {
        MinerUConfigurationDialogViewModel dialog = new(_main);
        await dialog.LoadAsync();
        if (MinerUDialogShower is { } shower)
        {
            await shower(dialog);
            return;
        }

        Window? owner = OwnerWindowResolver?.Invoke() ?? ResolveOwnerWindow();
        if (owner is null)
        {
            Status = "找不到可用的窗口来打开 MinerU 配置。";
            return;
        }

        MinerUConfigurationDialog window = new(dialog);
        await window.ShowDialog(owner);
    }

    /// <summary>
    /// Routes every non-MinerU component — activated, never-activated, custom or invalid-source —
    /// to the shared attributed-FSX configuration editor. The editor itself decides between a
    /// trusted form and a source-only view; no component has a code-only dead end here.
    /// </summary>
    private async Task OpenComponentEditorAsync(string extensionId, bool openSource)
    {
        ExtensionSettingsViewModel editor = _main.Settings.CreateExtensionEditor();
        await editor.OpenAsync(extensionId, openSource);
        if (ComponentEditorShower is { } shower)
        {
            try
            {
                await shower(editor);
            }
            finally
            {
                editor.Dispose();
            }

            return;
        }

        Window? owner = OwnerWindowResolver?.Invoke() ?? ResolveOwnerWindow();
        if (owner is null)
        {
            Status = "找不到可用的窗口来打开扩展配置。";
            editor.Dispose();
            return;
        }

        try
        {
            ExtensionConfigurationDialog dialog = new(editor);
            await dialog.ShowDialog(owner);
        }
        finally
        {
            editor.Dispose();
        }
    }

    private static Window? ResolveOwnerWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    private async Task<List<OcrComponentRowViewModel>> BuildRowsAsync(HostServices services,
        CancellationToken cancellationToken)
    {
        IExtensionManager manager = services.TranslationExtensions.Manager;
        IReadOnlyList<ExtensionPackageSnapshot> bundled = services.TranslationExtensions.BundledPackageSnapshots;
        IReadOnlyList<ExtensionGenerationInfo> generations = manager.List();
        HashSet<string> mappedPackages = new(StringComparer.Ordinal);
        List<OcrComponentRowViewModel> rows = [];
        if (Kind == ExtensionKind.Ocr)
        {
            bool? minerUCredential = null;
            foreach (OcrEngineDefinition definition in services.OcrEngineRuntime.ListDefinitions())
            {
                string? packageId = services.OcrEngineRuntime.GetExtensionId(definition.EngineId);
                if (packageId is null)
                {
                    if (IsBuiltInMinerU(definition))
                    {
                        minerUCredential ??= await HasMinerUCredentialAsync(services, cancellationToken);
                        rows.Add(BuildMinerURow(definition, minerUCredential.Value));
                    }
                    else
                    {
                        rows.Add(new OcrComponentRowViewModel(this, definition.EngineId,
                            definition.DisplayName, definition.Notes, "",
                            "无扩展包", false, false, null,
                            OcrComponentStatus.MissingPackage, false));
                    }

                    continue;
                }

                mappedPackages.Add(packageId);
                rows.Add(await BuildPackageRowAsync(definition.EngineId, definition.DisplayName,
                    definition.Notes, packageId, manager, bundled, generations,
                    bundled.All(package => package.Id != packageId), cancellationToken));
            }
        }
        else
        {
            foreach (PdfTextLayerDefinition provider in services.OcrEngineRuntime.ListTextLayerDefinitions())
            {
                if (string.Equals(provider.ProviderId, PdfTextLayerProviderIds.Disabled,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? packageId = services.OcrEngineRuntime.GetTextLayerExtensionId(provider.ProviderId);
                if (packageId is null)
                {
                    rows.Add(new OcrComponentRowViewModel(this, provider.ProviderId,
                        provider.DisplayName, provider.Notes ?? "", "",
                        "无扩展包", false, false, null,
                        OcrComponentStatus.MissingPackage, false));
                    continue;
                }

                mappedPackages.Add(packageId);
                rows.Add(await BuildPackageRowAsync(provider.ProviderId, provider.DisplayName,
                    provider.Notes, packageId, manager, bundled, generations,
                    bundled.All(package => package.Id != packageId), cancellationToken));
            }
        }

        // Custom packages of the same declared kind that no registered component maps to are still
        // listed so their configuration and FSX source stay reachable from the catalog.
        List<ExtensionPackageSnapshot> unmapped = [];
        unmapped.AddRange(bundled.Where(package => !mappedPackages.Contains(package.Id)));
        foreach (string id in services.TranslationExtensions.ListWorkingPackages()
                     .Where(id => !mappedPackages.Contains(id) && unmapped.All(p => p.Id != id)))
        {
            try
            {
                unmapped.Add(await manager.ReadSourceAsync(id, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or DirectoryNotFoundException or FileNotFoundException)
            {
                // An unreadable working package may still hold an ACTIVE generation; check the
                // lease before dropping it so the row keeps its real kind.
                await AddBrokenActiveRowAsync(id, null, manager, generations, rows,
                    cancellationToken);
            }
        }

        foreach (ExtensionPackageSnapshot package in unmapped)
        {
            try
            {
                ExtensionEntryMetadata metadata = ExtensionEntryMetadata.Parse(package);
                if (metadata.Kind != Kind)
                {
                    continue;
                }

                rows.Add(await BuildPackageRowAsync(metadata.Id, metadata.Name, metadata.Summary,
                    package.Id, manager, bundled, generations, true, cancellationToken));
            }
            catch (ExtensionMetadataException)
            {
                await AddBrokenActiveRowAsync(package.Id, package, manager, generations, rows,
                    cancellationToken);
            }
        }

        return rows;
    }

    /// <summary>
    /// A custom package whose working metadata can no longer be parsed but which still has an
    /// ACTIVE generation stays in its real catalog through the active lease's descriptor kind —
    /// acquiring a lease on an already-loaded generation is not activation. Packages with no
    /// active generation cannot be classified honestly and are left to the generic extension
    /// editor as the repair path instead of being miscategorized here.
    /// </summary>
    private async Task AddBrokenActiveRowAsync(string packageId, ExtensionPackageSnapshot? package,
        IExtensionManager manager, IReadOnlyList<ExtensionGenerationInfo> generations,
        List<OcrComponentRowViewModel> rows, CancellationToken cancellationToken)
    {
        if (!generations.Any(info => info.Id == packageId && info.IsActive))
        {
            return;
        }

        try
        {
            await using IExtensionLease? lease =
                await manager.AcquireAsync(packageId, cancellationToken);
            if (lease is null || lease.Descriptor.Kind != Kind)
            {
                return;
            }

            rows.Add(await BuildPackageRowAsync(lease.Descriptor.Id, lease.Descriptor.Name,
                lease.Descriptor.Description, packageId, manager,
                package is null ? [] : [package], generations, true,
                cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UnexpectedExceptions.Sink.Report(exception, "ocr-component-catalog",
                nameof(AddBrokenActiveRowAsync));
        }
    }

    private async Task<OcrComponentRowViewModel> BuildPackageRowAsync(string componentId,
        string displayName, string? notes, string packageId, IExtensionManager manager,
        IReadOnlyList<ExtensionPackageSnapshot> bundled,
        IReadOnlyList<ExtensionGenerationInfo> generations, bool isCustom,
        CancellationToken cancellationToken)
    {
        ExtensionPackageSnapshot source;
        try
        {
            source = await ExtensionSettingsViewModel.ReadDisplaySourceAsync(manager, bundled, packageId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OcrComponentRowViewModel(this, componentId, displayName,
                $"源码读取失败：{exception.Message}", "", "源码无效", false,
                true, packageId, OcrComponentStatus.SourceInvalid, isCustom);
        }

        ExtensionGenerationInfo? active = generations.FirstOrDefault(info =>
            info.Id == packageId && info.IsActive);
        ExtensionEntryMetadata? metadata = null;
        string? metadataError = null;
        try
        {
            metadata = ExtensionEntryMetadata.Parse(source);
        }
        catch (ExtensionMetadataException error)
        {
            metadataError = error.Message;
        }

        bool generationFailed = active?.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == ExtensionDiagnosticSeverity.Error) == true;
        OcrComponentStatus status = metadata is null || generationFailed
            ? OcrComponentStatus.SourceInvalid
            : active is null
                ? OcrComponentStatus.Unactivated
                : string.Equals(active.Fingerprint, source.Fingerprint, StringComparison.Ordinal)
                    ? OcrComponentStatus.Active
                    : OcrComponentStatus.ActiveSourceChanged;
        string statusText = status switch
        {
            OcrComponentStatus.Active => "已激活",
            OcrComponentStatus.ActiveSourceChanged => "已激活 · 工作源码不同",
            OcrComponentStatus.Unactivated => "未激活",
            _ => "源码无效"
        };
        if (isCustom)
        {
            statusText += " · 自定义包";
        }

        string summary = !string.IsNullOrWhiteSpace(notes) ? notes! : metadata?.Summary ?? "";
        if (metadataError is not null)
        {
            summary = string.IsNullOrWhiteSpace(summary) ? metadataError : summary + " · " + metadataError;
        }

        return new OcrComponentRowViewModel(this, componentId, metadata?.Name ?? displayName, summary,
            metadata is null ? "" : $"v{metadata.Version}", statusText, false,
            true, packageId, status, isCustom);
    }

    private static bool IsBuiltInMinerU(OcrEngineDefinition definition)
    {
        return string.Equals(definition.EngineId, OcrEngineIds.MinerU, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasMinerUCredentialAsync(HostServices services,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ProviderCredentialMetadata>> credentials =
            await services.Credentials.ListAsync(cancellationToken);
        return credentials.IsSuccess && credentials.Value.Any(item =>
            item.ProviderId == ProviderIds.MinerU && item.Status == ProviderCredentialStatus.Active);
    }

    private OcrComponentRowViewModel BuildMinerURow(OcrEngineDefinition definition, bool hasCredential)
    {
        string status = hasCredential ? "内置引擎 · 凭据已配置" : "内置引擎 · 凭据未配置";
        return new OcrComponentRowViewModel(this, definition.EngineId, definition.DisplayName,
            definition.Notes, "", status, true, false, null,
            OcrComponentStatus.BuiltIn, false);
    }
}

/// <summary>
/// One catalog row. <see cref="ConfigureCommand"/> routes to the MinerU-specific dialog for the
/// built-in engine and to the shared attributed-FSX configuration editor for every other
/// component; <see cref="ShowSourceCommand"/> opens the same editor with the source pane expanded.
/// </summary>
public sealed class OcrComponentRowViewModel
{
    internal OcrComponentRowViewModel(OcrComponentCatalogSettingsViewModel owner, string id, string name,
        string summary, string versionText, string statusText, bool isBuiltIn, bool canShowSource,
        string? extensionId, OcrComponentStatus status, bool isCustom)
    {
        Id = id;
        Name = name;
        Summary = summary;
        VersionText = versionText;
        StatusText = statusText;
        IsBuiltIn = isBuiltIn;
        CanShowSource = canShowSource;
        ExtensionId = extensionId;
        Status = status;
        IsCustom = isCustom;
        ConfigureCommand = new AsyncCommand(() => owner.ConfigureAsync(this));
        ShowSourceCommand = new AsyncCommand(() => owner.ShowSourceAsync(this));
    }

    public string Id { get; }
    public string Name { get; }
    public string Summary { get; }
    public string VersionText { get; }
    public string StatusText { get; }
    public bool IsBuiltIn { get; }
    public bool CanShowSource { get; }
    public string? ExtensionId { get; }
    public OcrComponentStatus Status { get; }
    public bool IsCustom { get; }
    public AsyncCommand ConfigureCommand { get; }
    public AsyncCommand ShowSourceCommand { get; }
}
