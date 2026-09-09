using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class SearchSnippetFormatterTests
{
    [Fact]
    public void Formatter_collapses_all_newlines_and_whitespace_runs_into_single_spaces()
    {
        SearchSnippetParts parts = SearchSnippetFormatter.Format("第一行\n第二行\r\n\n第三行   第四行", "第二行");

        parts.Prefix.Should().Be("第一行 ");
        parts.Hit.Should().Be("第二行");
        parts.Suffix.Should().Be(" 第三行 第四行");
    }

    [Fact]
    public void Formatter_without_a_locatable_hit_returns_plain_text_without_bold_span()
    {
        SearchSnippetParts parts = SearchSnippetFormatter.Format("完全无关的文本", "zzzz");

        parts.Prefix.Should().Be("完全无关的文本");
        parts.Hit.Should().BeEmpty();
        parts.Suffix.Should().BeEmpty();
    }

    [Fact]
    public void Formatter_without_a_locatable_hit_truncates_long_text_from_the_head()
    {
        string text = new('文', 200);
        SearchSnippetParts parts = SearchSnippetFormatter.Format(text, "zzzz", 40);

        parts.Hit.Should().BeEmpty();
        parts.Suffix.Should().BeEmpty();
        parts.Prefix.Should().EndWith("…");
        SearchSnippetFormatter.DisplayWidth(parts.Prefix).Should().BeLessThanOrEqualTo(42);
    }

    [Fact]
    public void Formatter_finds_case_insensitive_whitespace_token_hits()
    {
        SearchSnippetParts parts = SearchSnippetFormatter.Format("An Article About Quantum Fields", "quantum fields");

        parts.Prefix.Should().Be("An Article About ");
        parts.Hit.Should().Be("Quantum Fields");
        parts.Suffix.Should().BeEmpty();
    }

    [Fact]
    public void Formatter_truncates_around_the_hit_with_cjk_double_width_budget()
    {
        string padding = new('文', 200);
        string text = $"{padding}命中词{padding}";
        SearchSnippetParts parts = SearchSnippetFormatter.Format(text, "命中词", 40);

        parts.Hit.Should().Be("命中词");
        (SearchSnippetFormatter.DisplayWidth(parts.Prefix) +
         SearchSnippetFormatter.DisplayWidth(parts.Hit) +
         SearchSnippetFormatter.DisplayWidth(parts.Suffix)).Should().BeLessThanOrEqualTo(42);
        parts.Prefix.Should().StartWith("…");
        parts.Suffix.Should().EndWith("…");
        int prefixWidth = SearchSnippetFormatter.DisplayWidth(parts.Prefix.TrimStart('…'));
        int suffixWidth = SearchSnippetFormatter.DisplayWidth(parts.Suffix.TrimEnd('…'));
        prefixWidth.Should().BeLessThanOrEqualTo(24);
        suffixWidth.Should().BeLessThanOrEqualTo(24);
    }

    [Fact]
    public void Formatter_keeps_short_snippets_untruncated()
    {
        SearchSnippetParts parts = SearchSnippetFormatter.Format("短文本命中", "命中", 96);

        parts.Prefix.Should().Be("短文本");
        parts.Hit.Should().Be("命中");
        parts.Suffix.Should().BeEmpty();
    }

    [Fact]
    public void Display_width_counts_cjk_characters_double()
    {
        SearchSnippetFormatter.DisplayWidth("ab中文c").Should().Be(7);
    }
}
