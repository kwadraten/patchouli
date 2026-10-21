#load "Support.fsx"
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

let endpointBinding = Config.bind "endpoint" (Field.text "DeepLX translation URL" "http://127.0.0.1:1188/translate"
    |> Field.describe "Full URL for a self-hosted or trusted DeepLX-compatible /translate endpoint.")
let credentialBinding = Config.bind "credential-reference" (Field.credentialReference "DeepLX API credential")
let httpAccessBinding = Config.bind "http" (Capability.http "DeepLX network access")
let configFields = Config.fields (sharedBindings @ [ Config.asField endpointBinding; Config.asField credentialBinding ])
let descriptor = ExtensionDescriptor("translation.deeplx", "DeepLX", "1.0.0", "Sends box text to a configurable DeepLX-compatible translation endpoint.")

let translateOne (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (http: IHttpCapability) (endpoint: string) (credentialReference: string) (segment: Segment) cancellationToken = task {
    let text, tokens = protect segment.Id segment.Text
    let source = sourceLanguage context
    let body = jsonBody {| text = text; source_lang = source; target_lang = targetLanguage context |}
    let options = effectiveOptions context
    let send (token: CancellationToken) = sendJson capabilities http endpoint body options token credentialReference []
    let! response = retryTransient options.MaxRetries (TimeSpan.FromMilliseconds(350.0)) (TimeSpan.FromSeconds(5.0)) send cancellationToken
    use document = JsonDocument.Parse(response)
    let mutable data = JsonElement()
    if not (document.RootElement.TryGetProperty("data", &data)) || data.ValueKind <> JsonValueKind.String then
        invalidOp "DeepLX response has no translated data field."
    let translatedText = data.GetString() |> Option.ofObj |> Option.defaultValue String.Empty
    if String.IsNullOrWhiteSpace translatedText then invalidOp "DeepLX returned empty text."
    return translated segment.Id (restore translatedText tokens)
}

let translate (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (cancellationToken: CancellationToken) = task {
    let endpoint = validateHttpEndpoint (Binding.get endpointBinding context.Config)
    let credentialReference =
        match context.Config.Values.TryGetValue("credential-reference") with
        | true, value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
        | _ -> String.Empty
    let http = Capability.resolveHttp httpAccessBinding context.Config capabilities
    let translateSegment : Segment -> CancellationToken -> Task<SegmentOutcome> =
        translateOne context capabilities http endpoint credentialReference
    let runSegments : CancellationToken -> Task<TranslationExtensionResult> =
        translateBySegments context "deeplx-translation" translateSegment
    return! runSegments cancellationToken
}
let extension : ITranslationExtension =
    { new ITranslationExtension with
        member _.Descriptor = descriptor
        member _.ConfigFields = configFields
        member _.ClearDerivedStateAsync(cancellationToken) = clearDerivedState cancellationToken
        member _.TranslateAsync(context, capabilities, cancellationToken) = translate context capabilities cancellationToken }

