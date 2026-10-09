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

    private static string BuildInstructions()
    {
        StringBuilder text = new(ProtocolInstructions);
        text.AppendLine().AppendLine("Tool parameter contracts in F# (generated from the declared native schemas):");
        List<string> cases = [];
        foreach (string definition in Definitions)
        {
            JsonObject function = JsonNode.Parse(definition)!["function"]!.AsObject();
            string name = function["name"]!.GetValue<string>();
            string caseName = char.ToUpperInvariant(name[0]) + name[1..];
            string type = caseName + "Args";
            JsonObject parameters = function["parameters"]!.AsObject();
            HashSet<string> required = parameters["required"]!.AsArray()
                .Select(field => field!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            string fields = string.Join("; ", parameters["properties"]!.AsObject().Select(field =>
                "``" + field.Key + "``: " + FSharpType(field.Value!) +
                (required.Contains(field.Key) ? "" : " option")));
            text.AppendLine("type " + type + " = { " + fields + " }");
            cases.Add(caseName + " of " + type);
        }

        text.AppendLine("type ToolCall = " + string.Join(" | ", cases));
        text.AppendLine("These F# types describe native function arguments. Send JSON objects matching the records, " +
                        "not F# record syntax or a {tool,arguments} envelope. Lists are JSON arrays, bool is true/false, " +
                        "int64 is an integer. For option fields, omit None; encode Some x as x, never null or {Some:...}. " +
                        "Do not invent fields. F# source belongs only inside fsi.code. After a parsing/type failure, " +
                        "use the returned diagnostic to correct the next native call within the context's retry limit.");
        text.AppendLine("Actual FSI SDK (already installed; open Patchouli.Agent.Sdk):");
        text.AppendLine("type LibraryDomain = Items | Texts | Translations | CslStyles | Runs | Workflows");
        text.AppendLine("type BrowseScope = Root | Domain of LibraryDomain | DocumentTexts of string");
        text.AppendLine(
            "type ReadableResource = Uri of string | SourcePage of string * int | TranslationPage of string * int | ItemBibliography of string | CslStyle of string | LibrarySummary");
        text.AppendLine(
            "type WritableResource = TranslationPage of string * int | ItemBibliography of string | CslStyle of string");
        text.AppendLine(
            "val Sdk.find: BrowseScope -> Task<SdkResult>\nval Sdk.fetch: ReadableResource list -> Task<SdkResult>\nval Sdk.put: WritableResource -> string -> Task<SdkResult>");
        text.AppendLine(
            "SdkResult has Succeeded, Payload, ErrorCode, OperationId. Sdk.requireSuccess returns the lossless domain JSON or raises SdkCallException. For all other declared native arguments use Sdk.call name json. SDK operations use the same permission, validation and receipt path as native calls; each nested operation is recorded even when code catches its exception. Compile errors execute no SDK operations. Temporary FSI bindings are not checkpoints.");
        text.AppendLine(
            "F# example: let r = Sdk.find (BrowseScope.Domain LibraryDomain.Texts) |> Async.AwaitTask |> Async.RunSynchronously\nprintfn \"%s\" (Sdk.requireSuccess r)");
        text.AppendLine(
            "Typed FSI bindings generated from the same native schemas; AgentTools functions omit optional None fields on the JSON wire:");
        text.AppendLine(AgentSdkBindings.Source([]));
        text.AppendLine("Native function examples (name => arguments):");
        text.AppendLine("""find => {"in":"patchouli://texts/document-id/","limit":10}""");
        text.AppendLine("""fetch => {"uris":["patchouli://texts/document-id/page-1.md"]}""");
        text.AppendLine(
            """put => {"uri":"patchouli://translations/document-id/page-1.md","content":"# Translated page\n..."}""");
        text.AppendLine("""cite => {"refs":["patchouli://items/item-id.bib"]}""");
        text.AppendLine("""fsi => {"code":"printfn \"%d\" (1 + 1)"}""");
        return text.ToString();
    }

    private static string FSharpType(JsonNode schema)
    {
        return schema["type"]!.GetValue<string>() switch
        {
            "string" => "string",
            "integer" => "int64",
            "boolean" => "bool",
            "array" => FSharpType(schema["items"]!) + " list",
            string other => throw new InvalidOperationException("Unsupported tool parameter type: " + other)
        };
    }
}
