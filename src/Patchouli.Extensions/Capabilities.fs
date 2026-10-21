namespace Patchouli.Extensions.Sdk

open System
open Patchouli.Extensions.Contracts

[<RequireQualifiedAccess>]
module Domains =
    [<Literal>]
    let translation = "translation"

    [<Literal>]
    let metadata = "metadata"

[<RequireQualifiedAccess>]
module CapabilityKinds =
    [<Literal>]
    let llm = "llm"

    [<Literal>]
    let http = "http"

[<Sealed>]
type CapabilityDescriptor<'T> internal (name: string, resolve: IExtensionCapabilities -> 'T) =
    member _.Name = name
    member internal _.Resolve(capabilities) = resolve capabilities

[<RequireQualifiedAccess>]
module Capability =
    let llmHandle = CapabilityDescriptor("llm", fun (capabilities: IExtensionCapabilities) -> capabilities.Llm)
    let httpHandle = CapabilityDescriptor("http", fun (capabilities: IExtensionCapabilities) -> capabilities.Http)

    let llm label =
        CapabilityDeclaration<ExtensionModelSelection option>(
            ConfigFieldDescriptor("", label, ConfigValueKind.Model, false, "", "", Nullable(), Nullable(), null),
            (fun value ->
                if String.IsNullOrWhiteSpace value then None
                else
                    match System.Text.Json.JsonSerializer.Deserialize<ExtensionModelSelection>(value,
                        System.Text.Json.JsonSerializerOptions(PropertyNameCaseInsensitive = true)) |> Option.ofObj with
                    | None -> None
                    | Some selection when String.IsNullOrWhiteSpace selection.ProviderId && String.IsNullOrWhiteSpace selection.Model -> None
                    | Some selection -> Some selection),
            ExtensionCapabilityKind.Llm,
            None)

    let http label =
        CapabilityDeclaration<unit>(
            ConfigFieldDescriptor("", label, ConfigValueKind.Capability, false, "", null, Nullable(), Nullable(), null),
            (fun _ -> ()),
            ExtensionCapabilityKind.Http,
            None)

    let followDefault (domain: string) (declaration: CapabilityDeclaration<'T>) =
        if String.IsNullOrWhiteSpace domain then invalidArg "domain" "A default model domain is required."
        CapabilityDeclaration<'T>(declaration.Descriptor, declaration.Decode, declaration.RequestKind, Some domain)

    let resolveLlm (binding: Binding<ExtensionModelSelection option>)
                   (config: ExtensionConfigSnapshot)
                   (capabilities: IExtensionCapabilities) : ILlmCapability =
        match binding.CapabilityRequest with
        | Some request when request.Kind = ExtensionCapabilityKind.Llm ->
            let selection = Binding.get binding config |> Option.toObj
            capabilities.ResolveLlmSlot(request.Key, request.DefaultDomain, selection)
        | _ -> invalidArg "binding" "The binding does not declare an LLM capability request."

    let resolveHttp (binding: Binding<unit>)
                    (config: ExtensionConfigSnapshot)
                    (capabilities: IExtensionCapabilities) : IHttpCapability =
        match binding.CapabilityRequest with
        | Some request when request.Kind = ExtensionCapabilityKind.Http ->
            capabilities.ResolveHttpSlot(request.Key, request.DefaultDomain, config)
        | _ -> invalidArg "binding" "The binding does not declare an HTTP capability request."

    let get (capabilities: IExtensionCapabilities) (descriptor: CapabilityDescriptor<'T>) =
        if isNull (box capabilities) then nullArg "capabilities"
        if isNull (box descriptor) then nullArg "descriptor"
        descriptor.Resolve(capabilities)
