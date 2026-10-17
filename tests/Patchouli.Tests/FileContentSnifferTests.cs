using FluentAssertions;
using Patchouli.Core.Import;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: content sniffing must check actual file bytes, never just the extension.
/// </summary>
public sealed class FileContentSnifferTests
{
    [Fact]
    public void SniffKind_detects_pdf_magic()
    {
        FileContentSniffer.SniffKind("%PDF-1.7 some bytes"u8.ToArray()).Should()
            .Be(LocalImportKind.Pdf);
    }

    [Fact]
    public void SniffKind_rejects_content_without_pdf_magic_even_with_pdf_extension()
    {
        // The caller decides by content; a renamed text file is not a PDF.
        FileContentSniffer.SniffKind("just some text"u8.ToArray()).Should()
            .Be(LocalImportKind.Unknown);
    }

    [Fact]
    public void SniffKind_detects_csl_xml_with_bom_declaration_and_comment()
    {
        string xml = "\ufeff<?xml version=\"1.0\"?>\n<!-- leading comment -->\n" +
                     "<style xmlns=\"http://purl.org/net/xbiblio/csl\"><info/></style>";
        FileContentSniffer.SniffKind(System.Text.Encoding.UTF8.GetBytes(xml)).Should()
            .Be(LocalImportKind.Csl);
    }

    [Fact]
    public void SniffKind_detects_bare_style_root_without_namespace()
    {
        FileContentSniffer.SniffKind("<style><info/></style>"u8.ToArray()).Should()
            .Be(LocalImportKind.Csl);
    }

    [Fact]
    public void SniffKind_rejects_non_style_xml()
    {
        FileContentSniffer.SniffKind("<html><body/></html>"u8.ToArray()).Should()
            .Be(LocalImportKind.Unknown);
    }

    [Fact]
    public void SniffKind_rejects_style_prefix_that_is_not_the_root_element()
    {
        // "<stylesheet" must not match "<style".
        FileContentSniffer.SniffKind("<stylesheet/>"u8.ToArray()).Should()
            .Be(LocalImportKind.Unknown);
    }

    [Fact]
    public void SniffKind_rejects_truncated_xml_declaration()
    {
        FileContentSniffer.SniffKind("<?xml version=\"1.0\""u8.ToArray()).Should()
            .Be(LocalImportKind.Unknown);
    }

    [Fact]
    public void SniffKind_empty_input_is_unknown()
    {
        FileContentSniffer.SniffKind(ReadOnlySpan<byte>.Empty).Should().Be(LocalImportKind.Unknown);
    }

    [Theory]
    [InlineData(null, LocalImportKind.Unknown, true)]
    [InlineData("", LocalImportKind.Unknown, true)]
    [InlineData("pdf", LocalImportKind.Pdf, true)]
    [InlineData("PDF", LocalImportKind.Pdf, true)]
    [InlineData(" pdf ", LocalImportKind.Pdf, true)]
    [InlineData("csl", LocalImportKind.Csl, true)]
    [InlineData("CSL", LocalImportKind.Csl, true)]
    [InlineData("docx", LocalImportKind.Unknown, false)]
    public void TryParseKind_parses_explicit_kind(
        string? value, LocalImportKind expected, bool expectedOk)
    {
        bool ok = FileContentSniffer.TryParseKind(value, out LocalImportKind kind);

        ok.Should().Be(expectedOk);
        kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/paper.pdf", true)]
    [InlineData("http://example.com/paper.pdf", true)]
    [InlineData("ftp://example.com/paper.pdf", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("D:/papers/paper.pdf", false)]
    [InlineData(@"D:\papers\paper.pdf", false)]
    [InlineData("/home/user/papers/paper.pdf", false)]
    [InlineData("papers/paper.pdf", false)]
    [InlineData("paper.pdf", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsUrlLike_classifies_urls_and_local_paths(string input, bool expected)
    {
        FileContentSniffer.IsUrlLike(input).Should().Be(expected);
    }
}
