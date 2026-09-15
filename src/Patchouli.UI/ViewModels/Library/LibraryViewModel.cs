using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Core.Search;

namespace Patchouli.UI.ViewModels;

public sealed partial class LibraryViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    [ObservableProperty] public partial string DisplayName { get; set; } = "My Library";
    [ObservableProperty] public partial string RenameTo { get; set; } = "";
    [ObservableProperty] public partial string Details { get; set; } = "";

    public AsyncCommand CreateCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand RenameCommand { get; }

    partial void OnDisplayNameChanged(string value)
    {
        CreateCommand.NotifyCanExecuteChanged();
    }

    partial void OnRenameToChanged(string value)
    {
        RenameCommand.NotifyCanExecuteChanged();
    }

    public LibraryViewModel(MainWindowViewModel main)
    {
        _main = main;
        CreateCommand = new AsyncCommand(async () =>
        {
            Result<LibraryMetadata> r = await (await _main.ServicesAsync()).Library.CreateLibraryAsync(DisplayName);
            Details = r.IsSuccess
                ? $"{r.Value.DisplayName}\n{r.Value.LibraryId}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            _main.Report(Details);
            await _main.LogOperationAsync("create_library", Details);
        }, () => !string.IsNullOrWhiteSpace(DisplayName));

        RefreshCommand = new AsyncCommand(async () =>
        {
            Result<LibraryMetadata> r = await (await _main.ServicesAsync()).Library.GetCurrentLibraryAsync();
            Details = r.IsSuccess
                ? $"{r.Value.DisplayName}\n{r.Value.LibraryId}\nSchema {r.Value.SchemaVersion}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        });

        RenameCommand = new AsyncCommand(async () =>
        {
            Result<LibraryMetadata> r = await (await _main.ServicesAsync()).Library.RenameLibraryAsync(RenameTo);
            Details = r.IsSuccess
                ? $"Renamed: {r.Value.DisplayName}\n{r.Value.LibraryId}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            await _main.LogOperationAsync("rename_library", Details);
        }, () => !string.IsNullOrWhiteSpace(RenameTo));
    }
}
