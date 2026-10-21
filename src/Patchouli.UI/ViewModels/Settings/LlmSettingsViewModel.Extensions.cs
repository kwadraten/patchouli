using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
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
        new("translation.deeplx", "DeepLX", "")
    ];

    [ObservableProperty] public partial string TranslationEngineId { get; set; } = "translation.llm";

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

    private async Task RefreshTranslationEnginesAsync()
    {
        HostServices services = await _main.ServicesAsync();
        await services.TranslationExtensions.InitializeAsync();
        TranslationEngineOptions.Clear();
        foreach (ExtensionGenerationInfo item in services.TranslationExtensions.Manager.List()
                     .Where(item => item.IsActive))
        {
            await using IExtensionLease? lease = await services.TranslationExtensions.Manager.AcquireAsync(item.Id);
            if (lease is not null && lease.Descriptor.Kind == ExtensionKind.Translation)
            {
                TranslationEngineOptions.Add(new ExtensionListItem(item.Id, lease.Descriptor.Name, item.Fingerprint,
                    lease.Descriptor.Kind, Description: lease.Descriptor.Description));
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
