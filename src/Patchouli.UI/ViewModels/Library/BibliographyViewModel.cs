using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.UI.ViewModels;

public sealed class BibliographyViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string ItemType { get; set; } = "book";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Scheme { get; set; } = "DOI";
    public string IdentifierValue { get; set; } = "";
    public string Output { get; set; } = "";
    public ObservableCollection<string> RecentItems { get; } = new();
    public AsyncCommand CreateItemCommand { get; }
    public AsyncCommand AddIdentifierCommand { get; }

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
                Raise(nameof(ItemId));
            }

            Output = r.IsSuccess
                ? $"Item: {r.Value.ItemId}\n{r.Value.Title}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("create_item", Output);
        });
        AddIdentifierCommand = new AsyncCommand(async () =>
        {
            Result<ItemIdentifier> r =
                await (await _main.ServicesAsync()).Items.AddIdentifierAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),
                    Scheme, IdentifierValue, null);
            Output = r.IsSuccess
                ? $"Identifier: {r.Value.Scheme} {r.Value.Value}"
                : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
    }
}
