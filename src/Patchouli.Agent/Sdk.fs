namespace Patchouli.Agent.Sdk

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
type LibraryDomain = Items | Texts | Translations | CslStyles | Runs | Workflows

[<RequireQualifiedAccess>]
type BrowseScope = Root | Domain of LibraryDomain | DocumentTexts of documentId: string

[<RequireQualifiedAccess>]
type ReadableResource =
    | Uri of string
    | SourcePage of documentId: string * page: int
    | TranslationPage of documentId: string * page: int
    | ItemBibliography of itemId: string
    | CslStyle of styleId: string
    | LibrarySummary

[<RequireQualifiedAccess>]
type WritableResource =
    | TranslationPage of documentId: string * page: int
    | ItemBibliography of itemId: string
    | CslStyle of styleId: string

[<RequireQualifiedAccess>]
type SdkErrorKind = Validation | Permission | Conflict | Retryable | Unknown

/// Canonical invocation result; payload is the existing lossless domain envelope.
type SdkResult =
    { Succeeded: bool
      Payload: string
      ErrorCode: string
      OperationId: string }

type SdkObservation =
    { Name: string; Arguments: string; Payload: string; Succeeded: bool; OperationId: string; ErrorCode: string }

type SdkCallException(result: SdkResult) =
    inherit Exception(result.ErrorCode + ": " + result.Payload)
    member _.Result = result

/// The same invocation boundary serves registered native functions and worker SDK functions.
type ISdkInvoker =
    abstract InvokeAsync: name: string * arguments: string -> Task<SdkResult>

[<RequireQualifiedAccess>]
module LibraryUri =
    let private part (value: string) =
        if String.IsNullOrWhiteSpace value then invalidArg "identifier" "A resource identifier cannot be empty."
        Uri.EscapeDataString value

    let domain = function
        | LibraryDomain.Items -> "items" | LibraryDomain.Texts -> "texts"
        | LibraryDomain.Translations -> "translations" | LibraryDomain.CslStyles -> "csl-styles"
        | LibraryDomain.Runs -> "runs" | LibraryDomain.Workflows -> "workflows"

    let scope = function
        | BrowseScope.Root -> "patchouli://"
        | BrowseScope.Domain value -> "patchouli://" + domain value + "/"
        | BrowseScope.DocumentTexts document -> "patchouli://texts/" + part document + "/"

    let private page domain document index =
        if index < 1 then invalidArg "page" "A page index must be positive."
        $"patchouli://{domain}/{part document}/page-{index}.md"

    let readable = function
        | ReadableResource.Uri uri ->
            if String.IsNullOrWhiteSpace uri then invalidArg "uri" "A resource URI cannot be empty."
            uri
        | ReadableResource.SourcePage(document, index) -> page "texts" document index
        | ReadableResource.TranslationPage(document, index) -> page "translations" document index
        | ReadableResource.ItemBibliography item -> "patchouli://items/" + part item + ".bib"
        | ReadableResource.CslStyle style -> "patchouli://csl-styles/" + part style + ".csl"
        | ReadableResource.LibrarySummary -> "patchouli://library.toon"

    let writable = function
        | WritableResource.TranslationPage(document, index) -> page "translations" document index
        | WritableResource.ItemBibliography item -> "patchouli://items/" + part item + ".bib"
        | WritableResource.CslStyle style -> "patchouli://csl-styles/" + part style + ".csl"

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Sdk =
    let private ambient = AsyncLocal<ISdkInvoker>()

    /// Installed by the host/worker, never serialized as workflow state.
    let useInvoker (invoker: ISdkInvoker) =
        let previous = ambient.Value
        ambient.Value <- invoker
        { new IDisposable with member _.Dispose() = ambient.Value <- previous }

    let call name arguments =
        if isNull (box ambient.Value) then invalidOp "SDK_NOT_INSTALLED: no active invocation scope."
        ambient.Value.InvokeAsync(name, arguments)

    let requireSuccess (result: SdkResult) =
        if not result.Succeeded then raise (SdkCallException result)
        result.Payload

    let invoke<'i, 'o> name (input: 'i) = task {
        let! result = call name ((ValueCodec.create<'i>()).Encode input)
        return (ValueCodec.create<'o>()).Decode(requireSuccess result) }

    let find scope = call "find" (JsonSerializer.Serialize {| ``in`` = LibraryUri.scope scope |})
    let fetch targets = call "fetch" (JsonSerializer.Serialize {| uris = targets |> List.map LibraryUri.readable |})
    let put target content = call "put" (JsonSerializer.Serialize {| uri = LibraryUri.writable target; content = content |})

/// An exported function retains its typed codecs and implementation. Native is only a projection.
[<Sealed>]
type ExportedTool internal
    (name: string, description: string, inputSchema: string, outputSchema: string, identity: string,
     inputType: Type, outputType: Type, capabilities: Set<string>, invoke: string -> Task<string>) =
    member _.Name = name
    member _.Description = description
    member _.InputSchema = inputSchema
    member _.OutputSchema = outputSchema
    member _.Identity = identity
    member _.InputType = inputType
    member _.OutputType = outputType
    member _.Capabilities = capabilities
    member _.InvokeAsync arguments = invoke arguments
    member _.NativeDefinition =
        let func = JsonObject()
        func["name"] <- JsonValue.Create name
        func["description"] <- JsonValue.Create description
        func["parameters"] <- JsonNode.Parse inputSchema
        let json = JsonObject()
        json["type"] <- JsonValue.Create "function"
        json["function"] <- func
        json.ToJsonString()

/// Cold immutable tool description; ordinary F# functions remain the implementation language.
[<Sealed>]
type Tool<'input, 'output> internal
    (name: string, description: string, invoke: 'input -> Task<'output>, identity: string, capabilities: Set<string>) =
    member _.Name = name
    member internal _.Description = description
    member internal _.Invoke input = invoke input
    member internal _.Identity = identity
    member internal _.Capabilities = capabilities

[<RequireQualifiedAccess>]
module Tool =
    let create (name: string) description invoke : Tool<'i, 'o> =
        if not (System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,63}$")) then
            invalidArg "name" "Tool names must be stable identifiers."
        Tool(name, description, invoke, name, Set.empty)

    /// Declare the primitives required by this function; the host's permissions still apply.
    let withCapabilities capabilities (tool: Tool<'i, 'o>) =
        Tool(tool.Name, tool.Description, tool.Invoke, tool.Identity, Set.ofList capabilities)

    /// Include an implementation version in durable receipt identity.
    let withVersion version (tool: Tool<'i, 'o>) =
        Tool(tool.Name, tool.Description, tool.Invoke, tool.Identity + "/" + version, tool.Capabilities)

    let map transform (tool: Tool<'i, 'o>) =
        Tool(tool.Name, tool.Description, (fun input -> task { let! value = tool.Invoke input
                                                           return transform value }), tool.Identity, tool.Capabilities)

    let compose (next: Tool<'a, 'o>) (previous: Tool<'i, 'a>) =
        Tool(previous.Name, previous.Description, (fun input -> task { let! value = previous.Invoke input
                                                                    return! next.Invoke value }), previous.Identity + "/" + next.Identity, Set.union previous.Capabilities next.Capabilities)

    /// The frozen binding is part of identity, not an overridable model argument.
    let bind boundValue (tool: Tool<'bound * 'i, 'o>) =
        let codec = ValueCodec.create<'bound>()
        let binding = codec.Encode boundValue
        Tool(tool.Name, tool.Description, (fun input -> tool.Invoke(boundValue, input)),
             tool.Identity + "/bind/" + codec.Identity + "/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes binding)), tool.Capabilities)

    let exportWith (input: ValueCodec<'i>) (output: ValueCodec<'o>) (tool: Tool<'i, 'o>) =
        use inputDocument = System.Text.Json.JsonDocument.Parse(input.Schema)
        if not (inputDocument.RootElement.TryGetProperty("type") |> function true, value -> value.GetString() = "object" | _ -> false) then
            invalidArg "tool" "Native tool input must be an immutable record. Bind static tuple parameters first."
        ExportedTool(tool.Name, tool.Description, input.Schema, output.Schema, tool.Identity + "/" + input.Identity + "/" + output.Identity,
            typeof<'i>, typeof<'o>, tool.Capabilities, fun arguments -> task {
                let decoded = input.Decode arguments
                let! result = tool.Invoke decoded
                return output.Encode result })

    let export (tool: Tool<'i, 'o>) = exportWith (ValueCodec.create<'i>()) (ValueCodec.create<'o>()) tool

/// Standard statically bound translation capability. Target is absent from model arguments.
type TranslationContent = { content: string }

[<RequireQualifiedAccess>]
module LibraryTools =
    let commitTranslation =
        Tool.create "commitTranslation" "Commit the current workflow page; provide only its translated Markdown." (fun (target, input: TranslationContent) -> task {
            let! result = Sdk.put target input.content
            return Sdk.requireSuccess result })
        |> Tool.withCapabilities [ "put" ]
        |> Tool.withVersion "1"
