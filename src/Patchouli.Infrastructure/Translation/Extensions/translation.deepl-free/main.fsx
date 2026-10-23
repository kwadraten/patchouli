#load "Support.fsx"
open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

[<Settings>]
type Settings = {
    [<Override("concurrency", 1); Override("request-interval", 1)>]
    Common: CommonSettings
    [<Field("DeepL web endpoint", Key = "endpoint"); Default("https://www2.deepl.com/jsonrpc")>]
    Endpoint: string
}

let translateOne (common: CommonSettings) (job: ITranslationJob) (resources: IExtensionResources) (http: HttpClient) (endpoint: string) (segment: Segment) cancellationToken = task {
    let text, tokens = protect segment.Id segment.Text
    // DeepL web codes: Chinese collapses to the single code "ZH" for both scripts and the
    // auto sentinel stays lowercase; every other canonical tag uppercases to its DeepL code.
    let toDeepL (language: string) =
        match language with
        | "zh" | "zh-Hans" | "zh-Hant" -> "ZH"
        | "auto" -> "auto"
        | other -> other.ToUpperInvariant()
    let source = toDeepL (sourceLanguage common job)
    let target = toDeepL (targetLanguage common job)
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let count = text |> Seq.filter (fun character -> character = 'i') |> Seq.length |> int64
    let adjustedTimestamp = if count > 0L then let divisor = count + 1L in timestamp - timestamp % divisor + divisor else timestamp
    let body = jsonBody {| jsonrpc = "2.0"; method = "LMT_handle_texts"; id = Random.Shared.Next(1000000, 999999999); ``params`` = {| splitting = "newlines"; lang = {| source_lang_user_selected = source; target_lang = target |}; texts = [| {| text = text; requestAlternatives = 0 |} |]; timestamp = adjustedTimestamp |} |}
    let options = effectiveEngineOptions common job (Some 1) (Some 1M)
    let send (token: CancellationToken) = task {
        do! resources.WaitToProceedAsync(token)
        use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        request.Headers.TryAddWithoutValidation("Origin", "https://www.deepl.com") |> ignore
        request.Headers.TryAddWithoutValidation("Referer", "https://www.deepl.com/translator") |> ignore
        request.Headers.TryAddWithoutValidation("Accept", "*/*") |> ignore
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0") |> ignore
        use! response = http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
        ensureHttpResponseSuccess "DeepL" response
        return! response.Content.ReadAsStringAsync(token)
    }
    let! response = retryTransient 0 (TimeSpan.FromMilliseconds(500.0)) (TimeSpan.FromSeconds(6.0)) send cancellationToken
    return translated segment.Id (restore (parseDeepLResponse response) tokens)
}

/// <summary>Deepl Free translation extension.</summary>
/// <patchouli-extension id="translation.deepl-free" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Deepl Free</name>
/// </patchouli-extension>
let extension =
     Extension.createTranslation<Settings> (fun settings (job: ITranslationJob) (resources: IExtensionResources) cancellationToken -> task {
        let common = settings.Common
        let endpoint = validateHttpEndpoint settings.Endpoint
        let http = resources.Http
        let options = effectiveEngineOptions common job (Some 1) (Some 1M)
        let work = segmentTranslatorStable common job "deepl-translation" options.MaxBatchCharacters (translateOne common job resources http endpoint)
        let budget = { MaxItems = 1; MaxCharacters = max 1 options.MaxBatchCharacters; MaxTokens = None }
        do! runSegmentPagesStable job options budget work cancellationToken
    })
    |> Extension.withDerivedState clearDerivedState
