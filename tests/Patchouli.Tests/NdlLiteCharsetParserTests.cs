using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteCharsetParserTests
{
    [Fact]
    public void Parse_reads_charset_train_and_skips_the_test_charset()
    {
        string yaml = """
                      # NDLmoji.yaml
                      model:
                        charset_test: "SHOULD-NOT-BE-USED"
                        charset_train: " !\"#$%&'()*+,-./"
                      """;

        IReadOnlyList<string> chars = NdlLiteCharsetParser.Parse(yaml);

        chars.Should().Equal(" ", "!", "\"", "#", "$", "%", "&", "'", "(", ")", "*", "+", ",", "-", ".", "/");
    }

    [Fact]
    public void Parse_handles_escaped_backslash_and_unicode()
    {
        string yaml = """
                      model:
                        charset_train: "a\\bあいう"
                      """;

        IReadOnlyList<string> chars = NdlLiteCharsetParser.Parse(yaml);

        chars.Should().Equal("a", "\\", "b", "あ", "い", "う");
    }

    [Fact]
    public void Parse_reports_the_lowest_class_index_as_space()
    {
        // PARSeq class 1 is charset_train[0]; the official character set starts with a space.
        string yaml = """
                      model:
                        charset_train: " ab"
                      """;

        NdlLiteCharsetParser.Parse(yaml)[0].Should().Be(" ");
    }

    [Fact]
    public void Parse_keeps_astral_characters_as_one_entry_per_code_point()
    {
        // The NDLOCR-Lite NDLmoji.yaml contains astral code points U+2231E, U+2437D,
        // U+26F94 and U+20BB7. A per-char split would insert empty surrogate slots and
        // shift the tail of the vocabulary.
        string yaml = """
                      model:
                        charset_train: "a𢌞b𤍽c𦾔d𠮷e"
                      """;

        IReadOnlyList<string> chars = NdlLiteCharsetParser.Parse(yaml);

        chars.Should().HaveCount(9);
        chars.Should().Equal("a", "\U0002231E", "b", "\U0002437D", "c", "\U00026F94", "d", "\U00020BB7", "e");
        foreach (string codePoint in chars.Where(static value => value.Length == 2))
        {
            char.IsSurrogatePair(codePoint, 0).Should().BeTrue();
        }
    }

    [Fact]
    public void Parse_never_emits_a_lone_surrogate()
    {
        string yaml = """
                      model:
                        charset_train: "A𠮷B"
                      """;

        IReadOnlyList<string> chars = NdlLiteCharsetParser.Parse(yaml);

        chars.Should().HaveCount(3);
        chars.Should().ContainSingle(value => value.Length == 2);
        foreach (string codePoint in chars)
        {
            codePoint.Length.Should().BeOneOf(1, 2);
            if (codePoint.Length == 2)
            {
                char.IsHighSurrogate(codePoint[0]).Should().BeTrue();
                char.IsLowSurrogate(codePoint[1]).Should().BeTrue();
            }
        }
    }

    [Fact]
    public void Parse_converts_upper_u_escapes_to_a_single_code_point_entry()
    {
        string yaml = """
                      model:
                        charset_train: "\U0002231E"
                      """;

        IReadOnlyList<string> chars = NdlLiteCharsetParser.Parse(yaml);

        chars.Should().ContainSingle().Which.Should().Be("\U0002231E");
    }

    [Fact]
    public void Parse_throws_when_charset_train_is_missing()
    {
        string yaml = """
                      model:
                        charset_test: "abc"
                      """;

        Action parse = () => NdlLiteCharsetParser.Parse(yaml);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*charset_train*");
    }
}
