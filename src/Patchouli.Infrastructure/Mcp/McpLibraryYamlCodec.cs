using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using YamlDotNet.Core;

namespace Patchouli.Infrastructure.Mcp;

public static class McpLibraryYamlCodec
{
    public static Result<LibraryCatalogUpdate> Decode(string content, bool preserveTags, bool preserveCollections)
    {
        try
        {
            JsonObject root = McpYamlCodec.Decode(content) as JsonObject ??
                              throw new FormatException("Library YAML must be a mapping.");
            Fields(root, ["library_id", "display_name", "tags", "collections"]);
            string libraryId = Text(root, "library_id");
            string? displayName = root.ContainsKey("display_name") ? Text(root, "display_name") : null;
            List<string>? tags = null;
            if (!preserveTags && root.ContainsKey("tags"))
            {
                tags = [];
                foreach (JsonNode? node in Array(root, "tags"))
                {
                    JsonObject tag = node as JsonObject ??
                                     throw new FormatException("Each tag must be a mapping containing tag.");
                    Fields(tag, ["tag", "item_count"]);
                    tags.Add(Text(tag, "tag"));
                }
            }

            List<LibraryCollectionDefinition>? collections = null;
            if (!preserveCollections && root.ContainsKey("collections"))
            {
                collections = [];
                foreach (JsonNode? node in Array(root, "collections"))
                {
                    JsonObject collection = node as JsonObject ??
                                            throw new FormatException("Each collection must be a mapping.");
                    Fields(collection, ["collection_id", "name", "item_count"]);
                    CollectionId? id = null;
                    if (collection["collection_id"] is JsonValue suppliedId &&
                        suppliedId.TryGetValue(out string? idText) && Guid.TryParse(idText, out Guid value))
                    {
                        id = new CollectionId(value);
                    }

                    collections.Add(new LibraryCollectionDefinition(id, Text(collection, "name")));
                }
            }

            return Result<LibraryCatalogUpdate>.Success(new LibraryCatalogUpdate(libraryId, displayName, tags,
                collections));
        }
        catch (FormatException exception)
        {
            return Result<LibraryCatalogUpdate>.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Result<LibraryCatalogUpdate>.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
        catch (YamlException exception)
        {
            return Result<LibraryCatalogUpdate>.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
        catch (JsonException exception)
        {
            return Result<LibraryCatalogUpdate>.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
    }

    private static string Text(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new FormatException($"{name} must be a nonempty string.");
    }

    private static JsonArray Array(JsonObject obj, string name)
    {
        return obj[name] as JsonArray ?? throw new FormatException($"{name} must be an array; use [] to clear it.");
    }

    private static void Fields(JsonObject obj, IReadOnlyList<string> allowed)
    {
        if (obj.Any(property => !allowed.Contains(property.Key, StringComparer.Ordinal)))
        {
            throw new FormatException("Library YAML contains an unsupported field.");
        }
    }
}
