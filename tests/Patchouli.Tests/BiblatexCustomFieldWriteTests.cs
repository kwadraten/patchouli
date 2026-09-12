using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class BiblatexCustomFieldWriteTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"patchouli-bib-custom-{Guid.NewGuid():N}.sqlite");

    private ItemService _items = null!;
    private BiblatexImportService _import = null!;
    private McpWriteApi _writes = null!;

    public async Task InitializeAsync()
    {
        SqliteConnectionFactory db = new(_databasePath);
        SystemClock clock = new();
        await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
        LibraryIdentityService library = new(db, clock);
        (await library.CreateLibraryAsync("Custom field regression")).IsSuccess.Should().BeTrue();
        _items = new ItemService(db, library, clock);
        BiblatexHelperClient helper = new();
        _import = new BiblatexImportService(helper, _items, new FileAssetService(db, library, clock),
            new DocumentInstanceService(db, clock));
        _writes = new McpWriteApi(_items, helper, new CslStyleStore(db, clock));
    }

    public Task DisposeAsync()
    {
        SqliteTestCleanup.ReleasePools(_databasePath);
        File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Single_import_requires_and_respects_individual_custom_field_choices()
    {
        ItemMetadata local = await CreateItem();
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await _import.ParseTextAsync(
            "@unpublished{k,title={T},archive={New archive},license={New license},medium={Paper}}");
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        Result<BiblatexSingleImportPreview> preview =
            await _import.PreviewSingleAsync(parsed.Value.Single(), local.ItemId);
        preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);
        preview.Value.FieldConflictDescriptor.Should().NotBeNull();
        IReadOnlyList<BiblatexFieldConflict> conflicts =
            BiblatexFieldConflictAnalyzer.FindConflicts(local, preview.Value.Source);
        conflicts.Select(conflict => conflict.FieldKey).Should().BeEquivalentTo(
            "custom:archive", "custom:license", "custom:medium");
        conflicts.Single(conflict => conflict.FieldKey == "custom:archive").LocalValue.Should().Be("Old archive");

        Result<BiblatexImportApplyResult> missingChoices = await _import.ApplySingleAsync(
            preview.Value.Source, local.ItemId, null, null);
        missingChoices.IsFailure.Should().BeTrue();
        (await _items.GetItemAsync(local.ItemId)).Value.CustomFieldsJson.Should().Be(local.CustomFieldsJson);

        Result<BiblatexImportApplyResult> applied = await _import.ApplySingleAsync(
            preview.Value.Source, local.ItemId, new Dictionary<string, string>
            {
                ["custom:archive"] = BiblatexMappedItemMerge.ChoiceLocal,
                ["custom:license"] = BiblatexMappedItemMerge.ChoiceIncoming,
                ["custom:medium"] = BiblatexMappedItemMerge.ChoiceIncoming
            }, null);
        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
        Dictionary<string, string> fields = Fields((await _items.GetItemAsync(local.ItemId)).Value);
        fields["archive"].Should().Be("Old archive");
        fields["license"].Should().Be("New license");
        fields["medium"].Should().Be("Paper");
        fields["archive_location"].Should().Be("Shelf A");
    }

    [Fact]
    public async Task Accepted_merge_adopts_provided_custom_fields_and_preserves_omitted_values()
    {
        ItemMetadata local = await CreateItem();
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await _import.ParseTextAsync(
            "@unpublished{k,title={T},archive={New archive}}");
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(parsed.Value.Single());
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        UpdateItemRequest update = BiblatexMappedItemMerge.ToAcceptedUpdateRequest(local, mapped.Value, out _);
        Dictionary<string, string> fields =
            JsonSerializer.Deserialize<Dictionary<string, string>>(update.CustomFieldsJson!)!;
        fields["archive"].Should().Be("New archive");
        fields["archive_location"].Should().Be("Shelf A");
    }

    [Fact]
    public async Task Mcp_put_replaces_and_deletes_custom_fields_in_exported_projection()
    {
        ItemMetadata local = await CreateItem();
        Result<McpPutResponse> changed = await _writes.PutAsync(new McpPutRequest(
            McpResourceUris.ItemUri(local.ItemId),
            "@unpublished{k,title={T},archive={New archive},license={New license},eventtitle={Conference}}"));
        changed.IsSuccess.Should().BeTrue(changed.ErrorMessage);
        ItemMetadata stored = (await _items.GetItemAsync(local.ItemId)).Value;
        Fields(stored).Should().Contain("archive", "New archive").And.Contain("license", "New license");
        Fields(stored).Should().NotContainKey("archive_location");
        stored.CitationKey.Should().Be(local.CitationKey);
        Result<string> exported = await _import.ExportItemsAsync([local.ItemId]);
        exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
        exported.Value.Should().Contain("New archive").And.Contain("New license").And.Contain("Conference");

        Result<McpPutResponse> removed = await _writes.PutAsync(new McpPutRequest(
            McpResourceUris.ItemUri(local.ItemId), "@unpublished{k,title={T}}"));
        removed.IsSuccess.Should().BeTrue(removed.ErrorMessage);
        Fields((await _items.GetItemAsync(local.ItemId)).Value).Should().BeEmpty();
        Result<string> afterRemoval = await _import.ExportItemsAsync([local.ItemId]);
        afterRemoval.IsSuccess.Should().BeTrue(afterRemoval.ErrorMessage);
        afterRemoval.Value.Should().NotContain("New archive").And.NotContain("New license").And
            .NotContain("Conference");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task Invalid_local_custom_fields_are_rejected_without_data_loss(string invalidJson)
    {
        Result<ItemMetadata> created = await _items.CreateItemAsync(new CreateItemRequest(
            "manuscript", "T", CustomFieldsJson: invalidJson));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await _import.ParseTextAsync(
            "@unpublished{k,title={T},archive={New archive}}");
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(parsed.Value.Single());
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        Result<BiblatexImportApplyResult> result = await _import.ApplySingleAsync(mapped.Value,
            created.Value.ItemId,
            new Dictionary<string, string> { ["custom:archive"] = BiblatexMappedItemMerge.ChoiceIncoming }, null);
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await _items.GetItemAsync(created.Value.ItemId)).Value.CustomFieldsJson.Should().Be(invalidJson);

        BiblatexMappedItem originalTypeOnly =
            mapped.Value with { CustomFields = null, OriginalBiblatexEntryType = "custom" };
        Action mergeOriginalType = () =>
            BiblatexMappedItemMerge.ToAcceptedUpdateRequest(created.Value, originalTypeOnly, out _);
        mergeOriginalType.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Keyless_relaxed_bibtex_parses_and_maps_representative_fields()
    {
        // Representative database export: missing citation key, trailing comma,
        // Chinese note, curly quotes, long abstract and the standard BibTeX fields.
        const string bib = """
                           @phdthesis{
                             author = {山田, 太郎},
                             year = {2021},
                             title = {关于“汉字”标题的研究},
                             journal = {テスト誌},
                             note = {中文备注，含“弯引号”与，逗号},
                             abstract = {这是一段很长的摘要，用于验证宽松 BibTeX 的兼容性。},
                             keywords = {汉字, 测试, thesis},
                             isbn = {978-4-0000-0000-0},
                             language = {chinese},
                             url = {https://example.com/thesis},
                           }
                           """;

        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await _import.ParseTextAsync(bib);
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        parsed.Value.Should().ContainSingle();

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(parsed.Value.Single());
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        BiblatexMappedItem item = mapped.Value;
        item.ItemType.Should().Be("thesis");
        item.Title.Should().Be("关于“汉字”标题的研究");
        item.PublicationTitle.Should().Be("テスト誌");
        item.Note.Should().Contain("中文备注");
        item.AbstractText.Should().Contain("摘要");
        item.Language.Should().Be("chinese");
        item.Tags.Should().Contain("汉字");
        item.Creators.Should().ContainSingle(creator => creator.Family == "山田" && creator.Given == "太郎");
        item.Dates.Should().ContainSingle(date => date.Role == ItemDateRoles.Issued);
        item.Identifiers.Select(identifier => identifier.Scheme).Should().Contain(
            new[] { BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.URL });
    }

    [Fact]
    public async Task Keyless_import_does_not_adopt_the_temporary_source_key_as_citation_key()
    {
        Result<IReadOnlyList<BiblatexEntryDto>> parsed = await _import.ParseTextAsync(
            "@book{ author={Doe, Jane}, title={Temporary key regression} }");
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        parsed.Value.Single().Key.Should().Be("patchouli-import-1");

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(parsed.Value.Single());
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        Result<BiblatexImportApplyResult> applied = await _import.ApplySingleAsync(mapped.Value, null, null, null);
        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);

        ItemId createdId = ItemId.Parse(applied.Value.CreatedItemIds.Single());
        ItemMetadata created = (await _items.GetItemAsync(createdId)).Value;
        created.CitationKey.Should().NotBe("patchouli-import-1");
    }

    private async Task<ItemMetadata> CreateItem()
    {
        Result<ItemMetadata> created = await _items.CreateItemAsync(new CreateItemRequest(
            "manuscript", "T",
            CustomFieldsJson: """{"archive":"Old archive","license":"Old license","archive_location":"Shelf A"}"""));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        return created.Value;
    }

    private static Dictionary<string, string> Fields(ItemMetadata item)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(item.CustomFieldsJson)!;
    }
}
