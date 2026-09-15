using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Core.Search;

namespace Patchouli.UI.ViewModels;

public sealed partial class BibliographyViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    [ObservableProperty] public partial string ItemType { get; set; } = "book";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string ItemId { get; set; } = "";
    [ObservableProperty] public partial string Scheme { get; set; } = "DOI";
    [ObservableProperty] public partial string IdentifierValue { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "";

    public ObservableCollection<string> RecentItems { get; } = new();
    public AsyncCommand CreateItemCommand { get; }
    public AsyncCommand AddIdentifierCommand { get; }

    partial void OnTitleChanged(string value)
    {
        CreateItemCommand.NotifyCanExecuteChanged();
    }

    partial void OnItemIdChanged(string value)
    {
        AddIdentifierCommand.NotifyCanExecuteChanged();
    }

    partial void OnIdentifierValueChanged(string value)
    {
        AddIdentifierCommand.NotifyCanExecuteChanged();
    }

    public BibliographyViewModel(MainWindowViewModel main)
    {
        _main = main;
        CreateItemCommand = new AsyncCommand(async () =>
        {
            Result<ItemMetadata> r =
                await (await _main.ServicesAsync()).Items.CreateItemAsync(ItemType, Title, Subtitle);
            if (r.IsSuccess)
            {
                ItemId = r.Value.ItemId.ToString();
                RecentItems.Add($"{r.Value.ItemId} | {r.Value.Title}");
                await _main.Shell.RefreshItemsAsync();
            }

            Output = r.IsSuccess
                ? $"Item: {r.Value.ItemId}\n{r.Value.Title}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            await _main.LogOperationAsync("create_item", Output);
        }, () => !string.IsNullOrWhiteSpace(Title));

        AddIdentifierCommand = new AsyncCommand(async () =>
        {
            Result<ItemIdentifier> r =
                await (await _main.ServicesAsync()).Items.AddIdentifierAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),
                    Scheme, IdentifierValue, null);
            Output = r.IsSuccess
                ? $"Identifier: {r.Value.Scheme} {r.Value.Value}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
        }, () => !string.IsNullOrWhiteSpace(ItemId) && !string.IsNullOrWhiteSpace(IdentifierValue));
    }
}
