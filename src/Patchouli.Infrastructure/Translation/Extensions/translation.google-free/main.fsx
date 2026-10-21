#load "Support.fsx"
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

let endpointBinding = Config.bind "endpoint" (Field.text "Google Translate endpoint" "https://translate.googleapis.com/translate_a/single"
    |> Field.describe "Google's free GTX endpoint. No API key is used.")
let queryCharacterLimitBinding = Config.bind "max-query-characters" (Field.integer "Maximum query characters" 500
    |> Field.range 1 500
    |> Field.describe "Caps each URL-encoded GET query to leave room for UTF-8 percent encoding and endpoint parameters.")
let httpAccessBinding = Config.bind "http" (Capability.http "Google Translate network access")
let configFields = Config.fields (sharedBindings @ [ Config.asField endpointBinding; Config.asField queryCharacterLimitBinding ])
let descriptor = ExtensionDescriptor("translation.google-free", "Google Translate (free)", "1.0.0", "Uses Google's free GTX web endpoint without an API key.")

let translateOne (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (http: IHttpCapability) (endpoint: string) (segment: Segment) cancellationToken = task {
    let text, tokens = protect segment.Id segment.Text
    let source = sourceLanguage context
    let target = targetLanguage context
    let uri = endpoint + "?client=gtx&dt=t&dj=1&sl=" + Uri.EscapeDataString(source) + "&tl=" + Uri.EscapeDataString(target) + "&q=" + Uri.EscapeDataString(text)
    if uri.Length > 8192 then invalidOp "The Google Translate URL exceeds the safe request length. Reduce the maximum query characters or use another provider."
    let options = effectiveOptions context
    let send (token: CancellationToken) = task {
        do! pace capabilities options token
        use timeout = timeoutToken options token
        let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        headers["Accept"] <- "application/json"
        let request = HttpRequest("GET", Uri(uri), headers)
        let! result = http.SendAsync(request, timeout.Token)
        ensureHttpSuccess "Google Translate" result.StatusCode
        return result.Body
    }
    let! response = retryTransient options.MaxRetries (TimeSpan.FromMilliseconds(350.0)) (TimeSpan.FromSeconds(5.0)) send cancellationToken
    use document = JsonDocument.Parse(response)
    let mutable sentences = JsonElement()
    if not (document.RootElement.TryGetProperty("sentences", &sentences)) || sentences.ValueKind <> JsonValueKind.Array then
        invalidOp "Google Translate response has no sentences array."
    let translatedText = sentences.EnumerateArray() |> Seq.choose (fun sentence -> parseStringAt sentence "trans") |> String.concat ""
    if String.IsNullOrWhiteSpace translatedText then invalidOp "Google Translate returned empty text."
    return translated segment.Id (restore translatedText tokens)
}

let translate (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (cancellationToken: CancellationToken) = task {
    let endpoint = validateHttpEndpoint (Binding.get endpointBinding context.Config)
    let http = Capability.resolveHttp httpAccessBinding context.Config capabilities
    let queryLimit = Binding.get queryCharacterLimitBinding context.Config
    return! translateBySegmentsWithCharacterLimit context "google-translation" queryLimit
        (translateOne context capabilities http endpoint) cancellationToken
}
let extension : ITranslationExtension =
    { new ITranslationExtension with
        member _.Descriptor = descriptor
        member _.ConfigFields = configFields
        member _.ClearDerivedStateAsync(cancellationToken) = clearDerivedState cancellationToken
        member _.TranslateAsync(context, capabilities, cancellationToken) = translate context capabilities cancellationToken }

