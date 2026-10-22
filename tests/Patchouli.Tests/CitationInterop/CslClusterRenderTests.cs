using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;

namespace Patchouli.Tests.CitationInterop;

/// <summary>CSL cluster rendering through the interop path (issue #14 §6/§9).</summary>
public sealed class CslClusterRenderTests
{
    private static async Task<(InteropTestContext Context, ItemMetadata Austen, ItemMetadata Bronte)> SeedAsync()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        ItemMetadata austen = await context.CreateItemAsync(new CreateItemRequest(
            "book", "Pride and Prejudice",
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1813]]")],
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Austen", "Jane")]));
        ItemMetadata bronte = await context.CreateItemAsync(new CreateItemRequest(
            "book", "Jane Eyre",
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1847]]")],
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Brontë", "Charlotte")]));
        return (context, austen, bronte);
    }

    [Fact]
    public async Task Cluster_render_is_inline_and_applies_locator_prefix_suffix()
    {
        (InteropTestContext context, ItemMetadata austen, _) = await SeedAsync();
        await using (context)
        {
            Result<CslClusterRenderResult> rendered = await context.Renderer.RenderClusterAsync(
                new CslClusterRenderRequest(
                    [new CslCitationItem(austen.ItemId, "33", "page", "see", "and more")],
                    "probe", "en-US", CslRenderFormats.Text));

            rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
            // My stage-4 probe against the real processor produced
            // "(see Austen, 1813, p. 33 and more)" for this shape.
            rendered.Value.CitationText.Should().Contain("see").And.Contain("Austen")
                .And.Contain("1813").And.Contain("33").And.Contain("and more");
            rendered.Value.CitationText.Should().StartWith("(").And.EndWith(")");
            rendered.Value.CitationHtml.Should().Contain("Austen");
        }
    }

    [Fact]
    public async Task Cluster_suppress_author_removes_the_author_names()
    {
        (InteropTestContext context, ItemMetadata austen, _) = await SeedAsync();
        await using (context)
        {
            Result<CslClusterRenderResult> rendered = await context.Renderer.RenderClusterAsync(
                new CslClusterRenderRequest(
                    [new CslCitationItem(austen.ItemId, SuppressAuthor: true)],
                    "probe", "en-US", CslRenderFormats.Text));

            rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
            rendered.Value.CitationText.Should().NotContain("Austen");
            rendered.Value.CitationText.Should().Contain("1813");
        }
    }

    [Fact]
    public async Task Cluster_preserves_pick_order()
    {
        (InteropTestContext context, ItemMetadata austen, ItemMetadata bronte) = await SeedAsync();
        await using (context)
        {
            Result<CslClusterRenderResult> rendered = await context.Renderer.RenderClusterAsync(
                new CslClusterRenderRequest(
                    [new CslCitationItem(bronte.ItemId), new CslCitationItem(austen.ItemId)],
                    "probe", "en-US", CslRenderFormats.Text));

            rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
            int bronteIndex = rendered.Value.CitationText.IndexOf("Brontë", StringComparison.Ordinal);
            int austenIndex = rendered.Value.CitationText.IndexOf("Austen", StringComparison.Ordinal);
            bronteIndex.Should().BeGreaterThanOrEqualTo(0);
            austenIndex.Should().BeGreaterThan(bronteIndex);
        }
    }

    [Fact]
    public async Task Citation_only_style_renders_cluster_without_bibliography()
    {
        (InteropTestContext context, ItemMetadata austen, _) = await SeedAsync();
        await using (context)
        {
            Result<CslClusterRenderResult> rendered = await context.Renderer.RenderClusterAsync(
                new CslClusterRenderRequest(
                    [new CslCitationItem(austen.ItemId)],
                    "citation-only", "en-US", CslRenderFormats.Text));

            rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
            rendered.Value.CitationText.Should().Contain("Austen");
        }
    }

    [Fact]
    public async Task Formatted_citation_through_interop_service_is_inline_not_bibliography()
    {
        (InteropTestContext context, ItemMetadata austen, _) = await SeedAsync();
        await using (context)
        {
            CitationPick pick = new([
                new CitationPickEntry(austen.ItemId, austen.CitationKey, "33")
            ]);
            Result<CitationFormattedOutput> formatted = await context.Interop.FormatPickAsync(
                pick, "formatted-citation", new CitationFormatOptions(), "probe", "en-US");

            formatted.IsSuccess.Should().BeTrue(formatted.ErrorMessage);
            formatted.Value.Body.Should().Contain("1813").And.Contain("33");
            formatted.Value.Body.Should().NotContain("Pride and Prejudice");
            formatted.Value.ContentType.Should().StartWith("text/plain");
        }
    }

    [Fact]
    public async Task Formatted_bibliography_uses_existing_bibliography_path()
    {
        (InteropTestContext context, ItemMetadata austen, _) = await SeedAsync();
        await using (context)
        {
            CitationPick pick = new([new CitationPickEntry(austen.ItemId, austen.CitationKey)]);
            Result<CitationFormattedOutput> formatted = await context.Interop.FormatPickAsync(
                pick, "formatted-bibliography", new CitationFormatOptions(), "probe", "en-US");

            formatted.IsSuccess.Should().BeTrue(formatted.ErrorMessage);
            formatted.Value.Body.Should().Contain("Pride and Prejudice");
        }
    }

    [Fact]
    public async Task Cluster_with_general_item_fails_like_bibliography_does()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata general = await context.CreateItemAsync(new CreateItemRequest("general", "Not Ready"));
            Result<CslClusterRenderResult> rendered = await context.Renderer.RenderClusterAsync(
                new CslClusterRenderRequest(
                    [new CslCitationItem(general.ItemId)], "probe", "en-US", CslRenderFormats.Text));

            rendered.IsFailure.Should().BeTrue();
        }
    }
}
