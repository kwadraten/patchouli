using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;

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
                packages[id] = await services.TranslationExtensions.Manager.ReadSourceAsync(id);
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
