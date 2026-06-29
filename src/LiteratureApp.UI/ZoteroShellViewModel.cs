using System.Collections.ObjectModel;

namespace LiteratureApp.UI;

public sealed class ZoteroShellViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public ZoteroShellViewModel(MainWindowViewModel main)
    {
        _main = main;
        ToggleDeveloperToolsCommand = new AsyncCommand(ToggleDeveloperTools);
    }

    public string LibraryName { get; set; } = "My Library";
    public ObservableCollection<string> RecentItems { get; } = new();
    public ObservableCollection<string> RecentDocuments { get; } = new();
    public string StatusText => _main.Status;
    public bool ShowDeveloperTools { get; set; }
    public AsyncCommand ToggleDeveloperToolsCommand { get; }

    public Task ToggleDeveloperTools()
    {
        ShowDeveloperTools = !ShowDeveloperTools;
        Raise(nameof(ShowDeveloperTools));
        return Task.CompletedTask;
    }

    public void Refresh()
    {
        Raise(nameof(StatusText));
        Raise(nameof(LibraryName));
    }
}
