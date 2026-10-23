#load "Support.fsx"
open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

[<Settings>]
type Settings = { [<Override("concurrency", 1); Override("request-interval", 1)>] Common: CommonSettings
                  [<Field("Tencent Transmart endpoint", Key="endpoint"); Default("https://transmart.qq.com/api/imt")>] Endpoint: string
                  [<Field("HTTP User-Agent", Key="user-agent"); Default("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36")>] UserAgent: string }
// One anonymous identity per generation, reused across runs/retries rather than rotated per request.
// This follows the web client's browser/version/platform + UUID + creation-time shape; it is not a secret.
let private automaticClientKey = lazy (
    "browser-chrome-123.0.0-Linux-" + Guid.NewGuid().ToString("D") + "-" +
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
let failureFor (segmentId: string) (error: exn) =
    let code =
        match error with
        | :? TranslationRateLimitedException -> "rate_limited"
        | :? TranslationRequestRejectedException -> "translation_request_rejected"
        | :? TimeoutException -> "translation_request_timeout"
        | :? OperationCanceledException -> "translation_request_timeout"
        | :? ArgumentException -> "invalid_argument"
        | :? JsonException
        | :? InvalidOperationException -> "validation_failed"
        | _ -> "translation_request_failed"
    failure segmentId code error.Message

/// <summary>Unofficial Tencent Transmart web translation with an automatically generated anonymous client identifier. Live availability is not guaranteed.</summary>
/// <patchouli-extension id="translation.tencent-free" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Tencent Transmart (free web)</name>
/// </patchouli-extension>
let extension =
     Extension.createTranslation<Settings> (fun settings job resources cancellationToken -> task {
            let common = settings.Common
            let endpoint = validateHttpEndpoint settings.Endpoint
            // Keep explicit values from older saved configurations, but never require manual entry.
            let configuredClientKey = match job.Info.Config.Values.TryGetValue("client-key") with | true, value -> value | _ -> ""
            let clientKey = if String.IsNullOrWhiteSpace configuredClientKey then automaticClientKey.Value else configuredClientKey
            let userAgent = settings.UserAgent
            let http = resources.Http
            let options = effectiveEngineOptions common job (Some 1) (Some 1M)
            let maxItems = min 8 (max 1 options.BatchSize)
            let maxChars = min 4000 (max 1 options.MaxBatchCharacters)
            let prepare page (token: CancellationToken) = task {
                token.ThrowIfCancellationRequested()
                let initial = prepareWork common maxChars page
                let segments = initial.Segments |> Array.map (fun segment ->
                    let protectedText, _ = protect segment.Id segment.Text
                    if segment.ErrorCode.IsNone && protectedText.Length > maxChars then
                        { segment with ErrorCode = Some "translation_segment_exceeds_limit"; ErrorMessage = Some "The protected structure token exceeds the provider batch character budget." }
                    else segment)
                let work = { initial with Segments = segments }
                return { Page = page; State = work; Items = segments |> Array.map (fun segment -> { Page = page; Segment = segment; Context = [||] }); ContextKey = "tencent-transmart"; Sequential = true }
            }
            let translate (items: WorkSegment array) (token: CancellationToken) = task {
                if userAgent |> Seq.exists Char.IsControl then invalidArg "user-agent" "The User-Agent cannot contain control characters."
                let results = Array.zeroCreate<SegmentOutcome> items.Length
                let pending = ResizeArray<int * string * Dictionary<string,string>>()
                for i in 0 .. items.Length - 1 do
                    token.ThrowIfCancellationRequested()
                    let segment = items[i].Segment
                    if segment.ErrorCode.IsSome then results[i] <- failure segment.Id segment.ErrorCode.Value (Option.defaultValue "Invalid segment." segment.ErrorMessage)
                    else
                        match tryGetCachedTranslation common job items[i].Page "tencent-transmart" (captureCacheEpoch ()) [||] segment with
                        | Some cached -> results[i] <- translated segment.Id cached
                        | None ->
                            let text, protectedTokens = protect segment.Id segment.Text
                            if text.Length > maxChars then results[i] <- failure segment.Id "translation_segment_exceeds_limit" "The protected structure token exceeds the provider batch character budget."
                            else pending.Add(i, text, protectedTokens)
                if pending.Count > 0 then
                    let batch = pending.ToArray()
                    try
                        token.ThrowIfCancellationRequested()
                        let source = sourceLanguage common job
                        if source <> "auto" && source <> "zh-CN" && source <> "en" && source <> "ja" && source <> "ko" && source <> "fr" && source <> "de" && source <> "es" && source <> "ru" && source <> "pt" && source <> "it" && source <> "tr" && source <> "ar" && source <> "th" && source <> "vi" && source <> "id" then invalidOp ("Tencent Transmart does not have a verified language mapping for source language '" + source + "'.")
                        let target = targetLanguage common job
                        if target = "zh-TW" then invalidOp "Tencent Transmart's zh-TW output has not been verified; select another target language."
                        if target <> "zh-CN" && target <> "en" && target <> "ja" && target <> "ko" && target <> "fr" && target <> "de" && target <> "es" && target <> "ru" && target <> "pt" && target <> "it" && target <> "tr" && target <> "ar" && target <> "th" && target <> "vi" && target <> "id" then invalidOp ("Tencent Transmart does not have a verified language mapping for target language '" + target + "'.")
                        let sourceCode = if source = "zh-CN" then "zh" else source
                        let targetCode = if target = "zh-CN" then "zh" else target
                        let payload = {| header = {| fn = "auto_translation"; client_key = clientKey |}; ``type`` = "plain"; model_category = "normal"; source = {| text_list = batch |> Array.map (fun (_, text, _) -> text); lang = sourceCode |}; target = {| lang = targetCode |} |}
                        let body = jsonBody payload
                        do! resources.WaitToProceedAsync(token)
                        use request = new HttpRequestMessage(HttpMethod.Post, Uri(endpoint))
                        request.Content <- new StringContent(body, Text.Encoding.UTF8, "application/json")
                        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
                        request.Headers.Referrer <- Uri("https://transmart.qq.com/zh-CN/index")
                        if not (String.IsNullOrWhiteSpace userAgent) then request.Headers.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
                        let! response = http.SendAsync(request, token)
                        use response = response
                        if not response.IsSuccessStatusCode then
                            let message = "Tencent Transmart returned HTTP " + string (int response.StatusCode) + "."
                            if int response.StatusCode = 429 then raise (TranslationRateLimitedException(message, None))
                            elif int response.StatusCode = 408 || int response.StatusCode >= 500 then raise (TransientTranslationException(message))
                            else raise (TranslationRequestRejectedException(message))
                        let! responseBody = response.Content.ReadAsStringAsync(token)
                        let parsed = parseTencentResponse batch.Length responseBody
                        for j in 0 .. batch.Length - 1 do
                            let index, _, protectedTokens = batch[j]
                            let segment = items[index].Segment
                            try
                                let restored = restore parsed[j] protectedTokens
                                results[index] <- translated segment.Id restored
                                cacheTranslation common job items[index].Page "tencent-transmart" (captureCacheEpoch ()) [||] segment restored
                            with
                            | :? OperationCanceledException as error -> raise error
                            | error -> results[index] <- failureFor segment.Id error
                    with
                    | :? OperationCanceledException as error when token.IsCancellationRequested -> raise error
                    | error ->
                        for index, _, _ in batch do
                            if isNull (box results[index]) || results[index].Kind <> ExtensionBoxOutcomeKind.Translated then results[index] <- failureFor items[index].Segment.Id error
                for i in 0 .. results.Length - 1 do
                    token.ThrowIfCancellationRequested()
                return results
            }
            let translator : JobTranslator<PageWork, WorkSegment, SegmentOutcome> =
                { Prepare = prepare
                  Measure = fun item -> if item.Segment.ErrorCode.IsSome then 0, None else let text, _ = protect item.Segment.Id item.Segment.Text in text.Length, None
                  Translate = translate
                  Finish = fun work items results -> mergeSegments work.Boxes work.Segments (Array.map2 (fun item result -> match result with | Ok value -> value | Error error -> failureFor item.Segment.Id error) items results) }
            let budget = { MaxItems = maxItems; MaxCharacters = maxChars; MaxTokens = None }
            do! runSegmentPagesStable job options budget translator cancellationToken
    })
    |> Extension.withDerivedState clearDerivedState

// Protocol reference: KISS Translator builtin-plugins/tencent.js, responses.js and languageMappings.js,
// commit e6744cd60aa7b34cf98d30550834684878ed977b (GPL-3.0-compatible upstream; independently adapted).
