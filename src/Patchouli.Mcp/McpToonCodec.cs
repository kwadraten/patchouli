using System.Text.Json;
using Corvus.Toon;

namespace Patchouli.Mcp;

/// <summary>
/// Production TOON v3 codec for the v3 text surface. Both encoding and decoding are
/// performed exclusively by the MIT <c>Corvus.Toon.SystemTextJson</c> package; Patchouli
/// does not maintain a custom TOON parser, encoder, or semantic string post-processing.
/// The fixed deterministic profile is UTF-8/LF, literal TAB as the tabular delimiter,
/// <see cref="ToonKeyFolding.Off"/>, and strict JSON↔TOON round trips where numbers,
/// booleans, and null keep their JSON types.
/// </summary>
public static class McpToonCodec
{
    /// <summary>The TOON media type used by the v3 text surface.</summary>
    public const string MediaType = "text/toon";

    /// <summary>
    /// Fixed writer profile shared by every TOON projection: literal TAB tabular delimiter,
    /// no key folding, and the Corvus default two-space object indentation. Encoded text is
    /// UTF-8 with LF line endings.
    /// </summary>
    public static ToonWriterOptions WriterOptions { get; } = new()
    {
        Delimiter = ToonDelimiter.Tab,
        KeyFolding = ToonKeyFolding.Off
    };

    /// <summary>
    /// Fixed strict reader profile: structural validation enabled and dotted keys kept as
    /// literal property names, so decoding never alters the JSON data model.
    /// </summary>
    public static ToonReaderOptions ReaderOptions { get; } = new()
    {
        Strict = true,
        ExpandPaths = ToonPathExpansion.Off
    };

    /// <summary>Encodes a value's JSON data model as TOON text using the fixed profile.</summary>
    public static string Encode(object value)
    {
        return ToonDocument.ConvertToToon(JsonSerializer.SerializeToElement(value), WriterOptions);
    }

    /// <summary>Encodes an existing JSON element as TOON text using the fixed profile.</summary>
    public static string Encode(JsonElement element)
    {
        return ToonDocument.ConvertToToon(element, WriterOptions);
    }

    /// <summary>
    /// Strictly decodes TOON text to its canonical JSON projection, preserving numbers,
    /// booleans, null, and string semantics without any custom string post-processing.
    /// </summary>
    public static string DecodeToJson(string toon)
    {
        using JsonDocument document = ToonDocument.Parse(toon, ReaderOptions);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
