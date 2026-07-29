using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Editor;

namespace Patchouli.Tests;

/// <summary>Loaded-item identifier/file operations are staged until 保存题录 and dropped by 放弃更改.</summary>
public sealed class ItemEditorStagingTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Item_type_picker_is_populated_from_the_shared_profile_service()
    {
        string path = TempDbPath("item-types");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            await main.CreateItemMenuCommand.ExecuteAsync();
            Result<IReadOnlyList<CslItemTypeProfile>> profiles =
                await services.ItemTypeProfiles.ListProfilesAsync();

            profiles.IsSuccess.Should().BeTrue(profiles.ErrorMessage);
            main.ItemEditor.AvailableItemTypes.Select(option => (option.Key, option.DisplayName)).Should()
                .BeEquivalentTo(
                    profiles.Value.Select(profile => (profile.ItemType, profile.DisplayName)),
                    options => options.WithoutStrictOrdering());
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Identifier_add_on_loaded_item_is_staged_until_save_and_dropped_by_discard()
    {
        string path = TempDbPath("add");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("book", "Staging add"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            main.ItemEditor.IdentifierScheme = BuiltInIdentifierSchemes.DOI;
            main.ItemEditor.IdentifierValue = "10.1000/staged-add";
            await main.ItemEditor.AddIdentifierCommand.ExecuteAsync();

            main.ItemEditor.Identifiers.Should().Contain(row =>
                row.IsPending && row.Scheme == BuiltInIdentifierSchemes.DOI);
            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should()
                .NotContain(identifier => identifier.Value == "10.1000/staged-add");

            await main.ItemEditor.DiscardCommand.ExecuteAsync();

            main.ItemEditor.Identifiers.Should().NotContain(row =>
                row.Scheme == BuiltInIdentifierSchemes.DOI);
            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should().BeEmpty();

            main.ItemEditor.IdentifierScheme = BuiltInIdentifierSchemes.DOI;
            main.ItemEditor.IdentifierValue = "10.1000/staged-add";
            await main.ItemEditor.AddIdentifierCommand.ExecuteAsync();
            await main.ItemEditor.SaveCommand.ExecuteAsync();

            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should().Contain(identifier =>
                identifier.Scheme == BuiltInIdentifierSchemes.DOI &&
                identifier.Value == "10.1000/staged-add");
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Identifier_remove_on_loaded_item_is_staged_until_save_and_restored_by_discard()
    {
        string path = TempDbPath("remove");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("book", "Staging remove")
                {
                    Identifiers = [new ItemIdentifierInput(BuiltInIdentifierSchemes.ISBN, "9780306406157")]
                });
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            IdentifierItemViewModel row = main.ItemEditor.Identifiers.Single(candidate => !candidate.IsPending);
            await row.RemoveCommand.ExecuteAsync();

            main.ItemEditor.Identifiers.Should().BeEmpty();
            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should()
                .Contain(identifier => identifier.Scheme == BuiltInIdentifierSchemes.ISBN);

            await main.ItemEditor.DiscardCommand.ExecuteAsync();

            main.ItemEditor.Identifiers.Should().Contain(candidate =>
                !candidate.IsPending && candidate.Scheme == BuiltInIdentifierSchemes.ISBN);

            row = main.ItemEditor.Identifiers.Single(candidate => !candidate.IsPending);
            await row.RemoveCommand.ExecuteAsync();
            await main.ItemEditor.SaveCommand.ExecuteAsync();

            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should().BeEmpty();
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task File_registration_on_loaded_item_is_staged_until_save()
    {
        string path = TempDbPath("file");
        string file = Path.Combine(Path.GetTempPath(), $"patchouli-staged-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(file, "staged registration");
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("book", "Staging file"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            main.ItemEditor.FilePath = file;
            await main.ItemEditor.RegisterFileCommand.ExecuteAsync();

            main.ItemEditor.LinkedFiles.Should().Contain(row => row.IsPendingRegistration);
            main.ItemEditor.Status.Should().Contain("暂存");
            (await services.Documents.ListDocumentInstancesForItemAsync(created.Value.ItemId)).Value.Should()
                .BeEmpty();

            await main.ItemEditor.SaveCommand.ExecuteAsync();

            (await services.Documents.ListDocumentInstancesForItemAsync(created.Value.ItemId)).Value.Should()
                .HaveCount(1);
            main.ItemEditor.LinkedFiles.Should().NotContain(row => row.IsPendingRegistration);
        }
        finally
        {
            CleanupDb(path);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task File_registration_on_new_item_is_staged_and_attached_on_first_save()
    {
        string path = TempDbPath("new-file");
        string file = Path.Combine(Path.GetTempPath(), $"patchouli-new-staged-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(file, "new item staged registration");
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            await main.CreateItemMenuCommand.ExecuteAsync();
            main.ItemEditor.Title = "New item with file";

            main.ItemEditor.FilePath = file;
            await main.ItemEditor.RegisterFileCommand.ExecuteAsync();

            main.ItemEditor.ItemIdText.Should().BeEmpty();
            main.ItemEditor.LinkedFiles.Should().ContainSingle(row => row.IsPendingRegistration);

            await main.ItemEditor.SaveCommand.ExecuteAsync();

            ItemId itemId = ItemId.Parse(main.ItemEditor.ItemIdText);
            (await services.Documents.ListDocumentInstancesForItemAsync(itemId)).Value.Should().ContainSingle();
            main.ItemEditor.LinkedFiles.Should().NotContain(row => row.IsPendingRegistration);
        }
        finally
        {
            CleanupDb(path);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task Linked_file_remove_is_staged_until_save_and_restored_by_discard()
    {
        string path = TempDbPath("remove-file");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("book", "Staging file removal"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            Result<DocumentInstance> attached =
                await services.Documents.AttachDocumentInstanceAsync(
                    created.Value.ItemId,
                    null,
                    DocumentInstanceType.PrimaryScan,
                    "scan.pdf",
                    true);
            attached.IsSuccess.Should().BeTrue(attached.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            await main.ItemEditor.LinkedFiles.Single().RemoveCommand.ExecuteAsync();

            main.ItemEditor.LinkedFiles.Should().BeEmpty();
            (await services.Documents.ListDocumentInstancesForItemAsync(created.Value.ItemId)).Value
                .Should().ContainSingle();

            await main.ItemEditor.DiscardCommand.ExecuteAsync();
            main.ItemEditor.LinkedFiles.Should().ContainSingle();

            await main.ItemEditor.LinkedFiles.Single().RemoveCommand.ExecuteAsync();
            await main.ItemEditor.SaveCommand.ExecuteAsync();
            (await services.Documents.ListDocumentInstancesForItemAsync(created.Value.ItemId)).Value
                .Should().BeEmpty();
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Failed_staged_identifier_operation_is_reported_and_kept_for_retry()
    {
        string path = TempDbPath("failed-identifier");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("book", "Staging failure")
                {
                    Identifiers = [new ItemIdentifierInput(BuiltInIdentifierSchemes.DOI, "10.1000/duplicate")]
                });
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            main.ItemEditor.IdentifierScheme = BuiltInIdentifierSchemes.DOI;
            main.ItemEditor.IdentifierValue = "10.1000/duplicate";
            await main.ItemEditor.AddIdentifierCommand.ExecuteAsync();
            await main.ItemEditor.SaveCommand.ExecuteAsync();

            main.ItemEditor.Status.Should().Contain("部分暂存操作失败");
            main.ItemEditor.Identifiers.Should().Contain(row => row.IsPending);
            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should().ContainSingle();
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Url_lookup_button_extracts_doi_upserts_identifier_and_invokes_lookup()
    {
        string path = TempDbPath("url-fetch");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("webpage", "URL fetch"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            bool lookupInvoked = false;
            main.ItemEditor.LookupRunner = (_, _, identifier, _) =>
            {
                lookupInvoked = true;
                identifier.Scheme.Should().Be(BuiltInIdentifierSchemes.DOI);
                identifier.Value.Should().Be("10.1000/xyz123");
                return Task.FromResult(new MetadataLookupOutcome(true, "已从 mock 获取元数据。"));
            };

            ItemFieldDescriptor urlField =
                main.ItemEditor.Fields.Single(field => field.Key == "Identifier:url");
            urlField.ShowsLookupButton.Should().BeTrue();
            urlField.Value = "https://doi.org/10.1000/xyz123";
            await urlField.LookupFromUrlCommand!.ExecuteAsync();

            lookupInvoked.Should().BeTrue();
            main.ItemEditor.Identifiers.Should().Contain(row =>
                !row.IsPending && row.Scheme == BuiltInIdentifierSchemes.DOI);
            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should().Contain(identifier =>
                identifier.Scheme == BuiltInIdentifierSchemes.DOI &&
                identifier.Value == "10.1000/xyz123");
            main.ItemEditor.Status.Should().Be("元数据已获取并应用。");
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Url_lookup_button_reports_status_and_adds_nothing_when_url_is_not_recognizable()
    {
        string path = TempDbPath("url-miss");
        try
        {
            MainWindowViewModel main = await OpenMainAsync(path);
            AppServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                new CreateItemRequest("webpage", "URL miss"));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            await main.EditItemByIdAsync(created.Value.ItemId.ToString());

            bool lookupInvoked = false;
            main.ItemEditor.LookupRunner = (_, _, _, _) =>
            {
                lookupInvoked = true;
                return Task.FromResult(new MetadataLookupOutcome(true, "unexpected"));
            };

            ItemFieldDescriptor urlField =
                main.ItemEditor.Fields.Single(field => field.Key == "Identifier:url");
            urlField.Value = "https://example.org/page";
            await urlField.LookupFromUrlCommand!.ExecuteAsync();

            lookupInvoked.Should().BeFalse();
            main.ItemEditor.Status.Should()
                .Be("未能从该 URL 识别出 DOI/arXiv 等标识符，请手动在「唯一标识符」中添加。");
            main.ItemEditor.Identifiers.Should().NotContain(row =>
                row.Scheme == BuiltInIdentifierSchemes.DOI || row.Scheme == BuiltInIdentifierSchemes.ArXiv ||
                row.Scheme == BuiltInIdentifierSchemes.Pmid || row.Scheme == BuiltInIdentifierSchemes.ISBN);
            (await services.Items.ListIdentifiersAsync(created.Value.ItemId)).Value.Should().BeEmpty();
        }
        finally
        {
            CleanupDb(path);
        }
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private async Task<MainWindowViewModel> OpenMainAsync(string path)
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
        await main.OpenDatabaseCommand.ExecuteAsync();
        await main.Library.CreateCommand.ExecuteAsync();
        return main;
    }

    private static string TempDbPath(string tag)
    {
        return Path.Combine(Path.GetTempPath(), $"item-editor-staging-{tag}-{Guid.NewGuid():N}.sqlite");
    }

    private static void CleanupDb(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
