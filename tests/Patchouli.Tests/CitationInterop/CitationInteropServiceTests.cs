using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;
using Patchouli.Infrastructure.Csl;

namespace Patchouli.Tests.CitationInterop;

/// <summary>TexPile-oriented search/export and pick validation (issue #14 §7/§4).</summary>
public sealed class CitationInteropServiceTests
{
    private static async Task<(InteropTestContext Context, ItemMetadata Book, ItemMetadata Article)> SeedAsync()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest(
            "book", "Pride and Prejudice",
            Publisher: "Patchouli Press",
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1813]]")],
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Austen", "Jane")]));
        ItemMetadata article = await context.CreateItemAsync(new CreateItemRequest(
            "article-journal", "On Testing",
            PublicationTitle: "Journal of Testing",
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2020,5]]")],
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Doe", "John")]));
        return (context, book, article);
    }

    [Fact]
    public async Task Search_by_year_finds_the_item()
    {
        (InteropTestContext context, ItemMetadata book, _) = await SeedAsync();
        await using (context)
        {
            Result<IReadOnlyList<CitationItemProjection>> results = await context.Interop.SearchAsync("1813");

            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Should().ContainSingle(p => p.ItemId == book.ItemId);
            results.Value.Single().CitationKey.Should().Be(book.CitationKey);
            results.Value.Single().CslVariables.Should().ContainKey("citation-key");
        }
    }

    [Fact]
    public async Task Search_by_publication_title_finds_the_article()
    {
        (InteropTestContext context, _, ItemMetadata article) = await SeedAsync();
        await using (context)
        {
            Result<IReadOnlyList<CitationItemProjection>> results =
                await context.Interop.SearchAsync("Journal of Testing");

            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Should().ContainSingle(p => p.ItemId == article.ItemId);
        }
    }

    [Fact]
    public async Task Search_empty_query_returns_items_and_excludes_general()
    {
        (InteropTestContext context, ItemMetadata book, ItemMetadata article) = await SeedAsync();
        await using (context)
        {
            await context.CreateItemAsync(new CreateItemRequest("general", "Loose Note"));

            Result<IReadOnlyList<CitationItemProjection>> results = await context.Interop.SearchAsync("");

            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Select(p => p.ItemId).Should().BeEquivalentTo([book.ItemId, article.ItemId]);
        }
    }

    [Fact]
    public async Task Search_skips_an_item_whose_projection_fails_and_returns_the_rest()
    {
        (InteropTestContext context, ItemMetadata book, ItemMetadata article) = await SeedAsync();
        await using (context)
        {
            // A stored date the CSL mapper cannot read (a four-component
            // date-parts endpoint) makes this item's projection fail with a
            // validation error. Search is not all-or-nothing: the damaged
            // item is skipped and the rest of the library stays searchable.
            ItemMetadata broken = await context.CreateItemAsync(new CreateItemRequest(
                "book", "Broken Date Book",
                Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2020,5,3,9]]")]));
            Result<CitationItemProjection> projection = await context.Interop.ProjectAsync(broken.ItemId);
            projection.IsFailure.Should().BeTrue("the fixture item must really be unprojectable");
            projection.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);

            Result<IReadOnlyList<CitationItemProjection>> results = await context.Interop.SearchAsync("");

            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Select(p => p.ItemId).Should().BeEquivalentTo([book.ItemId, article.ItemId]);
        }
    }

    [Fact]
    public async Task Search_fails_as_a_whole_when_a_projection_reports_an_infrastructure_failure()
    {
        (InteropTestContext context, ItemMetadata book, _) = await SeedAsync();
        await using (context)
        {
            // A database failure while projecting one row is systemic: it
            // must surface as a failed search, not as a successful search
            // that silently returns fewer results.
            CitationInteropService interop = new(
                context.Query,
                context.Items,
                new InfrastructureFailureMapper(new CslItemMapper(), book.ItemId),
                context.Renderer,
                null!);

            Result<IReadOnlyList<CitationItemProjection>> results = await interop.SearchAsync("");

            results.IsFailure.Should().BeTrue();
            results.ErrorCode.Should().Be(AppErrorCodes.DatabaseError);
        }
    }

    [Fact]
    public async Task Export_biblatex_keeps_request_order_and_dedupes()
    {
        (InteropTestContext context, ItemMetadata book, ItemMetadata article) = await SeedAsync();
        await using (context)
        {
            Result<string> exported = await context.Interop.ExportAsync(
                [article.CitationKey, book.CitationKey, article.CitationKey],
                CaywCompatibility.TranslatorBetterBiblatex);

            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            int articleIndex = exported.Value.IndexOf($"{{{article.CitationKey},", StringComparison.Ordinal);
            int bookIndex = exported.Value.IndexOf($"{{{book.CitationKey},", StringComparison.Ordinal);
            articleIndex.Should().BeGreaterThanOrEqualTo(0);
            bookIndex.Should().BeGreaterThan(articleIndex);
            exported.Value.Split(article.CitationKey).Length.Should().Be(2); // key appears exactly once
        }
    }

    [Fact]
    public async Task Export_bibtex_is_classic_not_biblatex()
    {
        (InteropTestContext context, _, ItemMetadata article) = await SeedAsync();
        await using (context)
        {
            Result<string> exported = await context.Interop.ExportAsync(
                [article.CitationKey], CaywCompatibility.TranslatorBetterBibtex);

            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            exported.Value.Should().Contain("@article{");
            exported.Value.Should().Contain("journal = {Journal of Testing}");
            exported.Value.Should().Contain("year = {2020}");
            exported.Value.Should().NotContain("journaltitle");
            exported.Value.Should().NotContain("date = {");
        }
    }

    [Fact]
    public async Task Export_missing_key_fails_the_whole_batch_and_names_the_key()
    {
        (InteropTestContext context, ItemMetadata book, _) = await SeedAsync();
        await using (context)
        {
            Result<string> exported = await context.Interop.ExportAsync(
                [book.CitationKey, "no-such-key"], CaywCompatibility.TranslatorBetterBiblatex);

            exported.IsFailure.Should().BeTrue();
            exported.ErrorCode.Should().Be("cayw_key_not_found");
            exported.ErrorMessage.Should().Contain("no-such-key");
        }
    }

    [Fact]
    public async Task Export_unsupported_translator_fails_explicitly()
    {
        (InteropTestContext context, ItemMetadata book, _) = await SeedAsync();
        await using (context)
        {
            Result<string> exported = await context.Interop.ExportAsync(
                [book.CitationKey], "Better BibTeX (JSON)");

            exported.IsFailure.Should().BeTrue();
            exported.ErrorCode.Should().Be("cayw_translator_unsupported");
        }
    }

    [Fact]
    public async Task Export_trashed_item_key_is_not_selectable()
    {
        (InteropTestContext context, ItemMetadata book, _) = await SeedAsync();
        await using (context)
        {
            Result deleted = await context.Items.DeleteItemAsync(book.ItemId);
            deleted.IsSuccess.Should().BeTrue();

            Result<string> exported = await context.Interop.ExportAsync(
                [book.CitationKey], CaywCompatibility.TranslatorBetterBiblatex);

            exported.IsFailure.Should().BeTrue();
            exported.ErrorCode.Should().Be("cayw_key_not_found");
        }
    }

    [Fact]
    public async Task ValidatePick_rejects_trashed_items_and_general_items()
    {
        (InteropTestContext context, ItemMetadata book, _) = await SeedAsync();
        await using (context)
        {
            ItemMetadata general = await context.CreateItemAsync(new CreateItemRequest("general", "Note"));
            Result<CitationPick> generalPick = await context.Interop.ValidatePickAsync(
                [new CitationBridgePickEntry(general.ItemId)]);
            generalPick.IsFailure.Should().BeTrue();
            generalPick.ErrorCode.Should().Be(AppErrorCodes.NotCitable);

            await context.Items.DeleteItemAsync(book.ItemId);
            Result<CitationPick> trashedPick = await context.Interop.ValidatePickAsync(
                [new CitationBridgePickEntry(book.ItemId)]);
            trashedPick.IsFailure.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ValidatePick_reresolves_keys_and_preserves_order_and_modifiers()
    {
        (InteropTestContext context, ItemMetadata book, ItemMetadata article) = await SeedAsync();
        await using (context)
        {
            Result<CitationPick> pick = await context.Interop.ValidatePickAsync(
            [
                new CitationBridgePickEntry(article.ItemId, "33", SuppressAuthor: true),
                new CitationBridgePickEntry(book.ItemId)
            ]);

            pick.IsSuccess.Should().BeTrue(pick.ErrorMessage);
            pick.Value.Entries.Select(e => e.CitationKey).Should()
                .Equal(article.CitationKey, book.CitationKey);
            pick.Value.Entries[0].Locator.Should().Be("33");
            pick.Value.Entries[0].SuppressAuthor.Should().BeTrue();
        }
    }

    /// <summary>Delegates to the real mapper except for one item, whose
    /// projection reports a database (infrastructure) failure.</summary>
    private sealed class InfrastructureFailureMapper : ICslItemMapper
    {
        private readonly ICslItemMapper _inner;
        private readonly ItemId _failingItemId;

        public InfrastructureFailureMapper(ICslItemMapper inner, ItemId failingItemId)
        {
            _inner = inner;
            _failingItemId = failingItemId;
        }

        public Task<Result<CslMappedItem>> MapAsync(ItemMetadata item, CancellationToken cancellationToken = default)
        {
            if (item.ItemId == _failingItemId)
            {
                return Task.FromResult(Result<CslMappedItem>.Failure(
                    AppErrorCodes.DatabaseError, "Simulated database failure."));
            }

            return _inner.MapAsync(item, cancellationToken);
        }
    }
}
