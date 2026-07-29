using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

public sealed class ItemEditorDataRoundTripTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Editor_preserves_hidden_biblatex_fields_nonissued_dates_and_collections()
    {
        string path = Path.Combine(Path.GetTempPath(), $"item-editor-roundtrip-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            AppServices services = await main.ServicesAsync();

            CreateItemRequest request = BiblatexMappedItemMerge.ToCreateRequest(CreateImportedBook()) with
            {
                CollectionsJson = "[\"Imported collection\"]",
                CustomFieldsJson = "{\"archive\":\"Archive A\"}"
            };
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(request);
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await main.EditItemByIdAsync(created.Value.ItemId.ToString());
            main.ItemEditor.IdentifierSchemeShortcuts.Select(shortcut => shortcut.Scheme).Should().Equal("isbn");
            main.ItemEditor.Fields.Single(field => field.Key == "ExtraCsl").Value =
                "{\"archive\":\"Archive A\",\"jurisdiction\":\"CN\"}";
            main.ItemEditor.Title = "Edited imported book";

            await main.ItemEditor.SaveCommand.ExecuteAsync();

            Result<ItemMetadata> saved = await services.Items.GetItemAsync(created.Value.ItemId);
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
            saved.Value.Title.Should().Be("Edited imported book");
            saved.Value.TitleShort.Should().Be("Short title");
            saved.Value.ContainerTitleShort.Should().Be("Short container");
            saved.Value.CollectionTitle.Should().Be("Imported series");
            saved.Value.Edition.Should().Be("Second");
            saved.Value.ChapterNumber.Should().Be("3");
            saved.Value.Status.Should().Be("inpress");
            saved.Value.Note.Should().Be("Imported note");
            saved.Value.CollectionsJson.Should().Be("[\"Imported collection\"]");
            saved.Value.Dates.Select(date => date.Role).Should().Contain(ItemDateRoles.Accessed)
                .And.Contain(ItemDateRoles.OriginalDate);

            using JsonDocument customFields = JsonDocument.Parse(saved.Value.CustomFieldsJson);
            customFields.RootElement.GetProperty("archive").GetString().Should().Be("Archive A");
            customFields.RootElement.GetProperty("jurisdiction").GetString().Should().Be("CN");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private static BiblatexMappedItem CreateImportedBook()
    {
        return new BiblatexMappedItem(
            "book",
            "book",
            "Imported book",
            null,
            "Short title",
            [new ItemCreatorInput(ItemCreatorRoles.Author, "Doe", "Jane")],
            [
                new ItemDateInput(ItemDateRoles.Issued, "[[2024]]"),
                new ItemDateInput(ItemDateRoles.Accessed, Literal: "2025-01-02"),
                new ItemDateInput(ItemDateRoles.OriginalDate, "[[1901]]")
            ],
            [new ItemIdentifierInput(BuiltInIdentifierSchemes.ISBN, "9780306406157")],
            null,
            "Short container",
            "Imported series",
            "Imported Press",
            "Shanghai",
            "Second",
            "monograph",
            "42",
            "3",
            "1",
            "v2",
            "4",
            "10-20",
            "zh",
            "inpress",
            "Imported note",
            "Imported abstract",
            ["imported"],
            null,
            "imported-book",
            "book");
    }
}
