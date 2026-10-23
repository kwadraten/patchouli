namespace BuiltInTranslation

open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization.Metadata
open System.Text.RegularExpressions
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open System.Net.Http
open Patchouli.Core.Ids
open Patchouli.Core.Documents
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

module Support =
    let private tableJsonOptions =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        options

    /// Shared typed settings. Keys intentionally match the legacy persisted configuration keys.
    type CommonSettings =
        { [<Field("Batch size", Key = "batch-size"); Default(8); Range(1, 128)>]
          BatchSize: int
          [<Field("Parallel requests", Key = "concurrency"); Default(4); Range(1, 32)>]
          Concurrency: int
          [<Field("Maximum batch characters", Key = "max-batch-characters"); Default(24000); Range(1, 1000000)>]
          MaxBatchCharacters: int
          [<Field("Maximum output tokens", Key = "max-batch-tokens"); Default(0); Range(0, 1000000)>]
          MaxBatchTokens: int
          [<Field("Request interval (seconds)", Key = "request-interval"); Default(0.0); Range(0.0, 3600.0)>]
          RequestInterval: double
          [<Field("Request timeout (seconds)", Key = "request-timeout"); Default(120.0); Range(1.0, 3600.0)>]
          RequestTimeout: double
          [<Field("Maximum retries", Key = "max-retries"); Default(2); Range(0, 10)>]
          MaxRetries: int
          [<Field("Source language", Key = "source-language", Kind = ConfigValueKind.Language); Default("auto")>]
          SourceLanguage: string
          [<Field("Target language fallback", Key = "target-language", Kind = ConfigValueKind.Language); Default("")>]
          TargetLanguage: string
          [<Field("Cache successful translations", Key = "cache-enabled"); Default(true)>]
          CacheEnabled: bool
          [<Field("Box type filter", Key = "box-types"); Default("")>]
          BoxTypes: string }

    let private translationCache = ConcurrentDictionary<string, string>(StringComparer.Ordinal)
    let private translationCacheOrder = ConcurrentQueue<string>()
    let mutable private cacheEpoch = 0L

    let clearDerivedState (cancellationToken: CancellationToken) : Task = task {
        cancellationToken.ThrowIfCancellationRequested()
        Interlocked.Increment(&cacheEpoch) |> ignore
        translationCache.Clear()
        translationCacheOrder.Clear()
    }

    let private cacheSuccess (key: string) (value: string) =
        if value.Length <= 100000 && translationCache.TryAdd(key, value) then
            translationCacheOrder.Enqueue(key)
            while translationCache.Count > 4096 do
                let mutable oldest = ""
                if translationCacheOrder.TryDequeue(&oldest) then
                    translationCache.TryRemove(oldest) |> ignore

    /// The run-pinned options. Engine settings only override them when the host froze that key.
    let option (job: ITranslationJob) = job.Info.Options

    let config (job: ITranslationJob) = job.Info.Config

    /// The job's canonical source tag. An auto or unspecified job value falls back to the
    /// configured source language (itself canonicalized on entry), then to auto.
    let sourceLanguageTag (common: CommonSettings) (job: ITranslationJob) =
        let language = job.Info.SourceLanguage
        if not language.IsUnspecified && not language.IsAuto then language
        else
            let configured = LanguageTag.Create(common.SourceLanguage)
            if configured.IsUnspecified then LanguageTag.Auto else configured

    let sourceLanguage (common: CommonSettings) (job: ITranslationJob) =
        (sourceLanguageTag common job).Value

    /// The job's canonical target tag. An unspecified job value falls back to the configured
    /// target language; when neither names one, the tag stays unspecified.
    let targetLanguageTag (common: CommonSettings) (job: ITranslationJob) =
        let language = job.Info.TargetLanguage
        if not language.IsUnspecified then language
        else
            let configured = LanguageTag.Create(common.TargetLanguage)
            if configured.IsUnspecified then language else configured

    let targetLanguage (common: CommonSettings) (job: ITranslationJob) =
        (targetLanguageTag common job).Value

    let filteredBoxes (common: CommonSettings) (boxes: ExtensionBox array) =
        let configured = common.BoxTypes
        if String.IsNullOrWhiteSpace configured then boxes
        else
            let selected = Set.ofArray (configured.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
            boxes |> Array.filter (fun box -> selected.Contains(box.Type))

    /// Leaf boxes that carry translatable text; a box holding children is translated through them.
    let translatableBoxes (page: TranslationPageInput) =
        let boxes = page.Boxes
        let parentIds = HashSet<DocumentBoxId>(
            boxes |> Seq.choose (fun box -> if box.ParentId.HasValue then Some box.ParentId.Value else None))
        let allowedTypes = set [ "text"; "title"; "ref_text"; "list"; "table"; "algorithm"; "index"; "header"; "footer";
                                "aside_text"; "page_footnote"; "image_caption"; "image_footnote"; "table_caption";
                                "table_footnote"; "chart_caption"; "chart_footnote"; "code_caption"; "code_footnote" ]
        boxes
        |> Seq.filter (fun box -> not box.Suppressed && not (parentIds.Contains(box.Id)) && allowedTypes.Contains(box.Type)
                                  && not (String.IsNullOrWhiteSpace box.Text))
        |> Seq.toArray

    let glossary (job: ITranslationJob) =
        let frozen = config job
        let configured =
            match frozen.Values.TryGetValue("glossary") with
            | true, value -> value
            | _ -> String.Empty
        let fromConfig =
            if String.IsNullOrWhiteSpace configured then []
            else
                configured.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun line ->
                    let delimiter = line.IndexOf("=>", StringComparison.Ordinal)
                    let width = 2
                    let index = if delimiter >= 0 then delimiter else line.IndexOf('=')
                    let splitWidth = if delimiter >= 0 then width else 1
                    if index <= 0 || index + splitWidth >= line.Length then None
                    else
                        let value = line.Substring(index + splitWidth).Trim()
                        let noteStart = value.IndexOf(" // ", StringComparison.Ordinal)
                        if noteStart >= 0 then
                            Some (line.Substring(0, index).Trim() + " = " + value.Substring(0, noteStart).Trim() + " (" + value.Substring(noteStart + 4).Trim() + ")")
                        else Some (line.Substring(0, index).Trim() + " = " + value))
                |> Array.toList
        let fromRun =
            match job.Info.Options.Glossary with
            | null -> []
            | entries -> entries |> Seq.map (fun term ->
                let note = term.Note |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                           |> Option.map (fun value -> " (" + value.Trim() + ")") |> Option.defaultValue ""
                term.Source + " = " + term.Translation + note) |> Seq.toList
        fromConfig @ fromRun

    type SegmentOutcome =
        { SegmentId: string
          Kind: ExtensionBoxOutcomeKind
          TranslatedText: string option
          ErrorCode: string option
          ErrorMessage: string option }

    let failure (segmentId: string) code message =
        { SegmentId = segmentId; Kind = ExtensionBoxOutcomeKind.Failed; TranslatedText = None
          ErrorCode = Option.ofObj code; ErrorMessage = Option.ofObj message }

    let translated (segmentId: string) text =
        { SegmentId = segmentId; Kind = ExtensionBoxOutcomeKind.Translated; TranslatedText = Some text
          ErrorCode = None; ErrorMessage = None }

    let skipped (boxId: DocumentBoxId) = ExtensionBoxOutcome(boxId, ExtensionBoxOutcomeKind.Skipped)

    /// Config keys frozen in the job override the run options; optional engine-specific defaults apply when neither sets the key.
    let effectiveEngineOptions (common: CommonSettings) (job: ITranslationJob) (defaultConcurrency: int option) (defaultIntervalSeconds: decimal option) =
        let run = option job
        let frozen = config job
        let has key = frozen.Values.ContainsKey(key)
        let batchSize = if has "batch-size" then common.BatchSize else run.BatchSize
        let concurrency =
            if has "concurrency" then common.Concurrency
            elif defaultConcurrency.IsSome then defaultConcurrency.Value
            else run.Concurrency
        let maxCharacters = if has "max-batch-characters" then common.MaxBatchCharacters else run.MaxBatchCharacters
        let maxTokens = if has "max-batch-tokens" then
                            let value = common.MaxBatchTokens
                            if value <= 0 then Nullable<int>() else Nullable<int>(value)
                        else run.MaxBatchTokens
        let interval =
            if has "request-interval" then Nullable<TimeSpan>(TimeSpan.FromSeconds(common.RequestInterval))
            elif defaultIntervalSeconds.IsSome then Nullable<TimeSpan>(TimeSpan.FromSeconds(float defaultIntervalSeconds.Value))
            else run.RequestInterval
        let timeout = if has "request-timeout" then Nullable<TimeSpan>(TimeSpan.FromSeconds(common.RequestTimeout)) else run.RequestTimeout
        let retries = if has "max-retries" then common.MaxRetries else run.MaxRetries
        let radius =
            match frozen.Values.TryGetValue("window-radius") with
            | true, value ->
                match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, parsed -> Math.Clamp(parsed, 0, 5)
                | _ -> run.WindowRadius
            | _ -> run.WindowRadius
        let backfill =
            match frozen.Values.TryGetValue("backfill") with
            | true, value ->
                match Boolean.TryParse(value) with
                | true, parsed -> parsed
                | _ -> run.BackfillPreviousWindowTranslation
            | _ -> run.BackfillPreviousWindowTranslation
        TranslationExtensionOptions(batchSize, maxCharacters, maxTokens, concurrency, interval, timeout, retries,
            radius, backfill, run.DocumentTitle, run.Glossary, run.PromptContext, run.ProviderId, run.Model)

    let effectiveOptions (common: CommonSettings) (job: ITranslationJob) = effectiveEngineOptions common job None None

    type Segment =
        { Box: ExtensionBox
          Id: string
          PartIndex: int
          Text: string
          TableCellKey: string option
          TableCellStart: int
          TableCellLength: int
          ErrorCode: string option
          ErrorMessage: string option }

    let captureCacheEpoch () = Interlocked.Read(&cacheEpoch)

    /// The cache key covers the whole translation request identity: phase, job languages, box content,
    /// run options, the frozen configuration, page-level reference context, glossary and the page's boxes.
    /// Reference context is part of the key so a prompt change (neighbors, backfill, radius) invalidates it.
    let private translationCacheKey (common: CommonSettings) (job: ITranslationJob) (page: TranslationPageInput) (phase: string)
                                    (epoch: int64) (references: string array) (segment: Segment) =
        let options = effectiveOptions common job
        let frozen = config job
        use stream = new System.IO.MemoryStream()
        use writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true)
        let write (value: string | null) =
            writer.Write(value |> Option.ofObj |> Option.defaultValue String.Empty)
        write phase
        writer.Write(epoch)
        let fields: (string | null) list =
            [ sourceLanguage common job; targetLanguage common job; segment.Box.Type; segment.Text
              options.DocumentTitle; options.PromptContext; options.ProviderId; options.Model
              string options.MaxBatchTokens ]
        fields |> List.iter write
        let configuration = frozen.Values |> Seq.sortBy (fun pair -> pair.Key) |> Seq.toArray
        writer.Write(configuration.Length)
        for pair in configuration do
            write pair.Key
            write pair.Value
        writer.Write(references.Length)
        for reference in references do write reference
        let terms = glossary job
        writer.Write(terms.Length)
        for term in terms do write term
        writer.Write(page.Boxes.Count)
        for box in page.Boxes do
            write box.Type
            write box.Text
        writer.Flush()
        Convert.ToHexString(SHA256.HashData(stream.ToArray()))

    let tryGetCachedTranslation (common: CommonSettings) (job: ITranslationJob) (page: TranslationPageInput) phase epoch references segment =
        let mutable text = ""
        if common.CacheEnabled && captureCacheEpoch () = epoch
           && translationCache.TryGetValue(translationCacheKey common job page phase epoch references segment, &text) then Some text
        else None

    let cacheTranslation (common: CommonSettings) (job: ITranslationJob) (page: TranslationPageInput) phase epoch references segment text =
        if common.CacheEnabled && captureCacheEpoch () = epoch then
            cacheSuccess (translationCacheKey common job page phase epoch references segment) text

    let private structureAtomPattern = Regex(
        "<eq\\b[^>]*>[\\s\\S]*?</eq>|<[^>]+>|```[\\s\\S]*?```|~~~[\\s\\S]*?~~~|`[^`\\r\\n]+`|\\{\\{[^}]+\\}\\}|\\{[^}]+\\}|%(?:[0-9]+\\$)?[a-zA-Z]|&(?:#[0-9]+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);|\\$\\$[\\s\\S]*?\\$\\$|\\$(?:\\\\.|[^$\\r\\n])+?\\$|\\\\\\[[\\s\\S]*?\\\\\\]|\\\\\\(.*?\\\\\\)|(?<![\\w])=[A-Za-z_][A-Za-z0-9_.]*\\([^()\\r\\n]*(?:\\([^()\\r\\n]*\\)[^()\\r\\n]*)*\\)|(?<![\\w])=\\$?[A-Z]{1,3}\\$?\\d+(?:\\s*(?:[+*/^<>-]|<=|>=|<>|=)\\s*(?:\\$?[A-Z]{1,3}\\$?\\d+|\\d+(?:\\.\\d+)?))*",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

    let private segmentText (maxCharacters: int) (box: ExtensionBox) (baseId: string)
                            (cellKey: string option) (cellStart: int) (cellLength: int) (text: string) =
        let limit = max 1 maxCharacters
        if text.Length <= limit then
            let suffix = cellKey |> Option.map (fun key -> "#" + key) |> Option.defaultValue ""
            [| { Box = box; Id = baseId + suffix + "#1"; PartIndex = 1; Text = text
                 TableCellKey = cellKey; TableCellStart = cellStart; TableCellLength = cellLength
                 ErrorCode = None; ErrorMessage = None } |]
        else
            let parts = ResizeArray<Segment>()
            let mutable offset = 0
            let mutable part = 1
            while offset < text.Length do
                let window = min limit (text.Length - offset)
                let mutable length = window
                if window < text.Length - offset then
                    let preferred = max 1 (window / 2)
                    let findBoundary startIndex (predicate: char -> bool) =
                        let mutable found = -1
                        for index in startIndex .. window - 1 do
                            if predicate text[offset + index] then found <- index + 1
                        found
                    let findPreferredBoundary startIndex =
                        let newline = findBoundary startIndex (fun character ->
                            character = '\n' ||
                            (character = '\r' && (offset + window >= text.Length || text[offset + window] <> '\n')))
                        let sentence = findBoundary startIndex (fun character -> ".!?;。！？；…".Contains(character))
                        let whitespace = findBoundary startIndex (fun character -> character <> '\r' && Char.IsWhiteSpace(character))
                        if newline > 0 then newline elif sentence > 0 then sentence else whitespace
                    let preferredBoundary = findPreferredBoundary preferred
                    let candidate = if preferredBoundary > 0 then preferredBoundary else findPreferredBoundary 0
                    if candidate > 0 then
                        length <- candidate
                        if ".!?;。！？；…".Contains(text[offset + length - 1]) then
                            let closing = ")]}\"'”’»」』》〉）】〕］"
                            while length < window && closing.Contains(text[offset + length]) do
                                length <- length + 1
                    elif Char.IsHighSurrogate(text[offset + length - 1]) && Char.IsLowSurrogate(text[offset + length]) then length <- length - 1
                    elif text[offset + length - 1] = '\r' && text[offset + length] = '\n' then length <- length - 1
                    let proposedEnd = offset + length
                    let crossingAtom = structureAtomPattern.Matches(text) |> Seq.cast<Match>
                                        |> Seq.tryFind (fun matched -> matched.Index < proposedEnd && matched.Index + matched.Length > proposedEnd)
                    match crossingAtom with
                    | Some matched when matched.Index > offset -> length <- matched.Index - offset
                    | Some matched -> length <- matched.Length
                    | None -> ()
                let value = text.Substring(offset, length)
                let suffix = cellKey |> Option.map (fun key -> "#" + key) |> Option.defaultValue ""
                parts.Add({ Box = box; Id = baseId + suffix + "#" + part.ToString(CultureInfo.InvariantCulture)
                            PartIndex = part; Text = value; TableCellKey = cellKey
                            TableCellStart = cellStart; TableCellLength = cellLength
                            ErrorCode = None; ErrorMessage = None })
                offset <- offset + length
                part <- part + 1
            parts.ToArray()

    let private markdownCellRanges (text: string) =
        let lines = Regex.Matches(text, "(?m)^[^\\r\\n]+") |> Seq.cast<Match> |> Seq.toArray
        let getCells (line: Match) =
            let pipes = ResizeArray<int>()
            for index in 0 .. line.Length - 1 do
                if line.Value[index] = '|' && (index = 0 || line.Value[index - 1] <> '\\') then pipes.Add(index)
            if pipes.Count = 0 then [||]
            else
                let boundaries = ResizeArray<int>(pipes.Count + 2)
                boundaries.Add(-1)
                for pipe in pipes do boundaries.Add(pipe)
                boundaries.Add(line.Length)
                [| for index in 0 .. boundaries.Count - 2 do
                       let mutable first = boundaries[index] + 1
                       let mutable last = boundaries[index + 1]
                       while first < last && Char.IsWhiteSpace(line.Value[first]) do first <- first + 1
                       while last > first && Char.IsWhiteSpace(line.Value[last - 1]) do last <- last - 1
                       if last > first then yield line.Index + first, last - first |]
        let rows = lines |> Array.map (fun line -> line, getCells line)
        let separator = rows |> Array.tryFindIndex (fun (_, cells) -> cells.Length >= 2 && cells |> Array.forall (fun (start, length) ->
            Regex.IsMatch(text.Substring(start, length).Trim(), "^:?-{3,}:?$")))
        match separator with
        | Some separatorIndex when separatorIndex > 0 ->
            rows |> Array.mapi (fun rowIndex (_, cells) -> rowIndex, cells)
                 |> Array.filter (fun (rowIndex, _) -> rowIndex <> separatorIndex)
                 |> Array.collect (fun (rowIndex, cells) ->
                     cells |> Array.mapi (fun columnIndex (start, length) -> rowIndex, columnIndex, start, length))
                 |> Array.filter (fun (_, _, _, length) -> length > 0)
        | _ -> [||]

    let private readTablePayload (box: ExtensionBox) =
        match box.Payload with
        | :? TableBoxPayload as table -> Some table
        | null ->
            try
                JsonSerializer.Deserialize<TableBoxPayload>(box.PayloadJson, tableJsonOptions) |> Option.ofObj
            with _ -> None
        | _ -> None

    let private tableSource (box: ExtensionBox) =
        match readTablePayload box with
        | Some payload ->
            let mutable html = String.Empty
            if TableContentAdapter.TryGetHtml(payload, &html) then
                Some("html", html, not (TableContentAdapter.IsHtml(payload)))
            elif payload.ContentFormat = TableContentFormat.LegacyMarkdown
                 && not (String.IsNullOrWhiteSpace payload.Markdown) then
                Some("markdown", payload.Markdown, false)
            else None
        | None -> None

    let segment (maxCharacters: int) (box: ExtensionBox) =
        if String.Equals(box.Type, "table", StringComparison.OrdinalIgnoreCase) then
            let invalidTable () =
                [| { Box = box; Id = box.Id.ToString() + "#invalid-table"; PartIndex = 1; Text = String.Empty
                     TableCellKey = None; TableCellStart = -1; TableCellLength = 0
                     ErrorCode = Some "translation_table_invalid"
                     ErrorMessage = Some "The table has no safely translatable cells or could not be parsed." } |]
            let noContentPlaceholder =
                String.Equals((box.Text |> Option.ofObj |> Option.defaultValue String.Empty).Trim(),
                    "[Table]", StringComparison.OrdinalIgnoreCase)
            match tableSource box with
            | Some("html", text, placeholder) ->
                let ranges = Regex.Matches(text, "<(?<tag>td|th)\\b[^>]*>(?<cell>[\\s\\S]*?)</\\k<tag>>", RegexOptions.IgnoreCase)
                             |> Seq.cast<Match>
                             |> Seq.mapi (fun index matched ->
                                 let prefix = text.Substring(0, matched.Index)
                                 let rowMatches = Regex.Matches(prefix, "<tr\\b", RegexOptions.IgnoreCase)
                                 let rowStart = prefix.LastIndexOf("<tr", StringComparison.OrdinalIgnoreCase)
                                 let rowPrefix = if rowStart < 0 then prefix else prefix.Substring(rowStart)
                                 let column = Regex.Matches(rowPrefix, "<(?:td|th)\\b", RegexOptions.IgnoreCase).Count
                                 max 0 (rowMatches.Count - 1), column, matched.Groups["cell"].Index, matched.Groups["cell"].Length)
                             |> Seq.toArray
                if Regex.Matches(text, "<table\\b", RegexOptions.IgnoreCase).Count > 1 then invalidTable ()
                elif placeholder && ranges.Length = 0 then [||]
                elif ranges.Length = 0 then invalidTable ()
                else ranges |> Array.collect (fun (row, column, start, length) ->
                    let key = "r" + row.ToString(CultureInfo.InvariantCulture) + "c" + column.ToString(CultureInfo.InvariantCulture)
                    segmentText maxCharacters box (box.Id.ToString()) (Some key) start length (text.Substring(start, length)))
            | Some("markdown", text, _) ->
                let ranges = markdownCellRanges text
                if noContentPlaceholder then [||]
                elif ranges.Length = 0 then invalidTable ()
                else ranges |> Array.collect (fun (row, column, start, length) ->
                        let key = "r" + row.ToString(CultureInfo.InvariantCulture) + "c" + column.ToString(CultureInfo.InvariantCulture)
                        segmentText maxCharacters box (box.Id.ToString()) (Some key) start length (text.Substring(start, length)))
            | _ -> if noContentPlaceholder then [||] else invalidTable ()
        else
            let text = box.Text |> Option.ofObj |> Option.defaultValue String.Empty
            segmentText maxCharacters box (box.Id.ToString()) None -1 0 text

    let buildSegmentBatches (segments: Segment array) batchSize maxCharacters (maxTokens: Nullable<int>) =
        let size = max 1 batchSize
        let maxChars = max 1 maxCharacters
        let tokenLimit = if maxTokens.HasValue && maxTokens.Value > 0 then maxTokens.Value else 0
        let batches = ResizeArray<Segment array>()
        let current = ResizeArray<Segment>()
        let mutable characters = 0
        let mutable tokens = 0
        for segment in segments do
            let segmentTokens = (segment.Text.Length + 3) / 4
            let exceedsTokens = tokenLimit > 0 && tokens + segmentTokens > tokenLimit
            if current.Count > 0 && (current.Count >= size || characters + segment.Text.Length > maxChars || exceedsTokens) then
                batches.Add(current.ToArray())
                current.Clear()
                characters <- 0
                tokens <- 0
            current.Add(segment)
            characters <- characters + segment.Text.Length
            tokens <- tokens + segmentTokens
        if current.Count > 0 then batches.Add(current.ToArray())
        batches.ToArray()

    let mergeSegments (boxes: ExtensionBox array) (segments: Segment array) (outcomes: SegmentOutcome array) =
        let byId = Dictionary<string, SegmentOutcome>(StringComparer.Ordinal)
        for outcome in outcomes do byId[outcome.SegmentId] <- outcome
        boxes |> Array.map (fun box ->
            let owned = segments |> Array.filter (fun segment -> segment.Box.Id = box.Id) |> Array.sortBy (fun segment -> segment.PartIndex)
            if owned.Length = 0 then skipped box.Id
            else
                let results = owned |> Array.map (fun segment -> byId.TryGetValue(segment.Id))
                if results |> Array.exists (function | true, value -> value.Kind <> ExtensionBoxOutcomeKind.Translated | _ -> true) then
                    let error = results |> Array.tryPick (function | true, value when value.Kind = ExtensionBoxOutcomeKind.Failed -> Some value | _ -> None)
                    match error with
                    | Some error -> ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed, null, null,
                        Option.toObj error.ErrorCode, Option.toObj error.ErrorMessage)
                    | None -> ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed, null, null,
                        "translation_segment_missing", "A text segment did not produce a translation.")
                elif owned |> Array.exists (fun segment -> segment.TableCellKey.IsSome) then
                    match tableSource box with
                    | None -> ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed, null, null,
                        "translation_table_invalid", "The table payload could not be reconstructed safely.")
                    | Some(field, source, _) ->
                        let translatedCells =
                            owned |> Array.groupBy (fun segment -> segment.TableCellKey.Value)
                                  |> Array.map (fun (_, cellSegments) ->
                                      let ordered = cellSegments |> Array.sortBy (fun segment -> segment.PartIndex)
                                      ordered[0].TableCellStart, ordered[0].TableCellLength,
                                      (ordered |> Array.map (fun segment -> byId[segment.Id].TranslatedText |> Option.defaultValue "") |> String.concat ""))
                        let mutable rebuilt = source
                        for start, length, value in translatedCells |> Array.sortByDescending (fun (start, _, _) -> start) do
                            rebuilt <- rebuilt.Remove(start, length).Insert(start, value)
                        match readTablePayload box with
                        | None ->
                            ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed, null, null,
                                "translation_table_invalid", "The table payload could not be reconstructed safely.")
                        | Some sourcePayload ->
                            let candidate =
                                if field = "html" then
                                    let created = TableContentAdapter.CreateHtml(rebuilt)
                                    if created.IsSuccess then TableContentAdapter.ValidateTranslation(sourcePayload, created.Value)
                                    else created
                                else
                                    Patchouli.Core.Results.Result<TableBoxPayload>.Success(
                                        TableBoxPayload(rebuilt, Annotations = sourcePayload.Annotations))
                            if candidate.IsFailure then
                                ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Failed, null, null,
                                    "translation_table_invalid", candidate.ErrorMessage)
                            else
                                ExtensionBoxOutcome.FromPayload(box.Id, candidate.Value)
                else
                    let text = owned |> Array.map (fun segment -> byId[segment.Id].TranslatedText |> Option.defaultValue "") |> String.concat ""
                    match box.Payload with
                    | :? TextBoxPayload as source ->
                        ExtensionBoxOutcome.FromPayload(box.Id, TextBoxPayload(text, Annotations = source.Annotations))
                    | :? ListBoxPayload as source ->
                        ExtensionBoxOutcome.FromPayload(box.Id, ListBoxPayload(text, Annotations = source.Annotations))
                    | :? AlgorithmBoxPayload as source ->
                        ExtensionBoxOutcome.FromPayload(box.Id, AlgorithmBoxPayload(text, Annotations = source.Annotations))
                    | _ -> ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, text))

    let configText key fallback (job: ITranslationJob) =
        match (config job).Values.TryGetValue(key) with
        | true, value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
        | _ -> fallback

    let validateHttpEndpoint (endpoint: string) =
        if String.IsNullOrWhiteSpace endpoint || not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then
            invalidArg "endpoint" "The endpoint must be an absolute HTTP(S) URL."
        let uri = Uri(endpoint)
        if uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps then
            invalidArg "endpoint" "The endpoint must use HTTP or HTTPS."
        endpoint.Trim()

    let protect (identity: string) (text: string) =
        let identityHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))
                           |> fun value -> value.Substring(0, 10)
        let mutable index = 0
        let map = Dictionary<string, string>(StringComparer.Ordinal)
        let output = structureAtomPattern.Replace(text, fun matched ->
            let key = "ZXQPATCHOULI" + identityHash + "T" + index.ToString(CultureInfo.InvariantCulture) + "QXZ"
            map.Add(key, matched.Value)
            index <- index + 1
            key)
        output, map

    let restore (translated: string) (map: Dictionary<string, string>) =
        let mutable output = translated
        for pair in map do
            let occurrences = Regex.Matches(output, Regex.Escape(pair.Key), RegexOptions.IgnoreCase).Count
            if occurrences <> 1 then invalidOp "The provider changed or omitted a protected structure token."
            output <- Regex.Replace(output, Regex.Escape(pair.Key), MatchEvaluator(fun _ -> pair.Value), RegexOptions.IgnoreCase)
        output

    /// The SDK's ambient execution scope forwards this pause gate as a cross-worker progress step.
    /// Host scheduler queue/cooldown is opaque here; do not claim a cooldown countdown from it.
    let pace (resources: IExtensionResources) (_options: TranslationExtensionOptions) cancellationToken =
        let step =
            Step.async' (fun () token -> task { do! resources.WaitToProceedAsync(token) })
            |> Step.named "request-wait"
        Pipeline.run None "request-wait" step () cancellationToken

    exception TransientTranslationException of string

    /// Raised when the provider definitively rejects the request (a non-retryable status like 400
    /// or 403). Resending the same request cannot succeed, so it maps to a non-retryable code.
    exception TranslationRequestRejectedException of string

    /// Raised when the provider rate limits or exhausts quota (HTTP 429).
    exception TranslationRateLimitedException of string * TimeSpan option

    let retryTransient (maxRetries: int) (initialDelay: TimeSpan) (maxDelay: TimeSpan)
                       (work: CancellationToken -> Task<string>) (cancellationToken: CancellationToken) = task {
        let mutable attempt = 0
        let mutable result: string option = None
        while result.IsNone do
            try
                let! response = work cancellationToken
                result <- Some response
            with error ->
                let retryError: exn =
                    match error with
                    | :? OperationCanceledException as timeoutError when not cancellationToken.IsCancellationRequested ->
                        TimeoutException("The translation request timed out.", timeoutError) :> exn
                    | _ -> error
                let transient =
                    match retryError with
                    | :? TranslationRateLimitedException -> false
                    | :? TransientTranslationException -> true
                    | :? System.Net.Http.HttpRequestException -> true
                    | :? TimeoutException -> true
                    | _ -> false
                if cancellationToken.IsCancellationRequested then
                    raise (OperationCanceledException(cancellationToken))
                if not transient || attempt >= maxRetries then
                    raise retryError
                let delayTicks = min maxDelay.Ticks (initialDelay.Ticks * (1L <<< min attempt 20))
                attempt <- attempt + 1
                do! Task.Delay(TimeSpan.FromTicks(delayTicks), cancellationToken)
        return result.Value
    }

    let ensureHttpSuccess (provider: string) (statusCode: int) =
        let message = provider + " returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + "."
        if statusCode = 429 then
            raise (TranslationRateLimitedException(message, None))
        elif statusCode = 408 || (statusCode >= 500 && statusCode <= 599) then
            raise (TransientTranslationException(message))
        elif statusCode < 200 || statusCode >= 300 then
            raise (TranslationRequestRejectedException(message))

    let ensureHttpResponseSuccess (provider: string) (response: HttpResponseMessage) =
        let statusCode = int response.StatusCode
        let message = provider + " returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + "."
        if statusCode = 429 then
            let retryAfter =
                match response.Headers.RetryAfter with
                | null -> None
                | retry when retry.Delta.HasValue -> Some retry.Delta.Value
                | retry when retry.Date.HasValue -> Some(max TimeSpan.Zero (retry.Date.Value - DateTimeOffset.UtcNow))
                | _ -> None
            raise (TranslationRateLimitedException(message, retryAfter))
        elif statusCode = 408 || (statusCode >= 500 && statusCode <= 599) then
            raise (TransientTranslationException(message))
        elif statusCode < 200 || statusCode >= 300 then
            raise (TranslationRequestRejectedException(message))

    let report (job: ITranslationJob) completed total phase =
        Job.progress job |> Option.iter (fun progress -> progress.Report(ExtensionProgress(phase, completed, total)))

    let private jsonOptions =
        let options = JsonSerializerOptions()
        options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        options

    let jsonBody<'T> (value: 'T) = JsonSerializer.Serialize(value, jsonOptions)

    let sendJson (resources: IExtensionResources) (uri: string) (body: string)
                 (options: TranslationExtensionOptions) (cancellationToken: CancellationToken)
                 (credentialReference: string | null) (extraHeaders: (string * string) seq) = task {
        do! pace resources options cancellationToken
        use request = new HttpRequestMessage(HttpMethod.Post, Uri(uri))
        let content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        request.Content <- content
        for key, value in extraHeaders do
            if not (String.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) then
                if not (request.Headers.TryAddWithoutValidation(key, value)) then
                    content.Headers.TryAddWithoutValidation(key, value) |> ignore
        match Option.ofObj credentialReference |> Option.filter (String.IsNullOrWhiteSpace >> not) with
        | Some reference ->
            let! credential = resources.GetCredentialAsync(reference, cancellationToken)
            if not (String.IsNullOrWhiteSpace credential) then
                request.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential)
            else invalidOp "The configured credential is unavailable."
        | None -> ()
        use! response = resources.Http.SendAsync(request, cancellationToken)
        ensureHttpResponseSuccess "Translation service" response
        return! response.Content.ReadAsStringAsync(cancellationToken)
    }

    let parseStringAt (element: JsonElement) (property: string) =
        let mutable value = JsonElement()
        if element.TryGetProperty(property, &value) && value.ValueKind = JsonValueKind.String then
            value.GetString() |> Option.ofObj
        else None

    /// Pure LLM wire decoder: validates the complete identity set without touching job or resources.
    let decodeLlmResponse (responseText: string) (expectedIds: string array) =
        let mutable json = responseText.Trim()
        if json.StartsWith("```", StringComparison.Ordinal) then
            let newline = json.IndexOf('\n')
            json <- if newline >= 0 then json.Substring(newline + 1).Trim() else json.Trim('`').Trim()
            if json.EndsWith("```", StringComparison.Ordinal) then
                json <- json.Substring(0, json.Length - 3).Trim()
        use document = JsonDocument.Parse(json)
        if document.RootElement.ValueKind <> JsonValueKind.Array then
            invalidOp "The LLM response must be a JSON array."
        let returned = Dictionary<string, string>(StringComparer.Ordinal)
        for item in document.RootElement.EnumerateArray() do
            let id = parseStringAt item "id"
            let text = parseStringAt item "text"
            match id, text with
            | Some key, Some value when not (String.IsNullOrWhiteSpace key) && not (returned.ContainsKey(key)) ->
                returned.Add(key, value)
            | _ -> invalidOp "The LLM response contains a missing, duplicate, or invalid box id."
        if returned.Count <> expectedIds.Length || (expectedIds |> Array.exists (fun id -> not (returned.ContainsKey(id)))) then
            invalidOp "The LLM response does not contain exactly the requested box IDs."
        expectedIds |> Array.map (fun id -> id, returned[id]) |> Map.ofArray

    /// Pure provider response decoders used by the request adapters.
    let parseGoogleResponse (responseText: string) =
        use document = JsonDocument.Parse(responseText)
        let mutable sentences = JsonElement()
        if not (document.RootElement.TryGetProperty("sentences", &sentences)) || sentences.ValueKind <> JsonValueKind.Array then
            invalidOp "Google Translate response has no sentences array."
        let text = sentences.EnumerateArray() |> Seq.choose (fun sentence -> parseStringAt sentence "trans") |> String.concat ""
        if String.IsNullOrWhiteSpace text then invalidOp "Google Translate returned empty text."
        text

    let parseDeepLResponse (responseText: string) =
        use document = JsonDocument.Parse(responseText)
        let mutable error = JsonElement()
        if document.RootElement.TryGetProperty("error", &error) then invalidOp "DeepL returned a JSON-RPC error."
        let mutable result = JsonElement()
        let mutable texts = JsonElement()
        if not (document.RootElement.TryGetProperty("result", &result)) || not (result.TryGetProperty("texts", &texts))
           || texts.ValueKind <> JsonValueKind.Array || texts.GetArrayLength() <> 1 then
            invalidOp "DeepL response does not map to the single requested box."
        match parseStringAt texts[0] "text" with
        | Some text when not (String.IsNullOrWhiteSpace text) -> text
        | _ -> invalidOp "DeepL returned empty text."

    let parseDeepLxResponse (responseText: string) =
        use document = JsonDocument.Parse(responseText)
        let mutable data = JsonElement()
        if not (document.RootElement.TryGetProperty("data", &data)) || data.ValueKind <> JsonValueKind.String then
            invalidOp "DeepLX response has no translated data field."
        let text = data.GetString() |> Option.ofObj |> Option.defaultValue String.Empty
        if String.IsNullOrWhiteSpace text then invalidOp "DeepLX returned empty text."
        text

    let parseMicrosoftResponse (target: string) (expectedCount: int) (responseText: string) =
        use document = JsonDocument.Parse(responseText)
        if document.RootElement.ValueKind <> JsonValueKind.Array || document.RootElement.GetArrayLength() <> expectedCount then
            invalidOp "Microsoft Edge returned a response count different from the request."
        [| for index in 0 .. expectedCount - 1 do
               let item = document.RootElement[index]
               let mutable translations = JsonElement()
               if not (item.TryGetProperty("translations", &translations)) || translations.ValueKind <> JsonValueKind.Array
                  || translations.GetArrayLength() = 0 then invalidOp "Microsoft Edge omitted a translation."
               let requestedLanguage = target.ToLowerInvariant()
               let candidates = translations.EnumerateArray() |> Seq.toArray
               let hasExplicitLanguage = candidates |> Array.exists (fun candidate -> (parseStringAt candidate "to").IsSome)
               let selected =
                   if hasExplicitLanguage then
                        candidates |> Array.tryFind (fun candidate ->
                            match parseStringAt candidate "to" with
                            | Some value -> String.Equals(value, requestedLanguage, StringComparison.OrdinalIgnoreCase)
                            | None -> false)
                   elif candidates.Length = 1 then Some candidates[0]
                   else None
               let candidate = selected |> Option.defaultWith (fun () -> invalidOp "Microsoft Edge translations did not include the requested target language.")
               let value = parseStringAt candidate "text" |> Option.defaultValue ""
               if String.IsNullOrWhiteSpace value then invalidOp "Microsoft Edge returned empty text."
               yield value |]

    let parseTencentResponse (expectedCount: int) (responseText: string) =
        if responseText.TrimStart().StartsWith("<", StringComparison.Ordinal) then
            invalidOp "Tencent Transmart returned HTML, possibly an interactive challenge; no challenge is bypassed."
        use document = JsonDocument.Parse(responseText)
        let root = document.RootElement
        let mutable explicitError = JsonElement()
        let hasExplicitError = root.TryGetProperty("error", &explicitError) && explicitError.ValueKind <> JsonValueKind.Null && explicitError.ToString() <> ""
        let mutable returnCode = JsonElement()
        let hasCode =
            (root.TryGetProperty("ret_code", &returnCode) || root.TryGetProperty("code", &returnCode)) ||
            (let mutable responseHeader = JsonElement() in root.TryGetProperty("header", &responseHeader) && responseHeader.TryGetProperty("ret_code", &returnCode))
        let code = if hasCode then (if returnCode.ValueKind = JsonValueKind.String then returnCode.GetString() else returnCode.ToString()) else null
        let failedCode = hasCode && not (String.IsNullOrWhiteSpace code) && code <> "0" && code <> "200"
                         && not (String.Equals(code, "OK", StringComparison.OrdinalIgnoreCase))
                         && not (String.Equals(code, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                         && not (String.Equals(code, "SUCC", StringComparison.OrdinalIgnoreCase))
        if hasExplicitError || failedCode then invalidOp "Tencent Transmart returned an application-level error."
        let mutable translatedArray = JsonElement()
        if not (root.TryGetProperty("auto_translation", &translatedArray)) || translatedArray.ValueKind <> JsonValueKind.Array
           || translatedArray.GetArrayLength() <> expectedCount then
            invalidOp "Tencent Transmart returned an auto_translation count different from the request."
        [| for item in translatedArray.EnumerateArray() do
               let value = if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj |> Option.defaultValue ""
                           else parseStringAt item "dst" |> Option.orElseWith (fun () -> parseStringAt item "text") |> Option.defaultValue ""
               if String.IsNullOrWhiteSpace value then invalidOp "Tencent Transmart returned an empty translation."
               yield value |]

    /// A segment paired with the page it came from, so the shared cache key stays page-scoped.
    type WorkSegment =
        { Page: TranslationPageInput
          Segment: Segment
          /// Engine-specific context for the page; kept out of the cache key.
          Context: string array }

    /// One page's prepared work: the boxes to merge back into and the page-scoped segments to translate.
    type PageWork =
        { Page: TranslationPageInput
          Boxes: ExtensionBox array
          Segments: Segment array }

    let prepareWork (common: CommonSettings) (maxCharacters: int) (page: TranslationPageInput) =
        let boxes = translatableBoxes page |> filteredBoxes common
        { Page = page
          Boxes = boxes
          Segments = boxes |> Array.collect (segment maxCharacters) }

    /// Builds one page's drive state plus its items, with segment-level caching left to `translateOne`.
    let workPage (common: CommonSettings) (limit: int) (context: string array) (page: TranslationPageInput) =
        let work = prepareWork common limit page
        work, work.Segments |> Array.map (fun item -> { Page = page; Segment = item; Context = context })

    /// Segment-level translator for the request-based providers. Each item is one segment, the cache is
    /// consulted before `translateOne` runs, and every successful segment is cached for the next run.
    /// These engines have no page-level reference context, so their cache key carries none.
    let segmentTranslatorWithSequential (common: CommonSettings) (sequential: bool) (job: ITranslationJob) (phase: string) (limit: int)
                                        (translateOne: Segment -> CancellationToken -> Task<SegmentOutcome>)
                                        : JobTranslator<PageWork, WorkSegment, SegmentOutcome> =
        let noReferences = Array.empty<string>
        { Prepare = fun page token -> task {
              token.ThrowIfCancellationRequested()
              let work, items = workPage common limit noReferences page
              return { Page = page
                       State = work
                       Items = items
                       // Context grouping remains independent from page/batch sequence policy.
                       ContextKey = ""
                       Sequential = sequential } }
          Measure = fun item -> item.Segment.Text.Length, None
          Translate = fun items token -> task {
              let outcomes = Array.zeroCreate items.Length
              let mutable rateLimited = false
              for index in 0 .. items.Length - 1 do
                  token.ThrowIfCancellationRequested()
                  let entry = items[index]
                  let item = entry.Segment
                  let! outcome =
                      if rateLimited then
                          Task.FromResult(failure item.Id "rate_limited" "Translation stopped after the provider rate limited a request.")
                      elif item.ErrorCode.IsSome then
                          Task.FromResult(failure item.Id item.ErrorCode.Value
                              (Option.defaultValue "The table is invalid." item.ErrorMessage))
                      elif item.Text.Length > limit then
                          Task.FromResult(failure item.Id "translation_segment_exceeds_limit"
                              "A protected code or formula unit exceeds the provider's request character limit.")
                      else
                          match tryGetCachedTranslation common job entry.Page phase (captureCacheEpoch ()) noReferences item with
                          | Some text -> Task.FromResult(translated item.Id text)
                          | None -> task {
                              try
                                  return! translateOne item token
                              with
                              | :? OperationCanceledException as error -> return raise error
                              | :? TranslationRateLimitedException as error ->
                                  rateLimited <- true
                                  return failure item.Id "rate_limited" error.Message
                          }
                  match outcome.Kind, outcome.TranslatedText with
                  | ExtensionBoxOutcomeKind.Translated, Some text ->
                      cacheTranslation common job entry.Page phase (captureCacheEpoch ()) noReferences item text
                  | _ -> ()
                  outcomes[index] <- outcome
              return outcomes }
           // Classify the batch exception: provider rejections are final, transient request
          // failures stay retryable, and anything unexpected is treated as a request failure.
          Finish = fun work items results ->
              mergeSegments work.Boxes work.Segments
                  (Array.map2 (fun (entry: WorkSegment) (result: Result<SegmentOutcome, exn>) ->
                       match result with
                       | Ok outcome -> outcome
                       | Error error ->
                           let code =
                               match error with
                               | :? TranslationRateLimitedException -> "rate_limited"
                               | :? TranslationRequestRejectedException -> "translation_request_rejected"
                               | :? TimeoutException -> "translation_request_timeout"
                               | :? TransientTranslationException
                               | :? System.Net.Http.HttpRequestException -> "translation_request_failed"
                               | _ -> "translation_request_failed"
                           failure entry.Segment.Id code error.Message) items results) }

    /// Compatibility default preserves the prior concurrent policy. Traditional web providers
    /// must opt into the explicit stable strategy; self-hosted DeepLX remains unchanged.
    let segmentTranslator (common: CommonSettings) (job: ITranslationJob) (phase: string) (limit: int)
                          (translateOne: Segment -> CancellationToken -> Task<SegmentOutcome>) =
        segmentTranslatorWithSequential common false job phase limit translateOne

    let segmentTranslatorStable (common: CommonSettings) (job: ITranslationJob) (phase: string) (limit: int)
                                (translateOne: Segment -> CancellationToken -> Task<SegmentOutcome>) =
        segmentTranslatorWithSequential common true job phase limit translateOne

    let rateLimitFailurePolicy : JobFailurePolicy<SegmentOutcome> =
        { IsRateLimited = fun error -> error :? TranslationRateLimitedException
          IsRateLimitedResult = fun outcome -> outcome.ErrorCode = Some "rate_limited"
          RateLimitError = fun () -> TranslationRateLimitedException("Translation stopped after the provider rate limited a request.", None) }

    let runSegmentPages job (options: TranslationExtensionOptions) budget translator cancellationToken =
        ExecutionScope.run options.Concurrency (Job.progress job) cancellationToken (fun () -> task {
            do! Job.runPagesWithFailurePolicy rateLimitFailurePolicy job options budget translator cancellationToken })

    /// Opt-in policy for traditional web translators: serial pages and serial request batches.
    /// Ordinary segment engines and LLM callers retain the SDK driver's prior concurrency behavior.
    let runSegmentPagesStable job (options: TranslationExtensionOptions) budget translator cancellationToken =
        ExecutionScope.run options.Concurrency (Job.progress job) cancellationToken (fun () -> task {
            do! Job.runPagesSequentialWithFailurePolicy rateLimitFailurePolicy job options budget translator cancellationToken })
