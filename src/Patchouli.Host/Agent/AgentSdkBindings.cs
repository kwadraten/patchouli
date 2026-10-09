using System.Text;
using System.Text.Json;
using Patchouli.Agent.Sdk;

namespace Patchouli.Host.Agent;

/// <summary>Generate worker DTOs and callable functions from the exact exported wire contracts.</summary>
public static class AgentSdkBindings
{
    public static string Source(IReadOnlyList<ExportedTool> tools, IReadOnlyList<string>? nativeDefinitions = null)
    {
        StringBuilder source = new("module AgentTools =\n    open Patchouli.Agent.Sdk\n");
        int index = 0;

        string Identifier(string name)
        {
            if (name.Contains('`') || name.Contains('\n') || name.Contains('\r'))
            {
                throw new InvalidOperationException("SDK contract contains an invalid F# identifier.");
            }

            return "``" + name + "``";
        }

        string Type(JsonElement schema)
        {
            if (schema.TryGetProperty("anyOf", out JsonElement choices))
            {
                return "(" + Type(choices.EnumerateArray().First(c =>
                    !c.TryGetProperty("type", out JsonElement type) || type.GetString() != "null")) + ") option";
            }

            if (schema.TryGetProperty("oneOf", out JsonElement cases))
            {
                string name = "Contract" + ++index;
                List<string> variants = [];
                foreach (JsonElement item in cases.EnumerateArray())
                {
                    JsonElement properties = item.GetProperty("properties");
                    string variant = Identifier(properties.GetProperty("case").GetProperty("enum")[0].GetString()!);
                    string[] fields = properties.GetProperty("fields").GetProperty("prefixItems").EnumerateArray()
                        .Select(Type).ToArray();
                    variants.Add(variant + (fields.Length == 0
                        ? ""
                        : " of " + string.Join(" * ", fields.Select(f => "(" + f + ")"))));
                }

                source.AppendLine("    [<RequireQualifiedAccess>]");
                source.AppendLine("    type " + name + " = " + string.Join(" | ", variants));
                return name;
            }

            string kind = schema.GetProperty("type").GetString()!;
            if (kind == "string" && schema.TryGetProperty("enum", out JsonElement values))
            {
                string name = "Contract" + ++index;
                source.AppendLine("    [<RequireQualifiedAccess>]");
                source.AppendLine("    type " + name + " = " + string.Join(" | ",
                    values.EnumerateArray().Select(v => Identifier(v.GetString()!))));
                return name;
            }

            if (kind == "object")
            {
                string name = "Contract" + ++index;
                HashSet<string> required = schema.GetProperty("required").EnumerateArray()
                    .Select(field => field.GetString()!).ToHashSet(StringComparer.Ordinal);
                string fields = string.Join("; ", schema.GetProperty("properties").EnumerateObject()
                    .Select(p =>
                        Identifier(p.Name) + ": " + Type(p.Value) + (required.Contains(p.Name) ? "" : " option")));
                source.AppendLine("    type " + name + " = { " + fields + " }");
                return name;
            }

            if (kind == "array")
            {
                return schema.TryGetProperty("prefixItems", out JsonElement elements)
                    ? "(" + string.Join(" * ", elements.EnumerateArray().Select(Type)) + ")"
                    : "(" + Type(schema.GetProperty("items")) + ") list";
            }

            return kind switch
            {
                "string" => "string", "integer" => "int64", "number" => "decimal", "boolean" => "bool",
                "null" => "unit", _ => throw new InvalidOperationException("Unsupported SDK wire type: " + kind)
            };
        }

        foreach (string definition in nativeDefinitions ?? AgentNativeTools.Definitions)
        {
            using JsonDocument document = JsonDocument.Parse(definition);
            JsonElement function = document.RootElement.GetProperty("function");
            string name = function.GetProperty("name").GetString()!;
            if (name == "fsi" || tools.Any(tool => tool.Name == name))
            {
                continue;
            }

            string inputType = Type(function.GetProperty("parameters"));
            source.AppendLine("    let " + Identifier(name) + " (input: " + inputType + ") =");
            source.AppendLine("        let args = System.Text.Json.Nodes.JsonNode.Parse((ValueCodec.create<" +
                              inputType + ">()).Encode input).AsObject()");
            source.AppendLine(
                "        for key in args |> Seq.filter (fun field -> isNull field.Value) |> Seq.map (fun field -> field.Key) |> Seq.toArray do args.Remove key |> ignore");
            source.AppendLine("        Sdk.call " + JsonSerializer.Serialize(name) + " (args.ToJsonString())");
        }

        foreach (ExportedTool tool in tools)
        {
            using JsonDocument schema = JsonDocument.Parse(tool.InputSchema);
            string inputType = Type(schema.RootElement);
            using JsonDocument outputSchema = JsonDocument.Parse(tool.OutputSchema);
            string outputType = Type(outputSchema.RootElement);
            source.AppendLine("    let " + Identifier(tool.Name) + " (input: " + inputType + ") =");
            source.AppendLine("        Sdk.invoke<" + inputType + ", " + outputType + "> " +
                              JsonSerializer.Serialize(tool.Name) + " input");
        }

        return source.ToString();
    }
}
