using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// The <c>patchouli://images/</c> read-only domain: scope/document/page-image
/// shapes, canonical query ordering (rev, box, overlay, max_edge), and the
/// invalid-query rejections. Query parameters are part of the identity: a
/// different read is a different canonical URI.
/// </summary>
public sealed class McpImagesUriTests
{
    private const string DocId = "11111111-1111-1111-1111-111111111111";
    private const string RevId = "22222222-2222-2222-2222-222222222222";
    private const string BoxId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void Images_scope_parses()
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse("patchouli://images/");

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Kind.Should().Be(McpUriKind.ImagesScope);
        parsed.Value.CanonicalUri.Should().Be("patchouli://images/");
    }

    [Fact]
    public void Images_document_parses()
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse("patchouli://images/" + DocId + "/");

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Kind.Should().Be(McpUriKind.ImagesDocument);
        parsed.Value.DocumentId.Should().Be(new DocumentInstanceId(Guid.Parse(DocId)));
        parsed.Value.CanonicalUri.Should().Be("patchouli://images/" + DocId + "/");
    }

    [Fact]
    public void Page_image_parses_without_query()
    {
        Result<McpUriParseResult> parsed =
            McpResourceUris.Parse("patchouli://images/" + DocId + "/page-3.png");

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Kind.Should().Be(McpUriKind.PageImage);
        parsed.Value.DocumentId.Should().Be(new DocumentInstanceId(Guid.Parse(DocId)));
        parsed.Value.PageIndex.Should().Be(3);
        parsed.Value.ContentType.Should().Be("image/png");
        parsed.Value.TreeRevisionId.Should().BeNull();
        parsed.Value.BoxId.Should().BeNull();
        parsed.Value.OverlayBoxes.Should().BeFalse();
        parsed.Value.MaxEdge.Should().BeNull();
    }

    [Fact]
    public void Page_image_query_is_normalized_to_canonical_order()
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(
            "patchouli://images/" + DocId + "/page-01.png?max_edge=512&overlay=boxes&box=" + BoxId +
            "&rev=" + RevId);

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.PageIndex.Should().Be(1);
        parsed.Value.TreeRevisionId.Should().Be(new DocumentTreeRevisionId(Guid.Parse(RevId)));
        parsed.Value.BoxId.Should().Be(new DocumentBoxId(Guid.Parse(BoxId)));
        parsed.Value.OverlayBoxes.Should().BeTrue();
        parsed.Value.MaxEdge.Should().Be(512);
        parsed.Value.CanonicalUri.Should().Be(
            "patchouli://images/" + DocId + "/page-1.png?rev=" + RevId + "&box=" + BoxId +
            "&overlay=boxes&max_edge=512");
    }

    [Theory]
    [InlineData("patchouli://images/" + DocId + "/page-0.png")]
    [InlineData("patchouli://images/" + DocId + "/page-x.png")]
    [InlineData("patchouli://images/" + DocId + "/page-1.jpg")]
    [InlineData("patchouli://images/not-a-guid/page-1.png")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?overlay=circles")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?max_edge=10")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?max_edge=8193")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?max_edge=abc")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?rev=not-a-guid")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?box=not-a-guid")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?rev=" + RevId + "&rev=" + RevId)]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?unknown=1")]
    [InlineData("patchouli://images/" + DocId + "/page-1.png?rev")]
    [InlineData("patchouli://images/?foo=1")]
    [InlineData("patchouli://images/" + DocId + "/?rev=" + RevId)]
    public void Invalid_image_uris_are_rejected(string uri)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(uri);

        parsed.IsSuccess.Should().BeFalse(uri);
    }

    [Fact]
    public void Image_uri_builder_orders_query_canonically()
    {
        string uri = McpResourceUris.ImageUri(
            new DocumentInstanceId(Guid.Parse(DocId)), 2,
            new DocumentTreeRevisionId(Guid.Parse(RevId)),
            new DocumentBoxId(Guid.Parse(BoxId)), true, 1536);

        uri.Should().Be("patchouli://images/" + DocId + "/page-2.png?rev=" + RevId + "&box=" + BoxId +
                        "&overlay=boxes&max_edge=1536");
    }
}
