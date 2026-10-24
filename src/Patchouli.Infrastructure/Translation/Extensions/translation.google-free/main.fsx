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
    [<Field("Google Translate endpoint", Key = "endpoint"); Default("https://translate.googleapis.com/translate_a/single")>]
    Endpoint: string
    [<Field("Maximum query characters", Key = "max-query-characters"); Default(500); Range(1, 500)>]
    QueryCharacterLimit: int
}

let translateOne (common: CommonSettings) (job: ITranslationJob) (resources: IExtensionResources) (http: HttpClient) (endpoint: string) (segment: Segment) cancellationToken = task {
    let text, tokens = protect segment.Id segment.Text
    // Google codes are region-style: the canonical script tags map back to zh-CN/zh-TW;
    // every other canonical tag (including the auto sentinel) passes through unchanged.
    let toGoogle (language: string) =
        match language with
        | "zh-Hans" -> "zh-CN"
        | "zh-Hant" -> "zh-TW"
        | other -> other
    let source = toGoogle (sourceLanguage common job)
    let target = toGoogle (targetLanguage common job)
    let uri = endpoint + "?client=gtx&dt=t&dj=1&sl=" + Uri.EscapeDataString(source) + "&tl=" + Uri.EscapeDataString(target) + "&q=" + Uri.EscapeDataString(text)
    if uri.Length > 8192 then invalidOp "The Google Translate URL exceeds the safe request length. Reduce the maximum query characters or use another provider."
    let options = effectiveEngineOptions common job (Some 1) (Some 1M)
    let send (token: CancellationToken) = task {
        do! resources.WaitToProceedAsync(token)
        use request = new HttpRequestMessage(HttpMethod.Get, Uri(uri))
        request.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore
        use! response = http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
        ensureHttpResponseSuccess "Google Translate" response
        return! response.Content.ReadAsStringAsync(token)
    }
    let! response = retryTransient 0 (TimeSpan.FromMilliseconds(350.0)) (TimeSpan.FromSeconds(5.0)) send cancellationToken
    return translated segment.Id (restore (parseGoogleResponse response) tokens)
}

/// <summary>Google Free translation extension.</summary>
/// <patchouli-extension id="translation.google-free" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Google Free</name>
/// </patchouli-extension>
let extension =
     Extension.createTranslation<Settings> (fun settings (job: ITranslationJob) (resources: IExtensionResources) cancellationToken -> task {
        let common = settings.Common
        let endpoint = validateHttpEndpoint settings.Endpoint
        let http = resources.Http
        let queryLimit = settings.QueryCharacterLimit
        let options = effectiveEngineOptions common job (Some 1) (Some 1M)
        let work = segmentTranslatorStable common job "google-translation" queryLimit (translateOne common job resources http endpoint)
        let budget = { MaxItems = 1; MaxCharacters = queryLimit; MaxTokens = None }
        do! runSegmentPagesStable job options budget work cancellationToken
    })
    |> Extension.withDerivedState clearDerivedState
