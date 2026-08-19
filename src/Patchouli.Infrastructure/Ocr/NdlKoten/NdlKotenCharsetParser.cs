using System.Globalization;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public static class NdlKotenCharsetParser
{
    public static IReadOnlyList<char> Parse(string yamlText)
    {
        ReadOnlySpan<char> text = yamlText.AsSpan();
        int trainIndex = text.IndexOf("charset_train:".AsSpan(), StringComparison.Ordinal);
        if (trainIndex < 0)
        {
            throw new InvalidOperationException("'charset_train:' not found in NDLmoji.yaml.");
        }

        ReadOnlySpan<char> afterKey = text[(trainIndex + "charset_train:".Length)..];
        int quoteIndex = afterKey.IndexOf('"');
        if (quoteIndex < 0)
        {
            throw new InvalidOperationException("Expected quoted charset_train value in NDLmoji.yaml.");
        }

        ReadOnlySpan<char> value = afterKey[(quoteIndex + 1)..];
        List<char> result = new();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[i + 1];
                switch (next)
                {
                    case '"':
                        result.Add('"');
                        i++;
                        continue;
                    case '\\':
                        result.Add('\\');
                        i++;
                        continue;
                    case 'n':
                        result.Add('\n');
                        i++;
                        continue;
                    case 't':
                        result.Add('\t');
                        i++;
                        continue;
                    case 'r':
                        result.Add('\r');
                        i++;
                        continue;
                    case 'b':
                        result.Add('\b');
                        i++;
                        continue;
                    case 'f':
                        result.Add('\f');
                        i++;
                        continue;
                    case 'u' when i + 5 < value.Length:
                    {
                        ReadOnlySpan<char> hex = value.Slice(i + 2, 4);
                        if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            result.Add((char)code);
                            i += 5;
                            continue;
                        }

                        break;
                    }
                    case 'U' when i + 9 < value.Length:
                    {
                        ReadOnlySpan<char> hex = value.Slice(i + 2, 8);
                        if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            result.AddRange(char.ConvertFromUtf32(code));
                            i += 9;
                            continue;
                        }

                        break;
                    }
                }
            }

            if (c == '"')
            {
                break;
            }

            result.Add(c);
        }

        return result;
    }
}
