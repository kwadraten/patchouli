#load "Support.fsx"
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

[<Settings>]
type Settings = {
    Common: CommonSettings
    [<Model("Translation model", Key = "llm.primary", FollowDefault = "translation")>]
    Model: ExtensionModelSelection option
    [<Field("Additional prompt instructions", Key = "prompt", Kind = ConfigValueKind.MultilineText); Default("")>]
    Prompt: string
    [<Field("Context window radius", Key = "window-radius"); Default(1); Range(0, 5)>]
    WindowRadius: int
    [<Field("Backfill previous translations", Key = "backfill"); Default(true)>]
    Backfill: bool
    [<Field("Glossary", Key = "glossary", Kind = ConfigValueKind.MultilineText, Description = "One source => translation per line; add an optional note after //."); Default("")>]
    Glossary: string
}
/// The page's merged box outcomes plus the exact segments they came from. A batch-level error is
/// reported per segment with the same code the translateBatch catch uses.
let finish (state: PageWork) (items: WorkSegment array) (results: Result<SegmentOutcome, exn> array) =
    mergeSegments state.Boxes state.Segments
        (Array.map2 (fun (entry: WorkSegment) (result: Result<SegmentOutcome, exn>) ->
             match result with
             | Ok outcome -> outcome
             | Error error -> failure entry.Segment.Id "llm_translation_failed" error.Message) items results)

/// A stable, order-sensitive fingerprint of the page-level reference context. Pages with the same
/// references share a batch group because they share a prompt; no references means an empty key.
let contextKey (references: string array) =
    if references.Length = 0 then ""
    else
        let joined = String.Join("\u001f", references)
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))

let buildLlmRequest (options: TranslationExtensionOptions) (modelOverride: ExtensionModelSelection option)
                    (extraPrompt: string) (target: string) (source: string) (glossaryTerms: string list)
                    (references: string array) (batch: Segment array)
                    : LlmRequest * Dictionary<string, Dictionary<string, string>> =
    let protections = Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
    let promptSegments: {| id: string; text: string |} array = batch |> Array.map (fun (item: Segment) ->
        let protectedText, tokens = protect item.Id item.Text
        protections[item.Id] <- tokens
        {| id = item.Id; text = protectedText |})
    let structure = "Translate each text into " + target + ". Keep every input id unchanged. Return only a JSON array containing exactly one object per input, each with string properties id and text. Do not omit, duplicate, reorder ambiguously, add commentary, or modify HTML/XML tags, placeholders, code, or markup."
    let titleContext = options.DocumentTitle |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                      |> Option.map (fun title -> "Document title: " + title)
    let promptContext = options.PromptContext |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                       |> Option.toList
    let extraPromptContext = if String.IsNullOrWhiteSpace extraPrompt then [] else [ extraPrompt ]
    let referenceContext =
        if references.Length = 0 then None
        else Some("Reference context (do not translate):\n" + String.Join("\n---\n", references))
    let contextText =
        [ yield structure
          yield! Option.toList titleContext
          yield! promptContext
          yield! extraPromptContext
          yield! Option.toList referenceContext
          if not (List.isEmpty glossaryTerms) then
              yield "Glossary: " + String.Join("; ", glossaryTerms)
          yield "Source language: " + source ] |> String.concat "\n\n"
    let user = jsonBody promptSegments
    let selectedProvider, selectedModel =
        match modelOverride with
        | Some selection -> Some selection.ProviderId, Some selection.Model
        | None -> Option.ofObj options.ProviderId, Option.ofObj options.Model
    let systemPrompt = "You are a document translation engine. Translate every supplied segment into the requested target language and return structured JSON. Treat reference context as read-only background: never translate it and never return it. Preserve Markdown, LaTeX, code, links, placeholders, and each segment's internal structure and line breaks. Do not add commentary or explanations. Preserve complete table rows, columns, numbers, and formulas."
    let request = LlmRequest([| LlmMessage("system", systemPrompt + "\n\n" + contextText); LlmMessage("user", user) |],
        Option.toObj selectedModel, Nullable<decimal>(), Nullable<int>(), Option.toObj selectedProvider)
    request, protections

let translateBatch (resources: IExtensionResources) (llm: ILlmCapability)
                   (options: TranslationExtensionOptions) (modelOverride: ExtensionModelSelection option)
                   (extraPrompt: string) (target: string) (source: string) (glossaryTerms: string list)
                   (references: string array) (batch: Segment array) (ct: CancellationToken) = task {
    try
        if batch |> Array.exists (fun item -> item.Text.Length > options.MaxBatchCharacters) then
            invalidOp "A protected code or formula unit exceeds the model request character limit."
        let request, protections = buildLlmRequest options modelOverride extraPrompt target source glossaryTerms references batch
        // Retries belong to the page driver: each attempt issues one request here.
        let retryPolicy = Retry.withPolicy 0 (TimeSpan.FromMilliseconds(350.0)) (TimeSpan.FromSeconds(5.0))
        let complete (token: CancellationToken) = task {
            do! resources.WaitToProceedAsync(token)
            return! llm.CompleteAsync(request, token)
        }
        let! response = Retry.run retryPolicy complete ct
        let returned = decodeLlmResponse response.Text (batch |> Array.map (fun item -> item.Id))
        return batch |> Array.map (fun item -> translated item.Id (restore returned[item.Id] protections[item.Id]))
    with
    // A caller-owned cancellation ends the run; a capability-side timeout is just a failed batch.
    | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
    | error -> return batch |> Array.map (fun item -> failure item.Id "llm_translation_failed" error.Message)
}

/// <summary>Llm translation extension.</summary>
/// <patchouli-extension id="translation.llm" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Llm</name>
/// </patchouli-extension>
let extension =
     Extension.createTranslation<Settings> (fun settings (job: ITranslationJob) (resources: IExtensionResources) cancellationToken -> task {
        let common = settings.Common
        let phase = "llm-translation"
        let options = effectiveOptions common job
        let modelOverride = settings.Model
        let runModelSelection =
            match modelOverride with
            | Some selection -> Some selection
            | None when not (String.IsNullOrWhiteSpace options.ProviderId) || not (String.IsNullOrWhiteSpace options.Model) ->
                Some(ExtensionModelSelection(options.ProviderId |> Option.ofObj |> Option.defaultValue "",
                                             options.Model |> Option.ofObj |> Option.defaultValue ""))
            | None -> None
        let llm = resources.ResolveLlm("translation", Option.toObj runModelSelection)
        let extraPrompt = settings.Prompt
        let radius = settings.WindowRadius
        let backfill = settings.Backfill
        let limit = max 1 options.MaxBatchCharacters

        // Neighboring source pages plus, when backfill is on, the previous page's committed translation.
        let readReferences (page: TranslationPageInput) (token: CancellationToken) = task {
            let pageIndex = page.Page.PageIndex
            let references = ResizeArray<string>()
            let! snapshot = job.ReadContextAsync(page, radius, token)
            token.ThrowIfCancellationRequested()
            snapshot.Neighbors
            |> Seq.filter (fun neighbor ->
                abs (neighbor.Page.PageIndex - pageIndex) <= radius && neighbor.Page.PageIndex <> pageIndex)
            |> Seq.sortBy (fun neighbor -> neighbor.Page.PageIndex)
            |> Seq.iter (fun neighbor ->
                let kind = if neighbor.Page.PageIndex < pageIndex then "Previous page source" else "Next page source"
                if not (String.IsNullOrWhiteSpace neighbor.SourceText) then
                    references.Add(kind + " (page " + string neighbor.Page.PageIndex + "):\n" + neighbor.SourceText))
            if backfill then
                let! previous = job.GetCommittedPageTranslationAsync(pageIndex - 1, token)
                token.ThrowIfCancellationRequested()
                let matches language =
                    not (String.IsNullOrWhiteSpace language) &&
                     String.Equals(language, targetLanguage common job, StringComparison.OrdinalIgnoreCase)
                match Option.ofObj previous |> Option.filter (fun value -> matches value.TargetLanguage) with
                | Some translation ->
                    references.Add("Previous page translation (page " + string translation.Page.PageIndex +
                                   ", current run, read-only):\n" + translation.Markdown)
                | None -> ()
            return references.ToArray()
        }

        let translator: JobTranslator<PageWork, WorkSegment, SegmentOutcome> =
            { Prepare = fun page token -> task {
                  let! references = readReferences page token
                  let work, items = workPage common limit references page
                  return { Page = page
                           State = work
                           Items = items
                           ContextKey = contextKey references
                           // Backfill consumes what the previous page just committed, so pages run in order.
                           Sequential = backfill } }
              Measure = fun item -> item.Segment.Text.Length, None
              Translate = fun (items: WorkSegment[]) token -> task {
                  token.ThrowIfCancellationRequested()
                  // A batch only ever holds segments from pages with identical references.
                  let references = items[0].Context
                  let epoch = captureCacheEpoch ()
                  // Local failures (invalid tables, oversized units) and cache hits resolve without a request.
                  let resolved =
                      items |> Array.map (fun entry ->
                          let segment = entry.Segment
                          if segment.ErrorCode.IsSome then
                              Some(failure segment.Id segment.ErrorCode.Value
                                  (Option.defaultValue "The input cannot be translated safely." segment.ErrorMessage))
                          elif segment.Text.Length > limit then
                              Some(failure segment.Id "translation_segment_exceeds_limit"
                                  "A protected code or formula unit exceeds the model request character limit.")
                          else
                              tryGetCachedTranslation common job entry.Page phase epoch references segment
                              |> Option.map (translated segment.Id))
                  let pending =
                      items |> Array.indexed |> Array.choose (fun (index, entry) ->
                          if (resolved[index]).IsSome then None else Some entry.Segment)
                  let! requested =
                      if pending.Length = 0 then
                          Task.FromResult(Array.empty<SegmentOutcome>)
                      else
                           translateBatch resources llm options modelOverride extraPrompt (targetLanguage common job)
                               (sourceLanguage common job) (glossary job) references pending token
                  let requestedById = Dictionary<string, SegmentOutcome>(StringComparer.Ordinal)
                  for outcome in requested do
                      requestedById[outcome.SegmentId] <- outcome
                  for index in 0 .. items.Length - 1 do
                      let entry = items[index]
                      match resolved[index] with
                      | None ->
                          match requestedById.TryGetValue(entry.Segment.Id) with
                          | true, outcome ->
                              match outcome.Kind, outcome.TranslatedText with
                              | ExtensionBoxOutcomeKind.Translated, Some text ->
                                   cacheTranslation common job entry.Page phase epoch references entry.Segment text
                              | _ -> ()
                          | _ -> ()
                      | Some _ -> ()
                  // Strictly one outcome per input, in input order.
                  return items |> Array.mapi (fun index entry ->
                      match resolved[index] with
                      | Some outcome -> outcome
                      | None ->
                          match requestedById.TryGetValue(entry.Segment.Id) with
                          | true, outcome -> outcome
                          | _ -> failure entry.Segment.Id "llm_translation_failed" "The model response omitted this segment.") }
              Finish = finish }

        let maxTokens = if options.MaxBatchTokens.HasValue then Some options.MaxBatchTokens.Value else None
        let budget = { MaxItems = max 1 options.BatchSize; MaxCharacters = limit; MaxTokens = maxTokens }
        do! ExecutionScope.run options.Concurrency (Job.progress job) cancellationToken (fun () -> task {
            do! Job.runPages job options budget translator cancellationToken })
    })
    |> Extension.withDerivedState clearDerivedState
