using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class BiblatexImportDeduplicationTests : IAsyncLifetime
{
    private const string LindenTitle =
        "Milk Is Gold: An Environmental and Animal History of Livestock Herding in Socialist Mongolia";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"patchouli-bib-dedup-{Guid.NewGuid():N}");
    private SqliteConnectionFactory _database = null!;
    private ItemService _items = null!;
    private FileAssetService _files = null!;
    private DocumentInstanceService _documents = null!;
    private BiblatexImportService _import = null!;
    private string _filePath = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _filePath = Path.Combine(_root, "Milk Is Gold.pdf");
        await File.WriteAllTextAsync(_filePath, "Same file payload for content-hash regression");
        _database = new SqliteConnectionFactory(Path.Combine(_root, "runtime.sqlite"));
        SystemClock clock = new();
        await new MigrationRunner(_database, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
        LibraryIdentityService library = new(_database, clock);
        (await library.CreateLibraryAsync("Bib deduplication regression")).IsSuccess.Should().BeTrue();
        _items = new ItemService(_database, library, clock);
        _files = new FileAssetService(_database, library, clock);
        _documents = new DocumentInstanceService(_database, clock);
        _import = new BiblatexImportService(new BiblatexHelperClient(), _items, _files, _documents);
    }

    public Task DisposeAsync()
    {
        _database.ClearPools();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("Linden", "Kenneth E.", null, null, null, false)]
    [InlineData("Beethoven", "Ludwig", "van", "Jr.", null, false)]
    [InlineData(null, null, null, null, "Mongolian Academy of Sciences", false)]
    [InlineData("Linden", "Kenneth E.", null, null, null, true)]
    public async Task Batch_preview_matches_persisted_author_and_issued_year(
        string? family, string? given, string? particles, string? suffix, string? literal, bool literalYear)
    {
        Result<ItemMetadata> local = await _items.CreateItemAsync(new CreateItemRequest(
            "thesis", LindenTitle, PublicationTitle: "ProQuest Dissertations Publishing",
            Publisher: "Indiana University",
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, family, given, literal, suffix, particles)],
            Dates:
            [
                new ItemDateInput(ItemDateRoles.Issued,
                    literalYear ? "[]" : "[[2022]]", Literal: literalYear ? "2022" : null)
            ]));
        local.IsSuccess.Should().BeTrue(local.ErrorMessage);
        BiblatexEntryDto source = Entry("Linden2022", new BiblatexPersonDto(family, given, particles, suffix, literal));

        Result<BiblatexBatchImportPreview> preview = await _import.PreviewBatchAsync([source]);

        preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);
        BiblatexMatchCandidate candidate =
            preview.Value.Plan.Groups.Single().Candidates.Should().ContainSingle().Subject;
        candidate.ItemId.Should().Be(local.Value.ItemId.ToString());
        candidate.AuthorsMatched.Should().BeTrue();
        candidate.YearMatched.Should().BeTrue();
        candidate.SourceMatched.Should().BeFalse("the bibliography does not contain the ProQuest publication title");
        candidate.MatchCount.Should().Be(3);
    }

    [Fact]
    public async Task Batch_preview_does_not_match_a_different_suffix()
    {
        (await _items.CreateItemAsync(new CreateItemRequest("thesis", LindenTitle,
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Linden", "Kenneth E.", Suffix: "Jr.")],
                Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2022]]")])))
            .IsSuccess.Should().BeTrue();
        Result<BiblatexBatchImportPreview> preview = await _import.PreviewBatchAsync([Entry("Linden2022")]);
        preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);
        preview.Value.Plan.Groups.Single().Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_preview_does_not_count_empty_structured_authors_as_a_match()
    {
        Result<ItemMetadata> local = await _items.CreateItemAsync(new CreateItemRequest("thesis", LindenTitle,
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2022]]")]));
        local.IsSuccess.Should().BeTrue(local.ErrorMessage);
        // SQLite trims spaces in its CHECK; a legacy tab-only name is valid there but empty to .NET Trim.
        await using (Microsoft.Data.Sqlite.SqliteConnection connection = _database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO item_creators
                    (creator_id, item_id, role, family, given, suffix, particles, sequence_index, created_at)
                VALUES (@CreatorId, @ItemId, 'author', @Family, ' ', ' ', ' ', 0, @CreatedAt);
                """,
                new
                {
                    CreatorId = Guid.NewGuid().ToString("D"),
                    ItemId = local.Value.ItemId.ToString(),
                    Family = "\t",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O")
                });
        }

        BiblatexEntryDto source = Entry("empty-author", new BiblatexPersonDto(" ", " ", " ", " "));
        Result<BiblatexBatchImportPreview> preview = await _import.PreviewBatchAsync([source]);

        preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);
        preview.Value.Plan.Groups.Single().Candidates.Should().BeEmpty("title and year alone are two matches");
        BiblatexFieldMapper.AuthorMatchKeys(Map(source).Creators).Should().BeEmpty();
    }

    [Fact]
    public async Task Single_import_refuses_renamed_duplicate_before_creating_an_item()
    {
        BiblatexMappedItem source = Map(Entry("Linden2022") with { File = _filePath });
        (await _import.ApplySingleAsync(source, null, null, null)).IsSuccess.Should().BeTrue();
        string renamed = Path.Combine(_root, "Milk_Is_Gold_An_Environmental.pdf");
        File.Copy(_filePath, renamed);

        Result<BiblatexImportApplyResult> replay = await _import.ApplySingleAsync(
            source with { FilePath = renamed }, null, null, null);

        replay.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        await AssertItemAndDocumentCountsAsync(1, 1);
    }

    [Fact]
    public async Task Batch_new_choice_cannot_force_a_duplicate_file()
    {
        BiblatexEntryDto entry = Entry("Linden2022") with { File = _filePath };
        (await _import.ApplySingleAsync(Map(entry), null, null, null)).IsSuccess.Should().BeTrue();
        Result<BiblatexBatchImportPreview> preview = await _import.PreviewBatchAsync([entry]);
        preview.Value.Plan.HasCandidates.Should().BeTrue();

        Result<BiblatexImportApplyResult> replay = await _import.ApplyBatchAsync(
            preview.Value.Plan, new Dictionary<string, string> { [entry.Key] = "new" }, null);

        replay.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        await AssertItemAndDocumentCountsAsync(1, 1);
    }

    [Fact]
    public async Task Concurrent_single_imports_create_one_item_and_document()
    {
        BiblatexMappedItem source = Map(Entry("Linden2022") with { File = _filePath });
        Result<BiblatexImportApplyResult>[] results = await Task.WhenAll(
            _import.ApplySingleAsync(source, null, null, null),
            _import.ApplySingleAsync(source, null, null, null));

        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.ErrorCode == AppErrorCodes.Conflict);
        await AssertItemAndDocumentCountsAsync(1, 1);
    }

    [Fact]
    public async Task Same_batch_repeated_file_never_creates_a_second_item()
    {
        BiblatexEntryDto first = Entry("first") with { File = _filePath };
        BiblatexEntryDto second = Entry("second") with
        {
            File = _filePath,
            Fields = new Dictionary<string, string> { ["title"] = "Other metadata", ["school"] = "Indiana University" }
        };
        Result<BiblatexBatchImportPreview> preview = await _import.PreviewBatchAsync([first, second]);
        preview.Value.Plan.HasCandidates.Should().BeFalse();

        Result<BiblatexImportApplyResult> result = await _import.ApplyBatchAsync(preview.Value.Plan, null, null);

        result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        // Batch rollback may trash the first new item, but must never create the duplicate item.
        await AssertItemAndDocumentCountsAsync(1, 1);
    }

    [Fact]
    public async Task Ordinary_manual_creation_can_attach_an_existing_file()
    {
        BiblatexMappedItem source = Map(Entry("Linden2022") with { File = _filePath });
        (await _import.ApplySingleAsync(source, null, null, null)).IsSuccess.Should().BeTrue();
        Result<ItemMetadata> manual = await _items.CreateItemAsync(new CreateItemRequest("thesis", LindenTitle));
        Result<Core.Files.FileAsset> file = await _files.RegisterFileAsync(_filePath);
        Result<DocumentInstance> attached = await _documents.AttachDocumentInstanceAsync(
            manual.Value.ItemId, file.Value.FileAssetId, DocumentInstanceType.PrimaryScan, "Manual duplicate", true);

        attached.IsSuccess.Should().BeTrue(attached.ErrorMessage);
        await AssertItemAndDocumentCountsAsync(2, 2);
    }

    [Fact]
    public async Task Missing_file_retains_metadata_only_import_and_reports_skip()
    {
        BiblatexMappedItem source = Map(Entry("Linden2022") with { File = Path.Combine(_root, "missing.pdf") });
        Result<BiblatexImportApplyResult> applied = await _import.ApplySingleAsync(source, null, null, null);
        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
        applied.Value.FileSkips.Should().ContainSingle();
        await AssertItemAndDocumentCountsAsync(1, 0);
    }

    private async Task AssertItemAndDocumentCountsAsync(int itemCount, int documentCount)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM items;"))
            .Should().Be(itemCount, "no duplicate may be created even temporarily and then trashed");
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM document_instances;"))
            .Should().Be(documentCount);
    }

    private static BiblatexMappedItem Map(BiblatexEntryDto entry)
    {
        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);
        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        return mapped.Value;
    }

    private static BiblatexEntryDto Entry(string key, BiblatexPersonDto? person = null)
    {
        return new BiblatexEntryDto(key, "phdthesis", false,
            new Dictionary<string, string> { ["title"] = LindenTitle, ["school"] = "Indiana University" },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>
            {
                ["author"] = [person ?? new BiblatexPersonDto("Linden", "Kenneth E.")]
            },
            new Dictionary<string, BiblatexDateDto> { ["date"] = new([2022], [new[] { 2022 }]) },
            [], null, true, new BiblatexVerifyDto([], [], []));
    }
}
