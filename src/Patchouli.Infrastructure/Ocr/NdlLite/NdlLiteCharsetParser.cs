using System.Globalization;
using System.Text;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Reads the PARSeq <c>model.charset_train</c> string from the official
/// <c>config/NDLmoji.yaml</c>.
/// </summary>
/// <remarks>
/// The upstream loader is <c>list(charobj["model"]["charset_train"])</c>, which in
/// CPython is a list of <b>code points</b>. The NDLOCR-Lite release contains astral
/// characters (for example U+2231E and U+20BB7), so the result must be a list of
/// one string per code point, never a <c>char[]</c> of UTF-16 units: a per-<c>char</c>
/// split would add empty astral slots and shift ~60 trailing vocabulary tokens.
/// Entry <c>i</c> is model class <c>i + 1</c>; class 0 is the CTC/blank stop token.
/// </remarks>
public static class NdlLiteCharsetParser
{
    private const string Key = "charset_train:";

    public static IReadOnlyList<string> Parse(string yamlText)
    {
        ReadOnlySpan<char> text = yamlText.AsSpan();
        int keyIndex = text.IndexOf(Key.AsSpan(), StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            throw new InvalidOperationException("'charset_train:' not found in NDLmoji.yaml.");
        }

        ReadOnlySpan<char> afterKey = text[(keyIndex + Key.Length)..];
        int quoteIndex = afterKey.IndexOf('"');
        if (quoteIndex < 0)
        {
            throw new InvalidOperationException("Expected a quoted charset_train value in NDLmoji.yaml.");
        }

        ReadOnlySpan<char> value = afterKey[(quoteIndex + 1)..];
        List<string> result = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[i + 1];
                switch (next)
                {
                    case '"':
                        result.Add("\"");
                        i++;
                        continue;
                    case '\\':
                        result.Add("\\");
                        i++;
                        continue;
                    case 'n':
                        result.Add("\n");
                        i++;
                        continue;
                    case 't':
                        result.Add("\t");
                        i++;
                        continue;
                    case 'r':
                        result.Add("\r");
                        i++;
                        continue;
                    case 'u' when i + 5 < value.Length:
                    {
                        ReadOnlySpan<char> hex = value.Slice(i + 2, 4);
                        if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            result.Add(((char)code).ToString());
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
                            result.Add(char.ConvertFromUtf32(code));
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

            // Keep one entry per Unicode code point: pair a raw astral character's
            // surrogate halves so the vocabulary stays aligned with upstream.
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                result.Add(new string([c, value[i + 1]]));
                i++;
                continue;
            }

            result.Add(c.ToString());
        }

        return result;
    }
}
