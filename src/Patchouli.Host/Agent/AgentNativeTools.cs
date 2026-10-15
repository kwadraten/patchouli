using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Patchouli.McpServer;

namespace Patchouli.Host.Agent;

/// <summary>Stable native tool declarations shared by chat and workflow requests.</summary>
public static class AgentNativeTools
{
    private const string ProtocolInstructions =
        "Respond to the user in the language of their question. " +
        "Use the declared native function tools. Assistant text is only explanation or a final reply. " +
        "This supersedes legacy instructions asking for JSON tool calls in text. Do not embed executable calls in prose. " +
        "Keep text and function calls separate. Await all tool results; fix typed argument errors using their actual diagnostics. " +
        "A response without function calls completes the current turn. FSI is available in every session and uses its temporary workspace. " +
        "When context has been summarized, use history to search or page through your original transcript and recover needed details.";

    public static IReadOnlyList<string> Definitions { get; } = McpProtocolHandler.AgentToolDefinitions()
        .Append(AgentHistoryTool.Definition)
        .Append(
            """{"type":"function","function":{"name":"fsi","description":"Evaluate F# in the session-local independent FSI process; print values with printfn.","parameters":{"type":"object","properties":{"code":{"type":"string","description":"Non-empty F# source"}},"required":["code"],"additionalProperties":false}}}""")
        .ToArray();

    public static string Instructions { get; } = BuildInstructions();

    /// <summary>Builds contracts only for the native functions present in one activated scope.</summary>
    public static string InstructionsFor(IReadOnlyList<string> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        StringBuilder text = new(ProtocolInstructions);
        text.AppendLine().AppendLine("Tool parameter contracts in F# (generated from the active native schemas):");
        List<string> cases = [];
        foreach (string definition in definitions)
        {
            JsonObject function = JsonNode.Parse(definition)?["function"]?.AsObject() ??
                                  throw new InvalidDataException("An active agent tool has no function schema.");
            string name = function["name"]?.GetValue<string>() ??
                          throw new InvalidDataException("An active agent tool has no name.");
            string caseName = char.ToUpperInvariant(name[0]) + name[1..];
            string type = caseName + "Args";
            JsonObject parameters = function["parameters"]?.AsObject() ?? new JsonObject();
            HashSet<string> required = parameters["required"]?.AsArray().Select(field => field!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal) ?? [];
            JsonObject properties = parameters["properties"]?.AsObject() ?? new JsonObject();

            string FSharpType(JsonNode schema, string fieldName)
            {
                string type = schema["type"]!.GetValue<string>() switch
                {
                    "string" => "string",
                    "integer" => "int64",
                    "boolean" => "bool",
                    "array" => FSharpType(schema["items"]!, fieldName + "Item") + " list",
                    "object" => BuildObject(schema, fieldName),
                    string other => throw new InvalidOperationException("Unsupported tool parameter type: " + other)
                };
                return type;
            }

            string BuildObject(JsonNode schema, string name)
            {
                JsonObject objectProperties = schema["properties"]?.AsObject() ?? new JsonObject();
                HashSet<string> objectRequired = schema["required"]?.AsArray()
                    .Select(field => field!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
                string nestedName = type + "_" + char.ToUpperInvariant(name[0]) + name[1..];
                string nestedFields = string.Join("; ", objectProperties.Select(field =>
                    "``" + field.Key + "``: " + FSharpType(field.Value!, field.Key) +
                    (objectRequired.Contains(field.Key) ? "" : " option")));
                text.AppendLine("type " + nestedName + " = { " + nestedFields + " }");
                return nestedName;
            }

            string fields = string.Join("; ", properties.Select(field =>
                "``" + field.Key + "``: " + FSharpType(field.Value!, field.Key) +
                (required.Contains(field.Key) ? "" : " option")));
            text.AppendLine("type " + type + " = { " + fields + " }");
            cases.Add(caseName + " of " + type);
        }

        if (cases.Count > 0)
        {
            text.AppendLine("type ToolCall = " + string.Join(" | ", cases));
        }

        text.AppendLine(
            "These types describe only the active native function arguments. Send JSON objects matching the " +
            "records, not F# record syntax or a {tool,arguments} envelope. Lists are JSON arrays, bool is " +
            "true/false, int64 is an integer. For option fields, omit None; encode Some x as x, never " +
            "null or {Some:...}. Do not invent fields. F# source belongs only inside fsi.code. After a " +
            "parsing/type failure, use the returned diagnostic to correct the next native call.");
        return text.ToString();
    }

    private static string BuildInstructions()
    {
        StringBuilder text = new(InstructionsFor(Definitions));
        text.AppendLine(
            "Actual FSI SDK (already installed; open Patchouli.Agent.Sdk). Resource constructors generate canonical .xml page, .yml item, and library.yml links; enum names remain stable for existing scripts.");
        text.AppendLine("type LibraryDomain = Items | Texts | Translations | CslStyles | Runs | Workflows");
        text.AppendLine("type BrowseScope = Root | Domain of LibraryDomain | DocumentTexts of string");
        text.AppendLine(
            "type ReadableResource = Uri of string | SourcePage of string * int | TranslationPage of string * int | ItemBibliography of string | CslStyle of string | LibrarySummary");
        text.AppendLine(
            "type WritableResource = TranslationPage of string * int | SourcePageXml of string * int | TranslationPageXml of string * int | ItemBibliography of string | CslStyle of string");
        text.AppendLine(
            "val Sdk.find: BrowseScope -> Task<SdkResult>\nval Sdk.fetch: ReadableResource list -> Task<SdkResult>\nval Sdk.put: WritableResource -> string -> Task<SdkResult>\nval Sdk.putBoxes: string -> string -> int option -> (string * string) list -> Task<SdkResult>");
        text.AppendLine(
            "For Sdk.putBoxes and LibraryTools.commitTranslation, each box content is a complete <box> XML element copied from the fetched page. Keep the id and structural attributes unchanged; edit only translated state and payload text.");
        text.AppendLine(
            "SdkResult has Succeeded, Payload, ErrorCode, OperationId. Sdk.requireSuccess returns the lossless domain JSON or raises SdkCallException. For all other declared native arguments use Sdk.call name json. SDK operations use the same permission, validation and receipt path as native calls; each nested operation is recorded even when code catches its exception. Compile errors execute no SDK operations. Temporary FSI bindings are not checkpoints.");
        text.AppendLine(
            "F# example: let r = Sdk.find (BrowseScope.Domain LibraryDomain.Texts) |> Async.AwaitTask |> Async.RunSynchronously\nprintfn \"%s\" (Sdk.requireSuccess r)");
        text.AppendLine(
            "Typed FSI bindings generated from the same native schemas; AgentTools functions omit optional None fields on the JSON wire:");
        text.AppendLine(AgentSdkBindings.Source([]));
        text.AppendLine("Native function examples (name => arguments):");
        text.AppendLine("""find => {"in":"patchouli://texts/document-id/","limit":10}""");
        text.AppendLine("""fetch => {"uris":["patchouli://texts/document-id/page-1.xml"]}""");
        text.AppendLine(
            """put => {"uri":"patchouli://translations/document-id/page-1.xml","source_revision":"rev-id","translation_version":1,"boxes":[{"box_id":"box-id","content":"<box id=\"box-id\" type=\"text\" sub_type=\"\" base_type=\"\" heading_level=\"\" code_language=\"\" suppressed=\"false\" readonly=\"false\" translated=\"true\"><payload format=\"markdown\">Translated text</payload></box>"}]}""");
        text.AppendLine("""cite => {"refs":["patchouli://items/item-id.yml"]}""");
        text.AppendLine("""fsi => {"code":"printfn \"%d\" (1 + 1)"}""");
        return text.ToString();
    }
}
