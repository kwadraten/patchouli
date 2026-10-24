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
                  [<Field("Microsoft Edge translation endpoint", Key="endpoint"); Default("https://edge.microsoft.com/translate/translatetext")>] Endpoint: string
                  [<Field("HTTP User-Agent", Key="user-agent"); Default("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36")>] UserAgent: string }
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

/// <summary>Unofficial Microsoft Edge web translation. It is not Azure Translator and service availability is unverified.</summary>
/// <patchouli-extension id="translation.microsoft-free" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Microsoft Edge (free web)</name>
/// </patchouli-extension>
let extension =
     Extension.createTranslation<Settings> (fun settings job resources cancellationToken -> task {
            let common = settings.Common
            let endpoint = validateHttpEndpoint settings.Endpoint
            let userAgent = settings.UserAgent
            let http = resources.Http
            let options = effectiveEngineOptions common job (Some 1) (Some 1M)
            let maxItems = min 8 (max 1 options.BatchSize)
            let maxChars = min 4000 (max 1 options.MaxBatchCharacters)
            let limit = maxChars
            let prepare page (token: CancellationToken) = task {
                token.ThrowIfCancellationRequested()
                let initial = prepareWork common limit page
                let segments = initial.Segments |> Array.map (fun segment ->
                    let protectedText, _ = protect segment.Id segment.Text
                    if segment.ErrorCode.IsNone && protectedText.Length > maxChars then
                        { segment with ErrorCode = Some "translation_segment_exceeds_limit"; ErrorMessage = Some "The protected structure token exceeds the provider batch character budget." }
                    else segment)
                let work = { initial with Segments = segments }
                return { Page = page; State = work; Items = segments |> Array.map (fun segment -> { Page = page; Segment = segment; Context = [||] }); ContextKey = "microsoft-edge"; Sequential = true }
            }
            /// One HTTP request for a group of pending items; batch-level failures propagate so
            /// the bisecting sender can isolate a poisoned segment to itself.
            let sendPending (batch: (int * TranslationPageInput * Segment * string * Dictionary<string,string>) array) (token: CancellationToken) = task {
                token.ThrowIfCancellationRequested()
                do! resources.WaitToProceedAsync(token)
                // Microsoft codes are the canonical script tags themselves, so canonical
                // input passes straight through; the zh-CN/zh-TW branches only tolerate
                // pre-canonicalization values and never rewrite a canonical tag.
                let toMicrosoft (value: string) = if value = "zh-CN" then "zh-Hans" elif value = "zh-TW" then "zh-Hant" else value
                let source = sourceLanguage common job |> fun value -> if value = "auto" then "" else toMicrosoft value
                let target = targetLanguage common job |> toMicrosoft
                let uri = endpoint + "?from=" + Uri.EscapeDataString(source) + "&to=" + Uri.EscapeDataString(target) + "&isEnterpriseClient=false"
                let body = jsonBody (batch |> Array.map (fun (_, _, _, text, _) -> text))
                use request = new HttpRequestMessage(HttpMethod.Post, Uri(uri))
                request.Content <- new StringContent(body, Text.Encoding.UTF8, "application/json")
                request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
                if not (String.IsNullOrWhiteSpace userAgent) then request.Headers.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
                let! response = http.SendAsync(request, token)
                use response = response
                if not response.IsSuccessStatusCode then
                    let message = "Microsoft Edge returned HTTP " + string (int response.StatusCode) + "."
                    if int response.StatusCode = 429 then raise (TranslationRateLimitedException(message, None))
                    elif int response.StatusCode = 408 || int response.StatusCode >= 500 then raise (TransientTranslationException(message))
                    else raise (TranslationRequestRejectedException(message))
                let! responseBody = response.Content.ReadAsStringAsync(token)
                let parsed = parseMicrosoftResponse target batch.Length responseBody
                return parsed |> Array.mapi (fun j value ->
                    let _, page, segment, _, protectedTokens = batch[j]
                    try
                        let restored = restore value protectedTokens
                        cacheTranslation common job page "microsoft-edge" (captureCacheEpoch ()) [||] segment restored
                        translated segment.Id restored
                    with
                    | :? OperationCanceledException as error -> raise error
                    | error -> failureFor segment.Id error) }
            let translate (items: WorkSegment array) (token: CancellationToken) = task {
                if userAgent |> Seq.exists Char.IsControl then invalidArg "user-agent" "The User-Agent cannot contain control characters."
                let results = Array.zeroCreate<SegmentOutcome> items.Length
                let pending = ResizeArray<int * TranslationPageInput * Segment * string * Dictionary<string,string>>()
                for i in 0 .. items.Length - 1 do
                    token.ThrowIfCancellationRequested()
                    let segment = items[i].Segment
                    if segment.ErrorCode.IsSome then results[i] <- failure segment.Id segment.ErrorCode.Value (Option.defaultValue "Invalid segment." segment.ErrorMessage)
                    else
                        match tryGetCachedTranslation common job items[i].Page "microsoft-edge" (captureCacheEpoch ()) [||] segment with
                        | Some cached -> results[i] <- translated segment.Id cached
                        | None ->
                            let text, protectedTokens = protect segment.Id segment.Text
                            if text.Length > maxChars then results[i] <- failure segment.Id "translation_segment_exceeds_limit" "The protected structure token exceeds the provider batch character budget."
                            else pending.Add(i, items[i].Page, segment, text, protectedTokens)
                if pending.Count > 0 then
                    let batch = pending.ToArray()
                    let! outcomes =
                        sendInBatches (fun group -> sendPending group token)
                            (fun (_, _, segment, _, _) error -> failureFor segment.Id error) batch token
                    for (index, _, _, _, _), outcome in Array.zip batch outcomes do results[index] <- outcome
                for i in 0 .. results.Length - 1 do
                    token.ThrowIfCancellationRequested()
                return results
            }
            let translator : JobTranslator<PageWork, WorkSegment, SegmentOutcome> =
                { Prepare = prepare
                  Measure = fun item -> if item.Segment.ErrorCode.IsSome then 0, None else let text, _ = protect item.Segment.Id item.Segment.Text in text.Length, None
                  Translate = translate
                  Finish = fun work items results token -> task {
                      let retranslate (segments: Segment array) (retryToken: CancellationToken) =
                          sendPending (segments |> Array.map (fun segment ->
                              let text, tokens = protect segment.Id segment.Text
                              -1, work.Page, segment, text, tokens)) retryToken
                      return! mergeSegments retranslate work.Boxes work.Segments
                          (Array.map2 (fun item result -> match result with | Ok value -> value | Error error -> failureFor item.Segment.Id error) items results) token } }
            let budget = { MaxItems = maxItems; MaxCharacters = maxChars; MaxTokens = None }
            do! runSegmentPagesStable job options budget translator cancellationToken
    })
    |> Extension.withDerivedState clearDerivedState

// Protocol reference: KISS Translator builtin-plugins/microsoft.js and languageMappings.js,
// commit e6744cd60aa7b34cf98d30550834684878ed977b (GPL-3.0-compatible upstream; independently adapted).
