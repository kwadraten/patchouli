#load "Support.fsx"
open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

let endpointBinding = Config.bind "endpoint" (Field.text "DeepL web endpoint" "https://www2.deepl.com/jsonrpc"
    |> Field.describe "Uses DeepL's anonymous web JSON-RPC method. This is an unofficial keyless web adapter, not DeepL's official API free tier.")
let httpAccessBinding = Config.bind "http" (Capability.http "DeepL web network access")
let configFields = Config.fields (sharedBindings @ [ Config.asField endpointBinding ])
let descriptor = ExtensionDescriptor("translation.deepl-free", "DeepL (keyless web)", "1.0.0", "Uses the anonymous DeepL web JSON-RPC endpoint without an API key.")

let translateOne (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (http: IHttpCapability) (endpoint: string) (segment: Segment) cancellationToken = task {
    let text, tokens = protect segment.Id segment.Text
    let source = (sourceLanguage context).ToUpperInvariant()
    let target = (targetLanguage context).ToUpperInvariant()
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let count = text |> Seq.filter (fun character -> character = 'i') |> Seq.length |> int64
    let adjustedTimestamp = if count > 0L then let divisor = count + 1L in timestamp - timestamp % divisor + divisor else timestamp
    let body = jsonBody {| jsonrpc = "2.0"; method = "LMT_handle_texts"; id = Random.Shared.Next(1000000, 999999999); ``params`` = {| splitting = "newlines"; lang = {| source_lang_user_selected = source; target_lang = target |}; texts = [| {| text = text; requestAlternatives = 0 |} |]; timestamp = adjustedTimestamp |} |}
    let options = effectiveOptions context
    let deeplHeaders = [ "Origin", "https://www.deepl.com"; "Referer", "https://www.deepl.com/translator";
                         "Accept", "*/*"; "User-Agent", "Mozilla/5.0" ]
    let send (token: CancellationToken) = sendJson capabilities http endpoint body options token String.Empty deeplHeaders
    let! response = retryTransient options.MaxRetries (TimeSpan.FromMilliseconds(500.0)) (TimeSpan.FromSeconds(6.0)) send cancellationToken
    use document = JsonDocument.Parse(response)
    let mutable error = JsonElement()
    if document.RootElement.TryGetProperty("error", &error) then invalidOp "DeepL returned a JSON-RPC error."
    let mutable result = JsonElement()
    let mutable texts = JsonElement()
    if not (document.RootElement.TryGetProperty("result", &result)) || not (result.TryGetProperty("texts", &texts)) || texts.ValueKind <> JsonValueKind.Array || texts.GetArrayLength() <> 1 then
        invalidOp "DeepL response does not map to the single requested box."
    let translatedText = parseStringAt (texts[0]) "text"
    let value =
        match translatedText with
        | None -> invalidOp "DeepL returned empty text."
        | Some text when String.IsNullOrWhiteSpace text -> invalidOp "DeepL returned empty text."
        | Some text -> text
    return translated segment.Id (restore value tokens)
}

let translate (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (cancellationToken: CancellationToken) = task {
    let endpoint = validateHttpEndpoint (Binding.get endpointBinding context.Config)
    let http = Capability.resolveHttp httpAccessBinding context.Config capabilities
    return! translateBySegments context "deepl-translation" (translateOne context capabilities http endpoint) cancellationToken
}
let extension : ITranslationExtension =
    { new ITranslationExtension with
        member _.Descriptor = descriptor
        member _.ConfigFields = configFields
        member _.ClearDerivedStateAsync(cancellationToken) = clearDerivedState cancellationToken
        member _.TranslateAsync(context, capabilities, cancellationToken) = translate context capabilities cancellationToken }

