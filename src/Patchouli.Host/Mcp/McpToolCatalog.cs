namespace Patchouli.McpServer;

/// <summary>
/// The published MCP tool manifest as static data. The protocol handler filters this catalog
/// per request by the user's tool switches; the definitions themselves never change at
/// runtime, so they are built once instead of per tools/list or tools/call invocation.
/// </summary>
internal static class McpToolCatalog
{
    internal static readonly McpToolDefinition[] All = Build();

    internal static McpToolDefinition? Find(string name)
    {
        return All.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.Ordinal));
    }

    private static McpToolDefinition[] Build()
    {
        return
        [
            new McpToolDefinition(
                "patchouli.find",
                "Search or browse the Library resource tree and return text-only results.",
                [],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["query"] = McpToolSchemaProperty.String("Search query. Omit to browse the scope."),
                    ["in"] = McpToolSchemaProperty.String("Resource scope URI to search or browse."),
                    ["where"] = McpToolSchemaProperty.Array(
                        "Filter clauses as KEY=VALUE; supported keys: item_type, item_status, primary_document_ocr_index_status, collection_id (exact Collection id), tag (exact, case-sensitive), citable, document_status, source_status, ocr_index_status, style_enabled.",
                        McpToolSchemaProperty.String("KEY=VALUE filter clause.")),
                    ["literal"] = McpToolSchemaProperty.Boolean("Require an exact literal substring match."),
                    ["limit"] = McpToolSchemaProperty.Integer("Maximum results, from 1 through 50."),
                    ["cursor"] = McpToolSchemaProperty.String("Pagination cursor from a previous find response."),
                    ["detail"] = McpToolSchemaProperty.String(
                        "Detailed projection; set to \"long\" to request resource-specific status, relation, and capability metadata."),
                    ["format"] = McpToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new McpToolDefinition(
                "patchouli.fetch",
                "Fetch one or more existing text-only resources by their patchouli:// URIs.",
                ["uris"],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uris"] = McpToolSchemaProperty.Array(
                        "Resource URIs: library.yml, items/<id>.yml, texts/<document-id>/, texts/<document-id>/page-<index>.xml, " +
                        "texts/<document-id>/page-<index>.xml?rev=<tree-revision-id>[&box=<box-id>], texts/<document-id>/map.yml, " +
                        "translations/<document-id>/, translations/<document-id>/page-<index>.xml or csl-styles/<id>.csl.",
                        McpToolSchemaProperty.String("Resource URI.")),
                    ["range"] = McpToolSchemaProperty.String("Optional text slice: lines:S-E or pages:S-E."),
                    ["limit_bytes"] = McpToolSchemaProperty.Integer(
                        "Maximum response bytes per URI; oversized responses return explicit partial content and RESPONSE_TRUNCATED."),
                    ["format"] = McpToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new McpToolDefinition(
                "patchouli.put",
                "Replace one existing writable item bibliography, CSL style, document page map, source page XML, or translation page atomically.",
                ["uri"],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = McpToolSchemaProperty.String(
                        "Writable resource URI: items/<id>.yml, csl-styles/<id>.csl, texts/<document-id>/map.yml, " +
                        "texts/<document-id>/page-<index>.xml, or translations/<document-id>/page-<index>.xml. " +
                        "Legacy .bib/.md addresses remain accepted with a deprecation warning."),
                    ["content"] =
                        McpToolSchemaProperty.String("Complete replacement content; provide this or boxes, not both."),
                    ["source_revision"] =
                        McpToolSchemaProperty.String(
                            "Current source revision required for source XML or translation XML writes."),
                    ["translation_version"] =
                        McpToolSchemaProperty.Integer(
                            "Current translation version required for translation XML writes."),
                    ["boxes"] = McpToolSchemaProperty.Array(
                        "Replace selected boxes with complete <box> XML elements; preserve structural attributes and change only translated state and payload. Provide this or content, not both. Maximum 100 boxes.",
                        McpToolSchemaProperty.Object("One box replacement.",
                            new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                            {
                                ["box_id"] = McpToolSchemaProperty.String("Stable box id."),
                                ["content"] = McpToolSchemaProperty.String(
                                    "Complete <box> XML element with a matching id and replacement payload.")
                            }, ["box_id", "content"]), 1, 100),
                    ["preserve_tags"] =
                        McpToolSchemaProperty.Boolean("Preserve existing tags when replacing an item bibliography."),
                    ["format"] = McpToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                },
                false),
            new McpToolDefinition(
                "patchouli.add",
                "Import a host-accessible LOCAL file (PDF or CSL) into the Library; returns the new or reused " +
                "resource URI. Accepts only local paths, never URLs: download the file with your own ability " +
                "first, then pass its host-local path. The host applies its configured file handling behavior; " +
                "callers cannot override it per request.",
                ["path"],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["path"] = McpToolSchemaProperty.String(
                        "Host-local file path to import. URLs are rejected."),
                    ["kind"] = McpToolSchemaProperty.String(
                        "Optional explicit kind: \"pdf\" or \"csl\". When given it must match the actual file " +
                        "content; omit to content-sniff."),
                    ["format"] = McpToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                },
                false),
            new McpToolDefinition(
                "patchouli.view",
                "Return the source image of a library page or box for model text-image collation. Images come " +
                "from the original PDF, never re-rendered from OCR text. One target, one image per call. " +
                "Recommended collation flow: fetch (text plus version) to read the text, then view the same " +
                "revision or box source image, then put with the original source_revision; if the version " +
                "changed in the meantime the put reports a normal revision conflict instead of overwriting.",
                ["uri"],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["uri"] = McpToolSchemaProperty.String(
                        "Page, box/evidence or translation page/box URI: texts/<document-id>/page-<index>.xml" +
                        "[?rev=<tree-revision-id>[&box=<box-id>]], or the translations/ mirror for the " +
                        "corresponding source-document image."),
                    ["context"] = McpToolSchemaProperty.String(
                        "\"page\" (default) returns the whole page containing the box; \"region\" returns the " +
                        "bbox crop with a small margin."),
                    ["max_edge"] = McpToolSchemaProperty.Integer(
                        "Longest-edge pixel cap, from 64 through 8192; default 2048."),
                    ["format"] = McpToolSchemaProperty.String(
                        "Response encoding: \"toon\" (default) or \"json\". Only affects the text block; the " +
                        "image is always a native image content block.")
                }),
            new McpToolDefinition(
                "patchouli.cite",
                "Render a bibliography for existing item, document, page, or evidence resource URIs using a CSL style.",
                ["refs"],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["refs"] = McpToolSchemaProperty.Array(
                        "Item, document, page, or evidence resource URIs. Evidence URIs use ?rev=<tree-revision-id>[&box=<box-id>].",
                        McpToolSchemaProperty.String("Citation-capable resource URI.")),
                    ["style"] = McpToolSchemaProperty.String(
                        "Optional CSL style URI; omit to use the configured default style."),
                    ["locale"] = McpToolSchemaProperty.String("Optional locale."),
                    ["bibliography"] = McpToolSchemaProperty.Boolean(
                        "Return only the bibliography without inline citations."),
                    ["html"] = McpToolSchemaProperty.Boolean("Include the HTML rendering."),
                    ["format"] = McpToolSchemaProperty.String("Response encoding: \"toon\" (default) or \"json\".")
                }),
            new McpToolDefinition(
                "patchouli.send",
                "Send a typed message to runs/<domain>/control. Fetch the endpoint for message schemas; start accepts a document target, cancel/retry accept a same-domain task target, metadata lookup/apply/enrich accept an item target.",
                ["endpoint", "action", "target", "args"],
                new Dictionary<string, McpToolSchemaProperty>(StringComparer.Ordinal)
                {
                    ["endpoint"] =
                        McpToolSchemaProperty.String(
                            "Domain control endpoint URI, e.g. runs/ocr/control, runs/translation/control, or runs/metadata/control."),
                    ["action"] =
                        McpToolSchemaProperty.String(
                            "Endpoint message action: start, cancel, retry, lookup, apply, enrich, or enrich_batch when supported by the endpoint schema."),
                    ["target"] =
                        McpToolSchemaProperty.String(
                            "Operation target URI: texts/<document-id>/, items/<item-id>.yml, items/, or runs/<domain>/<task-id>/."),
                    ["args"] = McpToolSchemaProperty.Object(
                        "Domain arguments matching the endpoint message schema; use {} when empty."),
                    ["request_id"] =
                        McpToolSchemaProperty.String(
                            "Optional process-local idempotency key, 1 through 256 characters."),
                    ["format"] = McpToolSchemaProperty.String("Response encoding: toon (default) or json.")
                },
                false)
        ];
    }
}

internal sealed record McpToolDefinition(
    string Name,
    string Description,
    string[] Required,
    IReadOnlyDictionary<string, McpToolSchemaProperty> Properties,
    bool ReadOnly = true)
{
    public object ToWire()
    {
        return new
        {
            name = Name,
            description = Description,
            inputSchema = new
            {
                type = "object",
                additionalProperties = false,
                properties = Properties.ToDictionary(pair => pair.Key, pair => pair.Value.ToWire(),
                    StringComparer.Ordinal),
                required = Required
            },
            annotations = new
            {
                readOnlyHint = ReadOnly,
                destructiveHint = Name == "patchouli.send",
                idempotentHint = Name != "patchouli.send",
                openWorldHint = Name == "patchouli.send"
            }
        };
    }
}

internal sealed record McpToolSchemaProperty(
    string Type,
    string Description,
    McpToolSchemaProperty? Items = null,
    IReadOnlyDictionary<string, McpToolSchemaProperty>? Properties = null,
    string[]? Required = null,
    int? MinItems = null,
    int? MaxItems = null)
{
    public object ToWire()
    {
        if (Type == "object" && Properties is null)
        {
            return new { type = Type, description = Description, additionalProperties = true };
        }

        if (Type == "object")
        {
            return new
            {
                type = Type, description = Description, additionalProperties = false,
                properties = Properties!.ToDictionary(pair => pair.Key, pair => pair.Value.ToWire(),
                    StringComparer.Ordinal),
                required = Required!
            };
        }

        if (Type == "array")
        {
            Dictionary<string, object> array = new(StringComparer.Ordinal)
            {
                ["type"] = Type, ["description"] = Description, ["items"] = Items!.ToWire()
            };
            if (MinItems is { } min)
            {
                array["minItems"] = min;
            }

            if (MaxItems is { } max)
            {
                array["maxItems"] = max;
            }

            return array;
        }

        return new { type = Type, description = Description };
    }

    public static McpToolSchemaProperty String(string description)
    {
        return new McpToolSchemaProperty("string", description);
    }

    public static McpToolSchemaProperty Integer(string description)
    {
        return new McpToolSchemaProperty("integer", description);
    }

    public static McpToolSchemaProperty Boolean(string description)
    {
        return new McpToolSchemaProperty("boolean", description);
    }

    public static McpToolSchemaProperty Array(string description, McpToolSchemaProperty items)
    {
        return new McpToolSchemaProperty("array", description, items);
    }

    public static McpToolSchemaProperty Array(string description, McpToolSchemaProperty items, int? minItems,
        int? maxItems)
    {
        return new McpToolSchemaProperty("array", description, items, MinItems: minItems, MaxItems: maxItems);
    }

    public static McpToolSchemaProperty Object(string description)
    {
        return new McpToolSchemaProperty("object", description);
    }

    public static McpToolSchemaProperty Object(string description,
        IReadOnlyDictionary<string, McpToolSchemaProperty> properties, string[] required)
    {
        return new McpToolSchemaProperty("object", description, Properties: properties, Required: required);
    }
}
