using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Host.Composition;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrEngineMenuEntryViewModel> DocumentOcrEngineMenuEntries { get; } = new();

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrEngineMenuEntryViewModel> PageOcrEngineMenuEntries { get; } = new();

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrEngineMenuEntryViewModel> RegionOcrEngineMenuEntries { get; } = new();

    public async Task RefreshOcrEngineMenuAsync()
    {
        HostServices services = await ServicesAsync();
        Populate(DocumentOcrEngineMenuEntries, OcrScope.Document);
        Populate(PageOcrEngineMenuEntries, OcrScope.Page);
        Populate(RegionOcrEngineMenuEntries, OcrScope.Region);

        void Populate(ObservableCollection<OcrEngineMenuEntryViewModel> entries, OcrScope scope)
        {
            entries.Clear();
            foreach (OcrEngineCapability capability in services.OcrAdapters.ListCapabilities())
            {
                entries.Add(new OcrEngineMenuEntryViewModel(this, scope, capability.EngineId, capability.DisplayName));
            }
        }
    }

    internal void RefreshOcrEngineMenuChecks()
    {
        foreach (ObservableCollection<OcrEngineMenuEntryViewModel> entries in new[]
                     { DocumentOcrEngineMenuEntries, PageOcrEngineMenuEntries, RegionOcrEngineMenuEntries })
        {
            foreach (OcrEngineMenuEntryViewModel entry in entries)
            {
                entry.RefreshSelection();
            }
        }
    }
}

public sealed class OcrEngineMenuEntryViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly OcrScope _scope;
    private readonly string _engineId;

    public OcrEngineMenuEntryViewModel(MainWindowViewModel main, OcrScope scope, string engineId, string header)
    {
        _main = main;
        _scope = scope;
        _engineId = engineId;
        Header = header;
        SelectCommand = new AsyncCommand(SelectAsync);
    }

    public string Header { get; }
    public AsyncCommand SelectCommand { get; }
    public bool IsSelected => _main.AppOptions.OcrEngines.EngineFor(_scope) == _engineId;

    internal void RefreshSelection()
    {
        Raise(nameof(IsSelected));
    }

    private async Task SelectAsync()
    {
        OcrEnginesAppSettings current = _main.AppOptions.OcrEngines;
        OcrEnginesAppSettings updated = _scope switch
        {
            OcrScope.Document => current with { DocumentOcrEngine = _engineId },
            OcrScope.Page => current with { PageOcrEngine = _engineId },
            _ => current with { RegionOcrEngine = _engineId }
        };
        await _main.SaveOcrEngineSettingsAsync(updated);
        _main.RefreshOcrEngineMenuChecks();
    }
}
