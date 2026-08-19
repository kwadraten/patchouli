using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class BiblatexImportApplyTests
{
    [Fact]
    public async Task Apply_single_creates_item_and_roundtrips_export()
    {
        if (!File.Exists(BiblatexHelperClient.ResolveDefaultHelperPath()))
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"patchouli-bib-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string db = Path.Combine(root, "runtime.sqlite");
        try
        {
            AppServices services = await AppServices.CreateAsync(db, PatchouliAppSettings.Default() with
            {
                Runtime = PatchouliAppSettings.Default().Runtime with
                {
                    RuntimeDatabasePath = db,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs"),
                    UseMockOcrOnly = true
                }
            });
            (await services.Library.CreateLibraryAsync("Bib apply")).IsSuccess.Should().BeTrue();

            const string bib = """
                               @article{cjk2020,
                                 title = {汉字标题},
                                 author = {山田 太郎},
                                 journal = {テスト誌},
                                 year = {2020},
                                 doi = {10.1000/example}
                               }
                               """;

            Result<IReadOnlyList<BiblatexEntryDto>> parsed = await services.BiblatexImport.ParseTextAsync(bib);
            parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
            Result<BiblatexSingleImportPreview> preview =
                await services.BiblatexImport.PreviewSingleAsync(parsed.Value.Single(), null);
            preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);

            Result<BiblatexImportApplyResult> applied = await services.BiblatexImport.ApplySingleAsync(
                preview.Value.Source,
                null,
                null,
                null);
            applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
            applied.Value.CreatedItemIds.Should().ContainSingle();

            ItemId itemId = ItemId.Parse(applied.Value.CreatedItemIds[0]);
            Result<ItemMetadata> item = await services.Items.GetItemAsync(itemId);
            item.IsSuccess.Should().BeTrue();
            item.Value.Title.Should().Be("汉字标题");
            item.Value.ItemType.Should().Be("article-journal");

            Result<string> exported = await services.BiblatexImport.ExportItemsAsync([itemId]);
            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            exported.Value.Should().Contain("汉字标题");
            exported.Value.Should().NotContain("file =");
        }
        finally
        {
            SqliteTestCleanup.ReleasePools(db);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Apply_batch_without_candidates_creates_all()
    {
        if (!File.Exists(BiblatexHelperClient.ResolveDefaultHelperPath()))
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"patchouli-bib-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string db = Path.Combine(root, "runtime.sqlite");
        try
        {
            AppServices services = await AppServices.CreateAsync(db, PatchouliAppSettings.Default() with
            {
                Runtime = PatchouliAppSettings.Default().Runtime with
                {
                    RuntimeDatabasePath = db,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs"),
                    UseMockOcrOnly = true
                }
            });
            (await services.Library.CreateLibraryAsync("Bib batch")).IsSuccess.Should().BeTrue();

            const string bib = """
                               @book{a2020, title = {Book A}, author = {Author A}, year = {2020}, publisher = {Pub}}
                               @book{b2021, title = {Book B}, author = {Author B}, year = {2021}, publisher = {Pub}}
                               """;
            Result<IReadOnlyList<BiblatexEntryDto>> parsed = await services.BiblatexImport.ParseTextAsync(bib);
            Result<BiblatexBatchImportPreview> preview = await services.BiblatexImport.PreviewBatchAsync(parsed.Value);
            preview.IsSuccess.Should().BeTrue(preview.ErrorMessage);
            preview.Value.Plan.HasCandidates.Should().BeFalse();

            Result<BiblatexImportApplyResult> applied =
                await services.BiblatexImport.ApplyBatchAsync(preview.Value.Plan, null, null);
            applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
            applied.Value.CreatedItemIds.Should().HaveCount(2);
        }
        finally
        {
            SqliteTestCleanup.ReleasePools(db);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Field_merge_respects_local_choices()
    {
        ItemMetadata local = new(
            ItemId.New(),
            LibraryId.New(),
            "book",
            "local-key",
            "Local Title",
            null,
            null,
            "[]",
            [],
            null,
            [],
            [],
            null,
            null,
            null,
            "Local Pub",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "[]",
            "[]",
            "{}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        BiblatexMappedItem incoming = new(
            "article-journal",
            null,
            "Incoming Title",
            null,
            null,
            [],
            [],
            [],
            null,
            null,
            null,
            "Incoming Pub",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            "src",
            "article");

        UpdateItemRequest request = BiblatexMappedItemMerge.ToFieldChoiceUpdateRequest(
            local,
            incoming,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["item_type"] = BiblatexMappedItemMerge.ChoiceIncoming,
                ["title"] = BiblatexMappedItemMerge.ChoiceLocal,
                ["publisher"] = BiblatexMappedItemMerge.ChoiceIncoming
            },
            out _);

        request.ItemType.Should().Be("article-journal");
        request.Title.Should().Be("Local Title");
        request.Publisher.Should().Be("Incoming Pub");
    }
}
