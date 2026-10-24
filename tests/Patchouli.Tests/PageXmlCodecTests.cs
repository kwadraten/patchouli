using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using System.Xml.Linq;

namespace Patchouli.Tests;

public sealed class PageXmlCodecTests
{
    [Fact]
    public void Encode_and_decode_preserves_box_identity_and_suppressed_footnote_translation()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBoxId parentId = DocumentBoxId.New();
        DocumentBoxId footnoteId = DocumentBoxId.New();
        DocumentBox parent = Box(parentId, null, DocumentBoxType.Image,
            new MediaBoxPayload("asset-secret", null));
        DocumentBox footnote = Box(footnoteId, parentId, DocumentBoxType.ImageFootnote,
            new TextBoxPayload("Source & note"), true);
        DocumentBox[] boxes = [parent, footnote];

        string sourceXml = PageXmlCodec.EncodePage(documentId, 3, revision, boxes);

        Assert.Contains("complete=\"true\"", sourceXml, StringComparison.Ordinal);
        Assert.Contains("suppressed=\"true\"", sourceXml, StringComparison.Ordinal);
        Assert.Contains("Source &amp; note", sourceXml, StringComparison.Ordinal);
        Assert.Contains("null=\"true\"", sourceXml, StringComparison.Ordinal);
        Assert.DoesNotContain("asset-secret", sourceXml, StringComparison.Ordinal);
        XElement parentElement = XDocument.Parse(sourceXml).Root!.Element("box")!;
        Result<DocumentBoxPayload> nestedBox =
            PageXmlCodec.DecodeBox(parentElement.ToString(SaveOptions.DisableFormatting), parent);
        Assert.True(nestedBox.IsFailure);
        parentElement.Elements("box").Remove();
        Result<DocumentBoxPayload> decodedParent =
            PageXmlCodec.DecodeBox(parentElement.ToString(SaveOptions.DisableFormatting), parent);
        Assert.True(decodedParent.IsSuccess);
        Assert.Null(Assert.IsType<MediaBoxPayload>(decodedParent.Value).Description);

        string editedXml = PageXmlCodec.EncodePage(
            documentId,
            3,
            revision,
            boxes,
            new Dictionary<DocumentBoxId, string> { [footnoteId] = "Translated <note>" });
        Result<PageXmlDocument> decoded = PageXmlCodec.DecodePage(editedXml, documentId, 3, revision, boxes);

        Assert.True(decoded.IsSuccess);
        Assert.Equal("Translated <note>",
            Assert.IsType<TextBoxPayload>(decoded.Value.TranslatedPayloads[footnoteId]).Markdown);
        Assert.Equal(0, decoded.Value.TranslationVersion);
    }

    [Fact]
    public void Decode_rejects_readonly_attribute_changes_and_external_entities()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBox box = Box(DocumentBoxId.New(), null, DocumentBoxType.Text, new TextBoxPayload("Source"));
        string xml = PageXmlCodec.EncodePage(documentId, 1, revision, [box]);
        string changed = xml.Replace("type=\"text\"", "type=\"code\"", StringComparison.Ordinal);
        string dtd = "<!DOCTYPE page [<!ENTITY x SYSTEM 'file:///c:/Windows/win.ini'>]>" + xml;

        Assert.True(PageXmlCodec.DecodePage(changed, documentId, 1, revision, [box]).IsFailure);
        Assert.True(PageXmlCodec.DecodePage(dtd, documentId, 1, revision, [box]).IsFailure);
    }

    [Fact]
    public void Decode_rejects_translation_of_a_logical_page_even_without_children()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBox logicalPage = Box(
            DocumentBoxId.New(), null, DocumentBoxType.LogicalPage, new TextBoxPayload("logical page content"));
        XDocument edited = XDocument.Parse(PageXmlCodec.EncodePage(documentId, 1, revision, [logicalPage]));
        edited.Root!.Element("box")!.SetAttributeValue("translated", "true");

        Result<PageXmlDocument> result = PageXmlCodec.DecodePage(
            edited.ToString(SaveOptions.DisableFormatting), documentId, 1, revision, [logicalPage]);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Decode_box_rejects_a_malformed_continues_from_page_hint()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        DocumentBoxId headId = DocumentBoxId.New();
        DocumentBoxId followerId = DocumentBoxId.New();
        DocumentBox head = Box(headId, null, DocumentBoxType.Text, new TextBoxPayload("First."))
            with
            {
                NextSiblingBoxId = followerId
            };
        DocumentBox follower = Box(followerId, null, DocumentBoxType.Text, new TextBoxPayload("Second."))
            with
            {
                ContinuesFromBoxId = headId
            };
        DocumentBox[] boxes = [head, follower];
        string xml = PageXmlCodec.EncodePage(documentId, 1, revision, boxes,
            continuationSourcePages: new Dictionary<DocumentBoxId, int> { [headId] = 1 });
        XDocument document = XDocument.Parse(xml);
        XElement followerElement = document.Root!.Elements("box")
            .Single(element => element.Attribute("continues_from") is not null);

        // box 级解码与页级解码共享提示的形式校验：必须是正整数页码。
        followerElement.SetAttributeValue("continues_from_page", "0");
        Assert.True(PageXmlCodec.DecodeBox(
            followerElement.ToString(SaveOptions.DisableFormatting), follower).IsFailure);
        followerElement.SetAttributeValue("continues_from_page", "abc");
        Assert.True(PageXmlCodec.DecodeBox(
            followerElement.ToString(SaveOptions.DisableFormatting), follower).IsFailure);
        // 合法的正整数提示在 box 级被接受；它与源框实际页码是否一致由页级解码校验。
        followerElement.SetAttributeValue("continues_from_page", "2");
        Assert.True(PageXmlCodec.DecodeBox(
            followerElement.ToString(SaveOptions.DisableFormatting), follower).IsSuccess);
        // 页级解码：源框在本页时，提示必须等于本页页码。
        Assert.True(PageXmlCodec.DecodePage(
            document.ToString(SaveOptions.DisableFormatting), documentId, 1, revision, boxes).IsFailure);
    }

    private static DocumentBox Box(
        DocumentBoxId id,
        DocumentBoxId? parent,
        string type,
        DocumentBoxPayload payload,
        bool suppressed = false)
    {
        DocumentInstanceId documentId = new(Guid.Empty);
        PageId pageId = new(Guid.Empty);
        return new DocumentBox(
            new DocumentTreeRevisionId(Guid.Empty), id, documentId, pageId, parent, null,
            type, null, new NormalizedBBox(0, 0, 0.2, 0.2), payload, null, null, null, suppressed);
    }
}
