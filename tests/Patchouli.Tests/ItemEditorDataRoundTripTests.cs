using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Csl;
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
                CustomFieldsJson = "{\"archive\":\"Archive A\",\"legacy-key\":\"keep me\"}"
            };
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(request);
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await main.EditItemByIdAsync(created.Value.ItemId.ToString());
            main.ItemEditor.IdentifierSchemeShortcuts.Select(shortcut => shortcut.Scheme).Should()
                .Equal("isbn", "doi", "url");
            main.ItemEditor.ExtraCslRows.Should().Contain(row => row.Key == "archive" && row.Value == "Archive A");
            main.ItemEditor.ExtraCslRows.Should().Contain(row =>
                row.Key == "legacy-key" && row.Label == "legacy-key" && row.Value == "keep me");
            main.ItemEditor.MoreFields.Should().Contain(field => field.Key == "ContainerTitleShort");
            main.ItemEditor.Fields.Should().Contain(field => field.Key == "Volume");

            main.ItemEditor.SelectedExtraCslVariable =
                main.ItemEditor.AvailableExtraCslVariables.Single(option => option.Key == "jurisdiction");
            await main.ItemEditor.AddExtraCslRowCommand.ExecuteAsync();
            main.ItemEditor.ExtraCslRows.Single(row => row.Key == "jurisdiction").Value = "CN";
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
            saved.Value.Volume.Should().Be("1");
            saved.Value.Status.Should().Be("inpress");
            saved.Value.Note.Should().Be("Imported note");
            saved.Value.CollectionsJson.Should().Be("[\"Imported collection\"]");
            saved.Value.Dates.Select(date => date.Role).Should().Contain(ItemDateRoles.Accessed)
                .And.Contain(ItemDateRoles.OriginalDate);

            using JsonDocument customFields = JsonDocument.Parse(saved.Value.CustomFieldsJson);
            customFields.RootElement.GetProperty("archive").GetString().Should().Be("Archive A");
            customFields.RootElement.GetProperty("jurisdiction").GetString().Should().Be("CN");
            customFields.RootElement.GetProperty("legacy-key").GetString().Should().Be("keep me");
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

    [Fact]
    public async Task Url_projection_field_round_trips_through_identifiers_and_csl_mapping()
    {
        string path = Path.Combine(Path.GetTempPath(), $"item-editor-projection-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            AppServices services = await main.ServicesAsync();

            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("webpage", "Projection test"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await main.EditItemByIdAsync(created.Value.ItemId.ToString());
            ItemFieldDescriptor urlField =
                main.ItemEditor.Fields.Single(field => field.Key == "Identifier:url");
            urlField.Label.Should().Be("链接 URL");
            urlField.Value.Should().BeEmpty();

            urlField.Value = "https://example.org/page";
            main.ItemEditor.Identifiers.Should().Contain(row =>
                row.IsPending && row.Scheme == BuiltInIdentifierSchemes.URL);

            await main.ItemEditor.SaveCommand.ExecuteAsync();

            Result<IReadOnlyList<ItemIdentifier>> identifiers =
                await services.Items.ListIdentifiersAsync(created.Value.ItemId);
            identifiers.IsSuccess.Should().BeTrue(identifiers.ErrorMessage);
            identifiers.Value.Should().Contain(identifier =>
                identifier.Scheme == BuiltInIdentifierSchemes.URL && identifier.Value == "https://example.org/page");

            Result<ItemMetadata> saved = await services.Items.GetItemAsync(created.Value.ItemId);
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
            Result<CslMappedItem> mapped = await new CslItemMapper().MapAsync(saved.Value);
            mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
            mapped.Value.Variables["URL"].Should().Be("https://example.org/page");

            urlField.Value = "";
            main.ItemEditor.Identifiers.Should().NotContain(row =>
                string.Equals(row.Scheme, BuiltInIdentifierSchemes.URL, StringComparison.OrdinalIgnoreCase));
            await main.ItemEditor.SaveCommand.ExecuteAsync();

            identifiers = await services.Items.ListIdentifiersAsync(created.Value.ItemId);
            identifiers.Value.Should().NotContain(identifier =>
                identifier.Scheme == BuiltInIdentifierSchemes.URL);
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

    [Fact]
    public async Task Extra_csl_backed_field_syncs_with_rows_both_ways_and_round_trips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"item-editor-extracsl-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            AppServices services = await main.ServicesAsync();

            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("manuscript", "Projection test")
                {
                    CustomFieldsJson = "{}"
                });
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await main.EditItemByIdAsync(created.Value.ItemId.ToString());
            ItemFieldDescriptor archiveField =
                main.ItemEditor.Fields.Single(field => field.Key == "ExtraCsl:archive");
            archiveField.Type.Should().Be(CslItemTypeProfileService.ExtraCslBackedFieldType);
            archiveField.Value.Should().BeEmpty();
            main.ItemEditor.ExtraCslRows.Should().ContainSingle(row =>
                row.Key == "archive" && row.IsProjection && !row.CanRemove);

            // Row edit → form field sync.
            main.ItemEditor.ExtraCslRows.Single(row => row.Key == "archive").Value = "Archive B";
            archiveField.Value.Should().Be("Archive B");

            // Form field edit → row sync.
            archiveField.Value = "Archive C";
            main.ItemEditor.ExtraCslRows.Single(row => row.Key == "archive").Value.Should().Be("Archive C");

            // The projected row is a single shared editor for the same CSL variable.
            archiveField.Value = "";
            main.ItemEditor.ExtraCslRows.Should().ContainSingle(row =>
                row.Key == "archive" && row.Value == "" && row.IsProjection && !row.CanRemove);

            // Setting the form field again updates that same row rather than creating a duplicate.
            archiveField.Value = "Archive D";
            main.ItemEditor.ExtraCslRows.Should().ContainSingle(row =>
                row.Key == "archive" && row.Value == "Archive D");

            await main.ItemEditor.SaveCommand.ExecuteAsync();

            Result<ItemMetadata> saved = await services.Items.GetItemAsync(created.Value.ItemId);
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
            using (JsonDocument customFields = JsonDocument.Parse(saved.Value.CustomFieldsJson))
            {
                customFields.RootElement.GetProperty("archive").GetString().Should().Be("Archive D");
            }

            // Reload: the form field is repopulated from the stored row.
            await main.ItemEditor.LoadAsync(created.Value.ItemId.ToString());
            main.ItemEditor.Fields.Single(field => field.Key == "ExtraCsl:archive").Value.Should().Be("Archive D");
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

    [Fact]
    public async Task Newly_exposed_core_fields_round_trip_through_save_and_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"item-editor-corefields-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            AppServices services = await main.ServicesAsync();

            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("classic", "Core field test"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await main.EditItemByIdAsync(created.Value.ItemId.ToString());
            main.ItemEditor.Fields.Single(field => field.Key == "TitleShort").Value = "Short classic";
            main.ItemEditor.Fields.Single(field => field.Key == "OriginalDate").Value = "1901";
            main.ItemEditor.MoreFields.Single(field => field.Key == "Status").Value = "draft";

            await main.ItemEditor.SaveCommand.ExecuteAsync();

            Result<ItemMetadata> saved = await services.Items.GetItemAsync(created.Value.ItemId);
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
            saved.Value.TitleShort.Should().Be("Short classic");
            saved.Value.Status.Should().Be("draft");
            saved.Value.Dates.Should().Contain(date =>
                date.Role == ItemDateRoles.OriginalDate && date.Literal == "1901");
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

    [Fact]
    public async Task Creator_role_dropdown_follows_the_item_type_profile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"item-editor-roles-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            AppServices services = await main.ServicesAsync();

            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("motion_picture", "Role options test"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            CreatorItemViewModel creator = main.ItemEditor.Creators.Single();
            creator.AvailableRoles.Select(option => option.Key).Take(3).Should()
                .Equal(ItemCreatorRoles.Director, ItemCreatorRoles.Producer, ItemCreatorRoles.ScriptWriter);
            creator.AvailableRoles.Select(option => option.Label).Take(3).Should()
                .Equal("导演", "制片人", "编剧");
            // The creator's current role stays selectable even though the profile does not list it.
            creator.AvailableRoles.Should().Contain(option => option.Key == ItemCreatorRoles.Author);
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
