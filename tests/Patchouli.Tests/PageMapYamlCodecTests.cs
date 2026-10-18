using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Round-trip tests for the <c>map.yml</c> YAML codec (profile <c>patchouli-page-map-1</c>)
/// and the fixed-filename map URI. These compile here; they execute wherever the xunit
/// runner can reach the test host.
/// </summary>
public sealed class PageMapYamlCodecTests
{
    private static DocumentBoxId Box(string suffix)
    {
        return new DocumentBoxId(Guid.Parse($"22222222-2222-2222-2222-2222222222{suffix}"));
    }

    private static readonly DocumentInstanceId DocId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static DocumentPageMap SampleMap()
    {
        DocumentBoxId boxA = Box("01");
        DocumentBoxId boxB = Box("02");
        return new DocumentPageMap(
            DocId,
            [new PhysicalPaginationRule(1, 4, 1)],
            [
                new LogicalPaginationRule(5, 6, 10,
                    [new LogicalPageReadingEntry(boxA, 5), new LogicalPageReadingEntry(boxB, 6)])
            ],
            [
                new PageLabelOverride(new PageLabelTarget(2, null), "ii"),
                new PageLabelOverride(new PageLabelTarget(null, boxB), "扉页二")
            ],
            [
                new ChapterNode("ch1", "第一章", new NavigationPosition(5, boxA),
                    [ChapterNode.Leaf("ch1-1", "第一节", new NavigationPosition(6))])
            ]);
    }

    [Fact]
    public void Encode_Decode_RoundTrips_All_Sections()
    {
        DocumentPageMap map = SampleMap();
        string yaml = DocumentPageMapYamlCodec.Encode(map, true);

        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(yaml, DocId);

        Assert.True(decoded.IsSuccess, decoded.ErrorMessage);
        DocumentPageMap back = decoded.Value;
        Assert.Equal(map.PhysicalRules, back.PhysicalRules);
        back.LogicalRules.Should().BeEquivalentTo(map.LogicalRules, options => options.WithStrictOrdering());
        Assert.Equal(map.PageLabels, back.PageLabels);
        Assert.Equal(map.Chapters.Count, back.Chapters.Count);
        Assert.Equal("ch1", back.Chapters[0].ChapterId);
        Assert.Single(back.Chapters[0].Children);
        Assert.Equal("第一节", back.Chapters[0].Children[0].Title);
        Assert.Empty(PageMapValidator.Validate(back));
    }

    [Fact]
    public void Empty_Map_Projection_RoundTrips()
    {
        string yaml = DocumentPageMapYamlCodec.EncodeEmpty(DocId);

        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(yaml, DocId);

        Assert.True(decoded.IsSuccess, decoded.ErrorMessage);
        Assert.True(decoded.Value.IsEmpty);
    }

    [Fact]
    public void Decode_Rejects_Wrong_Profile()
    {
        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode("profile: something-else\n", DocId);

        Assert.True(decoded.IsFailure);
    }

    [Fact]
    public void Decode_Rejects_Document_Id_Mismatch()
    {
        string yaml = DocumentPageMapYamlCodec.Encode(SampleMap(), true);

        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(yaml, DocumentInstanceId.New());

        Assert.True(decoded.IsFailure);
    }

    [Fact]
    public void Map_Uri_Parses_With_Fixed_Filename()
    {
        string uri = McpResourceUris.MapUri(DocId);

        Assert.Equal($"patchouli://texts/{DocId}/map.yml", uri);
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(uri);

        Assert.True(parsed.IsSuccess, parsed.ErrorMessage);
        Assert.Equal(McpUriKind.DocumentMap, parsed.Value.Kind);
        Assert.Equal(DocId, parsed.Value.DocumentId);
        Assert.Equal(uri, parsed.Value.CanonicalUri);
    }

    [Fact]
    public void Map_Uri_Rejects_Query_Parameters()
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(McpResourceUris.MapUri(DocId) + "?x=1");

        Assert.True(parsed.IsFailure);
    }
}
