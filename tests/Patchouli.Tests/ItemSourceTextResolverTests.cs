using FluentAssertions;
using Patchouli.Core.Library;

namespace Patchouli.Tests;

public sealed class ItemSourceTextResolverTests
{
    [Theory]
    [InlineData("book", "Some Journal", "Test Press", "Test Press")]
    [InlineData("chapter", "Some Journal", "Test Press", "Test Press")]
    [InlineData("classic", "Some Journal", "Test Press", "Test Press")]
    [InlineData("thesis", "Some Journal", "University", "University")]
    [InlineData("report", "Some Journal", "Institute", "Institute")]
    public void Publisher_types_prefer_publisher(string itemType, string publicationTitle, string publisher,
        string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Theory]
    [InlineData("article-journal", "Nature", "Press", "Nature")]
    [InlineData("article-magazine", "Time", "Press", "Time")]
    [InlineData("article-newspaper", "Times", "Press", "Times")]
    [InlineData("review", "Review Journal", "Press", "Review Journal")]
    [InlineData("review-book", "Book Review", "Press", "Book Review")]
    public void Article_types_prefer_publication_title(string itemType, string publicationTitle, string publisher,
        string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Theory]
    [InlineData("paper-conference", "Proceedings", "IEEE", "Proceedings")]
    [InlineData("event", "Conference", "Organizer", "Conference")]
    [InlineData("speech", "Symposium", "Host", "Symposium")]
    public void Conference_and_event_types_prefer_publication_title_then_publisher(string itemType,
        string publicationTitle, string publisher, string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Theory]
    [InlineData("paper-conference", null, "IEEE", "IEEE")]
    [InlineData("event", "", "Organizer", "Organizer")]
    [InlineData("speech", "   ", "Host", "Host")]
    public void Conference_and_event_types_fall_back_to_publisher_when_publication_title_missing(string itemType,
        string? publicationTitle, string publisher, string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Theory]
    [InlineData("book", null, "Test Press", "Test Press")]
    [InlineData("article-journal", "Nature", null, "Nature")]
    public void Missing_secondary_field_returns_primary(string itemType, string? publicationTitle, string? publisher,
        string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Theory]
    [InlineData("book", null, null, "")]
    [InlineData("article-journal", "", "", "")]
    [InlineData("document", "   ", "   ", "")]
    public void Empty_fields_return_empty_string_without_placeholder(string itemType, string? publicationTitle,
        string? publisher, string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Theory]
    [InlineData("document", "Archive", "Library", "Archive")]
    [InlineData("webpage", "Site", "Publisher", "Site")]
    [InlineData("software", "Repo", "Org", "Repo")]
    public void Other_types_prefer_publication_title_then_publisher(string itemType, string publicationTitle,
        string publisher, string expected)
    {
        ItemSourceTextResolver.Resolve(itemType, publicationTitle, publisher).Should().Be(expected);
    }

    [Fact]
    public void Whitespace_is_trimmed_from_result()
    {
        ItemSourceTextResolver.Resolve("book", "  Journal  ", "  Press  ").Should().Be("Press");
    }

    [Fact]
    public void Empty_item_type_falls_back_to_publication_then_publisher()
    {
        ItemSourceTextResolver.Resolve("", "Journal", "Press").Should().Be("Journal");
        ItemSourceTextResolver.Resolve("", null, "Press").Should().Be("Press");
    }
}
