using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Patchouli.Mcp;

/// <summary>Converts JSON-shaped MCP values to and from a strict, deterministic YAML subset.</summary>
public static class McpYamlCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex JsonNumber = new(
        @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Encodes a value as deterministic UTF-8-compatible YAML using LF line endings.</summary>
    public static string Encode<T>(T value)
    {
        JsonNode? json = JsonSerializer.SerializeToNode(value, JsonOptions);
        YamlNode yaml = ToYamlNode(json);
        YamlStream stream = new(new YamlDocument(yaml));
        using StringWriter writer = new(CultureInfo.InvariantCulture) { NewLine = "\n" };
        stream.Save(writer, false);
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>Decodes exactly one YAML document into a JSON tree with strict JSON scalar typing.</summary>
    public static JsonNode Decode(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        Parser parser = new(new StringReader(yaml));
        parser.MoveNext();
        Require<StreamStart>(parser);
        parser.MoveNext();
        Require<DocumentStart>(parser);
        parser.MoveNext();
        if (parser.Current is DocumentEnd)
        {
            throw new FormatException("The YAML document must have a root value.");
        }

        JsonNode result = ReadNode(parser);
        if (result is null)
        {
            throw new FormatException("A YAML root value cannot be null.");
        }

        Require<DocumentEnd>(parser);
        parser.MoveNext();
        Require<StreamEnd>(parser);
        return result;
    }

    private static YamlNode ToYamlNode(JsonNode? node)
    {
        if (node is null)
        {
            return new YamlScalarNode("null");
        }

        if (node is JsonObject obj)
        {
            YamlMappingNode mapping = new();
            foreach ((string key, JsonNode? value) in obj)
            {
                mapping.Add(Quoted(key), ToYamlNode(value));
            }

            return mapping;
        }

        if (node is JsonArray array)
        {
            YamlSequenceNode sequence = new();
            foreach (JsonNode? value in array)
            {
                sequence.Add(ToYamlNode(value));
            }

            return sequence;
        }

        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        JsonElement element = document.RootElement;
        return element.ValueKind switch
        {
            JsonValueKind.String => Quoted(element.GetString() ?? string.Empty),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => new YamlScalarNode(
                element.GetRawText()),
            JsonValueKind.Null => new YamlScalarNode("null"),
            _ => throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}.")
        };
    }

    private static YamlScalarNode Quoted(string value)
    {
        return new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
    }

    private static JsonNode ReadNode(Parser parser)
    {
        ParsingEvent current = parser.Current ?? throw new FormatException("Unexpected end of YAML input.");
        switch (current)
        {
            case Scalar scalar:
                ValidateNodeMetadata(scalar.Anchor, scalar.Tag);
                JsonNode scalarNode = ReadScalar(scalar);
                parser.MoveNext();
                return scalarNode;

            case SequenceStart sequenceStart:
                ValidateNodeMetadata(sequenceStart.Anchor, sequenceStart.Tag);
                JsonArray array = new();
                parser.MoveNext();
                while (parser.Current is not SequenceEnd)
                {
                    if (parser.Current is null or StreamEnd or DocumentEnd)
                    {
                        throw new FormatException("Unterminated YAML sequence.");
                    }

                    array.Add(ReadNode(parser));
                }

                parser.MoveNext();
                return array;

            case MappingStart mappingStart:
                ValidateNodeMetadata(mappingStart.Anchor, mappingStart.Tag);
                JsonObject mapping = new();
                HashSet<string> keys = new(StringComparer.Ordinal);
                parser.MoveNext();
                while (parser.Current is not MappingEnd)
                {
                    if (parser.Current is not Scalar keyScalar)
                    {
                        throw new FormatException("YAML mapping keys must be scalar strings.");
                    }

                    ValidateNodeMetadata(keyScalar.Anchor, keyScalar.Tag);
                    if (keyScalar.Style == ScalarStyle.Plain && LooksLikeJsonScalar(keyScalar.Value))
                    {
                        throw new FormatException("YAML mapping keys must be strings.");
                    }

                    string key = keyScalar.Value;
                    if (!keys.Add(key))
                    {
                        throw new FormatException($"Duplicate YAML mapping key '{key}'.");
                    }

                    parser.MoveNext();
                    if (parser.Current is null or MappingEnd or DocumentEnd)
                    {
                        throw new FormatException($"Mapping key '{key}' has no value.");
                    }

                    mapping.Add(key, ReadNode(parser));
                }

                parser.MoveNext();
                return mapping;

            case AnchorAlias:
                throw new FormatException("YAML aliases are not supported.");

            default:
                throw new FormatException($"Unsupported YAML node: {current.GetType().Name}.");
        }
    }

    private static JsonNode ReadScalar(Scalar scalar)
    {
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(scalar.Value)!;
        }

        if (scalar.Value is "null" or "Null" or "NULL" or "~")
        {
            return JsonValue.Create((string?)null)!;
        }

        if (scalar.Value == "true")
        {
            return JsonValue.Create(true);
        }

        if (scalar.Value == "false")
        {
            return JsonValue.Create(false);
        }

        if (JsonNumber.IsMatch(scalar.Value))
        {
            try
            {
                return JsonNode.Parse(scalar.Value) ?? throw new FormatException("Invalid YAML number.");
            }
            catch (JsonException exception)
            {
                throw new FormatException("Invalid YAML JSON number.", exception);
            }
        }

        return JsonValue.Create(scalar.Value)!;
    }

    private static bool LooksLikeJsonScalar(string value)
    {
        return value is "null" or "Null" or "NULL" or "~" or "true" or "false" || JsonNumber.IsMatch(value);
    }

    private static void ValidateNodeMetadata(AnchorName anchor, TagName tag)
    {
        if (!anchor.IsEmpty)
        {
            throw new FormatException("YAML anchors and aliases are not supported.");
        }

        if (!tag.IsEmpty)
        {
            throw new FormatException("Explicit YAML tags are not supported.");
        }
    }

    private static void Require<TEvent>(Parser parser) where TEvent : ParsingEvent
    {
        if (parser.Current is not TEvent)
        {
            throw new FormatException($"Expected YAML {typeof(TEvent).Name}.");
        }
    }
}
