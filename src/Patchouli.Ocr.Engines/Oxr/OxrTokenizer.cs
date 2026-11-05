using System.Text;
using System.Text.Json;

namespace Patchouli.Ocr.Engines.Oxr;

internal sealed class OxrTokenizer
{
    private static readonly Dictionary<char, byte> CharToByte = CreateByteMap();
    private readonly string?[] _tokens;

    public OxrTokenizer(string tokenizerPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(tokenizerPath));
        JsonElement vocabulary = document.RootElement.GetProperty("model").GetProperty("vocab");
        _tokens = new string?[vocabulary.EnumerateObject().Max(token => token.Value.GetInt32()) + 1];
        foreach (JsonProperty token in vocabulary.EnumerateObject())
        {
            _tokens[token.Value.GetInt32()] = token.Name;
        }
    }

    public string Decode(IEnumerable<long> ids)
    {
        List<byte> bytes = [];
        foreach (long id in ids)
        {
            if (id < 0)
            {
                throw new InvalidDataException("OXR generated an invalid token ID.");
            }

            if (id >= _tokens.Length)
            {
                continue;
            }

            foreach (char character in _tokens[id] ??
                                       throw new InvalidDataException("OXR generated a missing vocabulary token."))
            {
                if (!CharToByte.TryGetValue(character, out byte value))
                {
                    throw new InvalidDataException("OXR generated an invalid byte-level token.");
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static Dictionary<char, byte> CreateByteMap()
    {
        List<int> printable =
        [
            .. Enumerable.Range(33, 94),
            .. Enumerable.Range(161, 12),
            .. Enumerable.Range(174, 82)
        ];
        Dictionary<char, byte> map = new();
        foreach (int value in printable)
        {
            map.Add((char)value, (byte)value);
        }

        int next = 256;
        for (int value = 0; value < 256; value++)
        {
            if (!printable.Contains(value))
            {
                map.Add((char)next++, (byte)value);
            }
        }

        return map;
    }
}
