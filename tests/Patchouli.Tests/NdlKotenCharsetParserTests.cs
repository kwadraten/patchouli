using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlKoten;

namespace Patchouli.Tests;

public sealed class NdlKotenCharsetParserTests
{
    [Fact]
    public void Parse_extracts_charset_train_with_escaped_characters()
    {
        string yaml = """
                      # comment
                      model:
                        charset_test: "abc\"def\\ghi"
                        charset_train: " !\"#$%&'()*+,-./0123456789:;<=>?@ABC\\"
                      """;

        IReadOnlyList<char> chars = NdlKotenCharsetParser.Parse(yaml);

        chars.Take(16).Should().Equal(' ', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/');
        chars.Should().Contain('\\');
    }

    [Fact]
    public void Parse_handles_unicode_charset()
    {
        string yaml = """
                      model:
                        charset_train: "あいうえお"
                      """;

        IReadOnlyList<char> chars = NdlKotenCharsetParser.Parse(yaml);

        chars.Should().Equal('あ', 'い', 'う', 'え', 'お');
    }

    [Fact]
    public void Parse_throws_when_charset_train_missing()
    {
        string yaml = """
                      model:
                        charset_test: "abc"
                      """;

        Action parse = () => NdlKotenCharsetParser.Parse(yaml);

        parse.Should().Throw<InvalidOperationException>();
    }
}
