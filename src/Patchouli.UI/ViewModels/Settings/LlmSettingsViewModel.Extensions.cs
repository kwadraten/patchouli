using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Llm;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class LlmSettingsViewModel
{
    public ObservableCollection<ExtensionListItem> TranslationEngineOptions { get; } =
    [
        new("translation.llm", "LLM", ""),
        new("translation.google-free", "Google（免密）", ""),
        new("translation.deepl-free", "DeepL（免密）", ""),
        new("translation.microsoft-free", "Microsoft Edge（非官方接口）", ""),
        new("translation.tencent-free", "腾讯翻译君（非官方接口）", ""),
        new("translation.deeplx", "DeepLX", "")
    ];

    [ObservableProperty] public partial string TranslationEngineId { get; set; } = TranslationSettings.DefaultEngineId;

    public ExtensionListItem? SelectedTranslationEngine
    {
        get => TranslationEngineOptions.FirstOrDefault(item => item.Id == TranslationEngineId);
        set
        {
            if (value is not null)
            {
                TranslationEngineId = value.Id;
            }
        }
    }

    public AsyncCommand ConfigureTranslationEngineCommand { get; }
    public AsyncCommand RefreshTranslationEnginesCommand { get; }

    partial void OnTranslationEngineIdChanged(string value)
    {
        Raise(nameof(SelectedTranslationEngine));
        if (!IsWaitingForDraftSync())
        {
            _current = _current with { Translation = _current.Translation with { EngineId = value } };
            OnTranslationEdited();
        }
    }

    /// <summary>
    /// Rebuilds the selectable engine list from display descriptors only: bundled manifests, working
    /// source metadata and <see cref="ExtensionEntryMetadata"/> — never a check, activation or run.
    /// Items listed here are not validated runtime descriptors; selection and gear commands still go
    /// through the real catalog path before anything executes.
    /// </summary>
    private async Task RefreshTranslationEnginesAsync()
    {
        HostServices services = await _main.ServicesAsync();
        Dictionary<string, ExtensionPackageSnapshot> packages = services.TranslationExtensions.BundledPackageSnapshots
            .ToDictionary(package => package.Id, StringComparer.Ordinal);
        foreach (string id in services.TranslationExtensions.ListWorkingPackages())
        {
            try
            {
                ExtensionPackageSnapshot working =
                    await services.TranslationExtensions.Manager.ReadSourceAsync(id);
                // A directory with no package payload is a config/state overlay, not a working
                // source: it must not shadow the bundled snapshot's metadata.
                if (working.Sources.Count > 0 || working.BinaryFiles.Count > 0)
                {
                    packages[id] = working;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A source being edited can be temporarily unavailable; keep bundled discovery intact.
            }
        }

        foreach (ExtensionPackageSnapshot package in packages.Values)
        {
            try
            {
                ExtensionEntryMetadata metadata = ExtensionEntryMetadata.Parse(package);
                if (metadata.Kind == ExtensionKind.Translation)
                {
                    ExtensionListItem item = new(metadata.Id, metadata.Name, package.Fingerprint,
                        metadata.Kind, Description: metadata.Summary);
                    int index = -1;
                    for (int current = 0; current < TranslationEngineOptions.Count; current++)
                    {
                        if (TranslationEngineOptions[current].Id == metadata.Id)
                        {
                            index = current;
                            break;
                        }
                    }

                    if (index < 0)
                    {
                        TranslationEngineOptions.Add(item);
                    }
                    else
                    {
                        TranslationEngineOptions[index] = item;
                    }
                }
            }
            catch (ExtensionMetadataException)
            {
                // Invalid packages remain available in the source editor, not in the selectable engine list.
            }
        }

        Raise(nameof(SelectedTranslationEngine));
    }

    private async Task ConfigureTranslationEngineAsync()
    {
        await _main.Settings.SelectSectionAsync("extensions");
        ExtensionSettingsViewModel extensions = _main.Settings.ExtensionSettings;
        extensions.SelectedExtension = extensions.Extensions.FirstOrDefault(item => item.Id == TranslationEngineId);
    }
}

/// <summary>
/// One-time migration of the retired host-owned multimodal OCR page into the attributed
/// <c>ocr.multimodal</c> FSX configuration (screenshot revision): the seed is written only when
/// the component has never been configured (revision 0 with no saved values) so explicit OCR
/// provider/model, reasoning effort, concurrency and local-layout choices survive the settings
/// move. Values written here use the same keys the package's attributed schema declares.
/// </summary>
internal static class OcrMultimodalLegacySeed
{
    public const string PackageId = "ocr.multimodal";

    public static bool NeedsSeed(string extensionId, ExtensionConfigSnapshot config)
    {
        return string.Equals(extensionId, PackageId, StringComparison.Ordinal) &&
               config.Revision == 0 && config.Values.Count == 0;
    }

    /// <summary>
    /// Writes the migration snapshot (revision 1) only for a never-configured component; the
    /// returned snapshot is what the configuration form should display.
    /// </summary>
    public static async Task<ExtensionConfigSnapshot> EnsureSeededAsync(IExtensionManager manager,
        string extensionId, ExtensionConfigSnapshot config, LlmAppSettings legacy,
        OcrStorageLocations? storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(config);
        if (!NeedsSeed(extensionId, config))
        {
            return config;
        }

        ExtensionConfigSnapshot seeded = new(CreateValues(legacy, storage), config.Revision + 1);
        await manager.SaveConfigAsync(extensionId, seeded, cancellationToken);
        return seeded;
    }

    internal static Dictionary<string, string> CreateValues(LlmAppSettings legacy,
        OcrStorageLocations? storage)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        (string providerId, string model) = legacy.OcrSelection;
        string layoutModel = OcrLayoutModelIds.Normalize(legacy.OcrLayoutModel);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Same {providerId, model} JSON shape ExtensionConfigFieldViewModel writes; an
            // unresolved pair stays empty so the model field follows the provider default.
            ["model"] = providerId.Length == 0 && model.Length == 0
                ? ""
                : JsonSerializer.Serialize(new { providerId, model }),
            ["model-id"] = model,
            ["model-path"] = legacy.OcrUseLocalLayout && storage is not null
                ? storage.GetLayoutModelPath(layoutModel)
                : "",
            ["use-local-layout"] = legacy.OcrUseLocalLayout ? "true" : "false",
            ["layout-model"] = layoutModel,
            ["concurrency"] = Math.Clamp(legacy.OcrConcurrency, 1, OcrPerformanceSettings.MaxConcurrency)
                .ToString(CultureInfo.InvariantCulture),
            ["reasoning-effort"] = OcrPerformanceSettings.NormalizeReasoning(legacy.OcrReasoningEffort)
        };
    }
}
