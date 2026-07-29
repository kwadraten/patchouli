using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;

namespace Patchouli.Tests;

public sealed class UrlIdentifierExtractorTests
{
    [Theory]
    [InlineData("https://doi.org/10.1000/xyz123", "10.1000/xyz123")]
    [InlineData("https://doi.org/10.1000/XYZ123", "10.1000/xyz123")]
    [InlineData("https://link.springer.com/article/10.1007/s12345-021-00001-1", "10.1007/s12345-021-00001-1")]
    [InlineData("See https://doi.org/10.1000/xyz123).", "10.1000/xyz123")]
    public void Extracts_doi_from_doi_org_and_publisher_urls(string url, string expected)
    {
        NormalizedIdentifier? extracted = UrlIdentifierExtractor.Extract(url);

        extracted.Should().NotBeNull();
        extracted!.Scheme.Should().Be(BuiltInIdentifierSchemes.DOI);
        extracted.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://arxiv.org/abs/2301.00001", "2301.00001")]
    [InlineData("https://arxiv.org/abs/2301.00001v2", "2301.00001")]
    [InlineData("https://arxiv.org/pdf/2301.00001.pdf", "2301.00001")]
    [InlineData("https://arxiv.org/abs/hep-th/9901001", "hep-th/9901001")]
    public void Extracts_arxiv_ids(string url, string expected)
    {
        NormalizedIdentifier? extracted = UrlIdentifierExtractor.Extract(url);

        extracted.Should().NotBeNull();
        extracted!.Scheme.Should().Be(BuiltInIdentifierSchemes.ArXiv);
        extracted.Value.Should().Be(expected);
    }

    [Fact]
    public void Extracts_pmid_from_pubmed_url()
    {
        NormalizedIdentifier? extracted =
            UrlIdentifierExtractor.Extract("https://pubmed.ncbi.nlm.nih.gov/12345678/");

        extracted.Should().NotBeNull();
        extracted!.Scheme.Should().Be(BuiltInIdentifierSchemes.Pmid);
        extracted.Value.Should().Be("12345678");
    }

    [Fact]
    public void Extracts_isbn13_only_from_isbn_path_segments()
    {
        NormalizedIdentifier? extracted =
            UrlIdentifierExtractor.Extract("https://books.example.com/isbn/9780306406157");

        extracted.Should().NotBeNull();
        extracted!.Scheme.Should().Be(BuiltInIdentifierSchemes.ISBN);
        extracted.Value.Should().Be("9780306406157");
    }

    [Theory]
    [InlineData("https://example.org/page")]
    [InlineData("https://example.com/products/9780306406157")] // 13 digits, but not an isbn segment
    [InlineData("https://books.example.com/isbn/9780306406158")] // isbn segment, but bad checksum
    [InlineData("not a url at all")]
    [InlineData("")]
    public void Returns_null_when_nothing_recognizable(string url)
    {
        UrlIdentifierExtractor.Extract(url).Should().BeNull();
    }
}
