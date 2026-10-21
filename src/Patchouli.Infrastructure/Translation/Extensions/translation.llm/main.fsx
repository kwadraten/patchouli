#load "Support.fsx"
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

let modelBinding = Config.bind "llm.primary" (Capability.llm "Translation model override" |> Capability.followDefault Domains.translation)
let promptBinding = Config.bind "prompt" (Field.multiline "Additional prompt instructions" "")
let windowRadiusBinding = Config.bind "window-radius" (Field.integer "Context window radius" 1 |> Field.range 0 5)
let backfillBinding = Config.bind "backfill" (Field.boolean "Backfill previous translations" true)
let glossaryBinding = Config.bind "glossary" (Field.multiline "Glossary" "" |> Field.describe "One source => translation per line; add an optional note after //.")
let configFields =
    Config.fields (
        sharedBindings @ [
            Config.asField modelBinding
            Config.asField promptBinding
            Config.asField windowRadiusBinding
            Config.asField backfillBinding
            Config.asField glossaryBinding
        ])

let descriptor = ExtensionDescriptor("translation.llm", "LLM", "1.0.0",
    "Translates stable box IDs through the configured managed language model.",
    ExtensionKind.Translation, ExtensionPageExecutionMode.Sequential)

let translate (context: TranslationExtensionContext) (capabilities: IExtensionCapabilities) (cancellationToken: CancellationToken) = task {
    let contextSnapshot = Option.ofObj context.ContextSnapshot
    let chosenReferences = ResizeArray<string>()
    match Option.ofObj context.ReferenceContexts with
    | Some references -> chosenReferences.AddRange(references)
    | None -> ()
    match contextSnapshot with
    | Some snapshot ->
        let radius = Binding.get windowRadiusBinding context.Config
        let neighbors = snapshot.Neighbors |> Seq.filter (fun page ->
            abs (page.Page.PageIndex - context.Page.PageIndex) <= radius &&
            page.Page.PageIndex <> context.Page.PageIndex) |> Seq.sortBy (fun page -> page.Page.PageIndex) |> Seq.toArray
        for neighbor in neighbors do
            let kind = if neighbor.Page.PageIndex < context.Page.PageIndex then "Previous page source" else "Next page source"
            if not (String.IsNullOrWhiteSpace neighbor.SourceText) then
                chosenReferences.Add(kind + " (page " + string neighbor.Page.PageIndex + "):\n" + neighbor.SourceText)
        if Binding.get backfillBinding context.Config then
            let previousPage = snapshot.Neighbors |> Seq.tryFind (fun page ->
                page.Page.PageIndex = context.Page.PageIndex - 1)
            let isCurrentLanguage (translation: TranslationContextTranslationSnapshot) =
                translation.TargetLanguage <> null &&
                String.Equals(translation.TargetLanguage, context.TargetLanguage, StringComparison.OrdinalIgnoreCase) &&
                (previousPage |> Option.exists (fun page -> page.Page.RevisionId = translation.Page.RevisionId))
            let previousRun = Option.ofObj snapshot.PreviousRunTranslation |> Option.filter isCurrentLanguage
            let previousStored = Option.ofObj snapshot.PreviousStoredTranslation |> Option.filter isCurrentLanguage
            let previousTranslation =
                match previousRun with
                | Some value -> Some(value, "current run")
                | None -> previousStored |> Option.map (fun value -> value, "stored")
            match previousTranslation with
            | Some (translation, origin) ->
                chosenReferences.Add("Previous page translation (page " + string translation.Page.PageIndex + ", " + origin + ", read-only):\n" + translation.Markdown)
            | None -> ()
    | None -> ()
    let context = context.WithReferenceContexts(chosenReferences.ToArray())
    let options = effectiveOptions context
    let llm = Capability.resolveLlm modelBinding context.Config capabilities
    let modelOverride = Binding.get modelBinding context.Config
    let extraPrompt = Binding.get promptBinding context.Config
    let boxes = translatableBoxes context
    let eligible = boxes |> filteredBoxes context
    let segments = eligible |> Array.collect (segment options.MaxBatchCharacters)
    let requestSegments = segments |> Array.filter (fun item -> item.ErrorCode.IsNone)
    let localFailures = segments |> Array.choose (fun item ->
        item.ErrorCode |> Option.map (fun code ->
            failure item.Id code (item.ErrorMessage |> Option.defaultValue "The input cannot be translated safely.")))
    let cacheEpoch = captureCacheEpoch ()
    let cacheLookups = requestSegments |> Array.map (fun item -> item, tryGetCachedTranslation context "llm-translation" cacheEpoch item)
    let cachedOutcomes = cacheLookups |> Array.choose (fun (item, text) -> text |> Option.map (translated item.Id))
    let uncachedSegments = cacheLookups |> Array.choose (fun (item, text) -> if text.IsSome then None else Some item)
    let batches = buildSegmentBatches uncachedSegments options.BatchSize options.MaxBatchCharacters options.MaxBatchTokens
    let mutable completed = localFailures.Length + cachedOutcomes.Length
    if cachedOutcomes.Length > 0 then
        report context completed segments.Length "llm-translation"
    let translateBatch (batch: Segment array) ct = task {
        try
            if batch |> Array.exists (fun item -> item.Text.Length > options.MaxBatchCharacters) then
                invalidOp "A protected code or formula unit exceeds the model request character limit."
            let protections = Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            let promptSegments = batch |> Array.map (fun item ->
                let protectedText, tokens = protect item.Id item.Text
                protections[item.Id] <- tokens
                {| id = item.Id; text = protectedText |})
            let structure = "Translate each text into " + targetLanguage context + ". Keep every input id unchanged. Return only a JSON array containing exactly one object per input, each with string properties id and text. Do not omit, duplicate, reorder ambiguously, add commentary, or modify HTML/XML tags, placeholders, code, or markup."
            let titleContext = options.DocumentTitle |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                              |> Option.map (fun title -> "Document title: " + title)
            let promptContext = options.PromptContext |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                               |> Option.toList
            let extraPromptContext =
                if String.IsNullOrWhiteSpace extraPrompt then [] else [ extraPrompt ]
            let referenceContext = context.ReferenceContexts |> Option.ofObj
                                   |> Option.filter (fun references -> references.Count > 0)
                                   |> Option.map (fun references -> "Reference context (do not translate):\n" + String.Join("\n---\n", references))
            let contextText =
                [ yield structure
                  yield! Option.toList titleContext
                  yield! promptContext
                  yield! extraPromptContext
                  yield! Option.toList referenceContext
                  if not (List.isEmpty (glossary context)) then
                      yield "Glossary: " + String.Join("; ", glossary context)
                  yield "Source language: " + sourceLanguage context ] |> String.concat "\n\n"
            let user = jsonBody promptSegments
            let selectedProvider, selectedModel =
                match modelOverride with
                | Some selection -> Some selection.ProviderId, Some selection.Model
                | None -> Option.ofObj options.ProviderId, Option.ofObj options.Model
            let systemPrompt = "You are a document translation engine. Translate every supplied segment into the requested target language and return structured JSON. Treat reference context as read-only background: never translate it and never return it. Preserve Markdown, LaTeX, code, links, placeholders, and each segment's internal structure and line breaks. Do not add commentary or explanations. Preserve complete table rows, columns, numbers, and formulas."
            let request = LlmRequest([| LlmMessage("system", systemPrompt + "\n\n" + contextText); LlmMessage("user", user) |],
                Option.toObj selectedModel, Nullable<decimal>(), Nullable<int>(), Option.toObj selectedProvider)
            let retryPolicy = Retry.withPolicy options.MaxRetries (TimeSpan.FromMilliseconds(350.0)) (TimeSpan.FromSeconds(5.0))
            let complete (token: CancellationToken) = task {
                do! capabilities.WaitToProceedAsync(token)
                return! llm.CompleteAsync(request, token)
            }
            let! response = Retry.run retryPolicy complete ct
            let mutable json = response.Text.Trim()
            if json.StartsWith("```", StringComparison.Ordinal) then
                let newline = json.IndexOf('\n')
                json <- if newline >= 0 then json.Substring(newline + 1).Trim() else json.Trim('`').Trim()
                if json.EndsWith("```", StringComparison.Ordinal) then json <- json.Substring(0, json.Length - 3).Trim()
            use document = JsonDocument.Parse(json)
            if document.RootElement.ValueKind <> JsonValueKind.Array then invalidOp "The LLM response must be a JSON array."
            let returned = Dictionary<string, string>(StringComparer.Ordinal)
            for item in document.RootElement.EnumerateArray() do
                let id = parseStringAt item "id"
                let text = parseStringAt item "text"
                match id, text with
                | Some key, Some value when not (String.IsNullOrWhiteSpace key) && not (returned.ContainsKey(key)) ->
                    returned.Add(key, value)
                | _ -> invalidOp "The LLM response contains a missing, duplicate, or invalid box id."
            if returned.Count <> batch.Length || batch |> Array.exists (fun item -> not (returned.ContainsKey(item.Id))) then
                invalidOp "The LLM response does not contain exactly the requested box IDs."
            let values = batch |> Array.map (fun item -> translated item.Id (restore returned[item.Id] protections[item.Id]))
            let now = Interlocked.Add(&completed, batch.Length)
            report context now segments.Length "llm-translation"
            return values
        with
        | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException(ct))
        | error ->
            let values = batch |> Array.map (fun item -> failure item.Id "llm_translation_failed" error.Message)
            let now = Interlocked.Add(&completed, batch.Length)
            report context now segments.Length "llm-translation"
            return values
    }
    let batchPipeline = Step.sync id |>> Step.async (fun batch token -> translateBatch batch token)
    let runBatch batch token = task {
        let! values = Pipeline.run context.Progress "llm-translation" batchPipeline batch token
        return values |> Array.map Ok
    }
    let! mapped = Batch.map options.Concurrency runBatch batches cancellationToken
    let requestedOutcomes = mapped |> Array.map (fun item ->
        match item.Result with
        | Ok value -> value
        | Error error -> failure item.Item.Id "llm_translation_failed" error.Message)
    let outcomes = Array.concat [| localFailures; cachedOutcomes; requestedOutcomes |]
    let mergedOutcomes = mergeSegments boxes segments outcomes
    let translatedBoxes =
        HashSet<Patchouli.Core.Ids.DocumentBoxId>(
            mergedOutcomes
            |> Seq.filter (fun outcome -> outcome.Kind = ExtensionBoxOutcomeKind.Translated)
            |> Seq.map (fun outcome -> outcome.BoxId))
    for item in uncachedSegments do
        if translatedBoxes.Contains(item.Box.Id) then
            match outcomes |> Array.tryFind (fun outcome -> outcome.SegmentId = item.Id), item.ErrorCode with
            | Some outcome, None when outcome.Kind = ExtensionBoxOutcomeKind.Translated ->
                outcome.TranslatedText |> Option.iter (fun text -> cacheTranslation context "llm-translation" cacheEpoch item text)
            | _ -> ()
    return TranslationExtensionResult(mergedOutcomes)
}

let extension : ITranslationExtension =
    { new ITranslationExtension with
        member _.Descriptor = descriptor
        member _.ConfigFields = configFields
        member _.ClearDerivedStateAsync(cancellationToken) = clearDerivedState cancellationToken
        member _.TranslateAsync(context, capabilities, cancellationToken) = translate context capabilities cancellationToken }

