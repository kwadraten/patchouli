using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Patchouli.Ocr.Engines.OvisOcr3;

internal sealed class OvisOcr3Tokenizer
{
    private static readonly Regex PreTokenPattern = new(
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?[\p{L}\p{M}]+|\p{N}| ?[^\s\p{L}\p{M}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] ByteToChar = CreateByteMap();

    private static readonly Dictionary<char, byte> CharToByte =
        Enumerable.Range(0, 256).ToDictionary(index => ByteToChar[index], index => (byte)index);

    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<(string Left, string Right), int> _merges;
    private readonly string[] _tokens;

    public OvisOcr3Tokenizer(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement model = document.RootElement.GetProperty("model");
        _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty token in model.GetProperty("vocab").EnumerateObject())
        {
            _vocabulary.Add(token.Name, token.Value.GetInt32());
        }

        _tokens = new string[_vocabulary.Count];
        foreach ((string token, int id) in _vocabulary)
        {
            _tokens[id] = token;
        }

        _merges = new Dictionary<(string Left, string Right), int>();
        int rank = 0;
        foreach (JsonElement merge in model.GetProperty("merges").EnumerateArray())
        {
            string[] pair = merge.GetString()!.Split(' ', 2);
            if (pair.Length != 2)
            {
                throw new InvalidDataException("The OvisOCR3 tokenizer has an invalid BPE merge.");
            }

            _merges.Add((pair[0], pair[1]), rank++);
        }
    }

    public long[] EncodePrompt(string prompt, int imageTokenCount)
    {
        if (imageTokenCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageTokenCount));
        }

        List<long> ids = [248045];
        ids.AddRange(Encode("user\n"));
        ids.Add(248053);
        ids.AddRange(Enumerable.Repeat(248056L, imageTokenCount));
        ids.Add(248054);
        ids.AddRange(Encode(prompt));
        ids.Add(248046);
        ids.AddRange(Encode("\n"));
        ids.Add(248045);
        ids.AddRange(Encode("assistant\n"));
        ids.Add(248068);
        ids.AddRange(Encode("\n\n"));
        ids.Add(248069);
        ids.AddRange(Encode("\n\n"));
        return ids.ToArray();
    }

    public string Decode(IEnumerable<long> ids)
    {
        List<byte> bytes = [];
        foreach (long id in ids)
        {
            if (id >= _tokens.Length)
            {
                continue;
            }

            if (id < 0)
            {
                throw new InvalidDataException("OvisOCR3 produced an invalid token ID.");
            }

            foreach (char character in _tokens[id])
            {
                if (!CharToByte.TryGetValue(character, out byte value))
                {
                    throw new InvalidDataException("OvisOCR3 produced an invalid byte-level token.");
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private IEnumerable<long> Encode(string text)
    {
        foreach (Match match in PreTokenPattern.Matches(text))
        {
            StringBuilder encoded = new();
            foreach (byte value in Encoding.UTF8.GetBytes(match.Value))
            {
                encoded.Append(ByteToChar[value]);
            }

            List<string> pieces = encoded.ToString().Select(character => character.ToString()).ToList();
            while (pieces.Count > 1)
            {
                int bestRank = int.MaxValue;
                int bestIndex = -1;
                for (int index = 0; index < pieces.Count - 1; index++)
                {
                    if (_merges.TryGetValue((pieces[index], pieces[index + 1]), out int rank) && rank < bestRank)
                    {
                        bestRank = rank;
                        bestIndex = index;
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                pieces[bestIndex] += pieces[bestIndex + 1];
                pieces.RemoveAt(bestIndex + 1);
            }

            foreach (string piece in pieces)
            {
                if (!_vocabulary.TryGetValue(piece, out int id))
                {
                    throw new InvalidDataException("The OvisOCR3 tokenizer is missing a BPE token.");
                }

                yield return id;
            }
        }
    }

    private static char[] CreateByteMap()
    {
        List<int> values =
        [
            .. Enumerable.Range('!', '~' - '!' + 1),
            .. Enumerable.Range('¡', '¬' - '¡' + 1),
            .. Enumerable.Range('®', 'ÿ' - '®' + 1)
        ];
        char[] result = new char[256];
        for (int index = 0; index < values.Count; index++)
        {
            result[values[index]] = (char)values[index];
        }

        int next = 256;
        for (int value = 0; value < 256; value++)
        {
            if (!values.Contains(value))
            {
                result[value] = (char)next++;
            }
        }

        return result;
    }
}
