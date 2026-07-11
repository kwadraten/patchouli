using FluentAssertions;
using Patchouli.Infrastructure.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUContentListParserTests
{
    [Fact]
    public void Parse_returns_pages_from_valid_json()
    {
        string json = """
                      {
                          "pages": [
                              { "page_num": 1, "width": 595, "height": 842, "blocks": [
                                  { "type": "text", "bbox": [0, 0, 100, 50], "text": "Hello" }
                              ]}
                          ]
                      }
                      """;
        MinerUContentListParser parser = new();
        MinerUContentListDocument? doc = parser.Parse(json);
        doc.Should().NotBeNull();
        doc!.Pages.Should().HaveCount(1);
        doc.Pages[0].PageNum.Should().Be(1);
        doc.Pages[0].Blocks.Should().HaveCount(1);
        doc.Pages[0].Blocks[0].Text.Should().Be("Hello");
    }

    [Fact]
    public void Parse_returns_null_for_empty_input()
    {
        MinerUContentListParser parser = new();
        parser.Parse("").Should().BeNull();
    }

    [Fact]
    public void Parse_handles_missing_bbox()
    {
        string json = """
                      {
                          "pages": [
                              { "page_num": 1, "width": 595, "height": 842, "blocks": [
                                  { "type": "text", "text": "No bbox" }
                              ]}
                          ]
                      }
                      """;
        MinerUContentListParser parser = new();
        MinerUContentListDocument? doc = parser.Parse(json);
        doc.Should().NotBeNull();
        doc!.Pages[0].Blocks[0].Bbox.Should().BeNull();
    }

    [Fact]
    public void Parse_handles_real_mineru_flat_content_list()
    {
        string json = """
                      [
                        { "type": "text", "page_idx": 0, "text": "First paragraph", "text_level": 0, "bbox": [10, 20, 900, 80] },
                        { "type": "equation", "page_idx": 1, "text": "E = mc^2", "bbox": [10, 100, 900, 160] },
                        { "type": "image", "page_idx": 1, "img_path": "images/1.png" }
                      ]
                      """;

        MinerUContentListParser parser = new();
        MinerUContentListDocument? doc = parser.Parse(json);

        doc.Should().NotBeNull();
        doc!.Pages.Should().HaveCount(2);
        doc.Pages[0].PageNum.Should().Be(1);
        doc.Pages[0].Width.Should().Be(1000);
        doc.Pages[0].Blocks[0].Text.Should().Be("First paragraph");
        doc.Pages[1].PageNum.Should().Be(2);
        doc.Pages[1].Blocks.Should().HaveCount(2);
        doc.Pages[1].Blocks[0].Type.Should().Be("equation");
    }
}
