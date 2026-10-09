namespace Patchouli.Agent

open System.Text.Json

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AssistantReplies =
    let toJson (reply: AssistantReply) =
        JsonSerializer.Serialize {| text = reply.Text; model = reply.Model; finish = string reply.Finish; metadata = reply.Metadata
                                    toolCalls = reply.ToolCalls |> List.map (fun call -> {| id = call.Id; name = call.Name; arguments = call.Arguments; metadata = call.Metadata |}) |> List.toArray |}

    let fromJson (text: string) : AssistantReply =
        use doc = JsonDocument.Parse text
        let root = doc.RootElement
        let value (name: string) (element: JsonElement) = element.GetProperty(name).GetString() |> function null -> "" | text -> text
        let metadata (element: JsonElement) = element.EnumerateObject() |> Seq.tryFind (fun property -> property.Name = "metadata")
                                            |> Option.map (fun property -> property.Value.GetString() |> function null -> "" | text -> text) |> Option.defaultValue ""
        { Text = value "text" root; Model = value "model" root
          Finish = match value "finish" root with "Tools" -> AssistantFinish.Tools | "Truncated" -> AssistantFinish.Truncated | _ -> AssistantFinish.Complete
          ToolCalls = root.GetProperty("toolCalls").EnumerateArray()
                      |> Seq.map (fun call -> { Id = value "id" call; Name = value "name" call; Arguments = value "arguments" call; Metadata = metadata call }) |> Seq.toList
          Metadata = metadata root }
