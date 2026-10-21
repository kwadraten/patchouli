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
open Patchouli.Core.Ids
open Patchouli.Core.Documents
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

module Support =
    let private tableJsonOptions =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        options

    let batchSizeBinding = Config.bind "batch-size" (Field.integer "Batch size" 8 |> Field.range 1 128)
    let concurrencyBinding = Config.bind "concurrency" (Field.integer "Parallel requests" 4 |> Field.range 1 32)
    let maxCharactersBinding = Config.bind "max-batch-characters" (Field.integer "Maximum batch characters" 24000 |> Field.range 1 1000000)
    let maxTokensBinding = Config.bind "max-batch-tokens" (Field.integer "Maximum output tokens" 0 |> Field.range 0 1000000)
    let requestIntervalBinding = Config.bind "request-interval" (Field.decimal "Request interval (seconds)" 0M |> Field.decimalRange 0M 3600M)
    let requestTimeoutBinding = Config.bind "request-timeout" (Field.decimal "Request timeout (seconds)" 120M |> Field.decimalRange 1M 3600M)
    let maxRetriesBinding = Config.bind "max-retries" (Field.integer "Maximum retries" 2 |> Field.range 0 10)
    let sourceLanguageBinding = Config.bind "source-language" (Field.language "Source language" "auto")
    let targetLanguageBinding = Config.bind "target-language" (Field.language "Target language fallback" "")
    let cacheBinding = Config.bind "cache-enabled" (Field.boolean "Cache successful translations" true)
    let boxTypesBinding = Config.bind "box-types" (Field.text "Box type filter" "" |> Field.describe "Optional comma-separated box type IDs to translate.")
    let sharedBindings = [ Config.asField batchSizeBinding; Config.asField concurrencyBinding; Config.asField maxCharactersBinding
                           Config.asField maxTokensBinding; Config.asField requestIntervalBinding; Config.asField requestTimeoutBinding
                           Config.asField maxRetriesBinding; Config.asField sourceLanguageBinding; Config.asField targetLanguageBinding
                           Config.asField cacheBinding; Config.asField boxTypesBinding ]
    let llmBindings = sharedBindings

    let private translationCache = ConcurrentDictionary<string, string>(StringComparer.Ordinal)
    let private translationCacheOrder = ConcurrentQueue<string>()
    let mutable private cacheEpoch = 0L

    let clearDerivedState (cancellationToken: CancellationToken) = task {
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

    let sharedFields () = Config.fields sharedBindings

    let option (context: TranslationExtensionContext) =
        match context.Options with
        | null -> TranslationExtensionOptions()
        | options -> options

    let sourceLanguage (context: TranslationExtensionContext) =
        if String.IsNullOrWhiteSpace context.SourceLanguage || String.Equals(context.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) then
            let configured = Binding.get sourceLanguageBinding context.Config
            if String.IsNullOrWhiteSpace configured then "auto" else configured
        else context.SourceLanguage

    let targetLanguage (context: TranslationExtensionContext) =
        if String.IsNullOrWhiteSpace context.TargetLanguage then
            let configured = Binding.get targetLanguageBinding context.Config
            if String.IsNullOrWhiteSpace configured then context.TargetLanguage else configured
        else context.TargetLanguage

    let filteredBoxes (context: TranslationExtensionContext) (boxes: ExtensionBox array) =
        let configured = Binding.get boxTypesBinding context.Config
        if String.IsNullOrWhiteSpace configured then boxes
        else
            let selected = Set.ofArray (configured.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
            boxes |> Array.filter (fun box -> selected.Contains(box.Type))

    let translatableBoxes (context: TranslationExtensionContext) =
        let parentIds = HashSet<DocumentBoxId>(
            context.Boxes |> Seq.choose (fun box -> if box.ParentId.HasValue then Some box.ParentId.Value else None))
        let allowedTypes = set [ "text"; "title"; "ref_text"; "list"; "table"; "algorithm"; "index"; "header"; "footer";
                                "aside_text"; "page_footnote"; "image_caption"; "image_footnote"; "table_caption";
                                "table_footnote"; "chart_caption"; "chart_footnote"; "code_caption"; "code_footnote" ]
        context.Boxes
        |> Seq.filter (fun box -> not box.Suppressed && not (parentIds.Contains(box.Id)) && allowedTypes.Contains(box.Type)
                                  && not (String.IsNullOrWhiteSpace box.Text))
        |> Seq.toArray

    let glossary (context: TranslationExtensionContext) =
        let configured =
            match context.Config.Values.TryGetValue("glossary") with
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
            match context.Options with
            | null -> []
            | options ->
                match options.Glossary with
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

    let effectiveOptions (context: TranslationExtensionContext) =
        let run = option context
        let has key = context.Config.Values.ContainsKey(key)
        let batchSize = if has "batch-size" then Binding.get batchSizeBinding context.Config else run.BatchSize
        let concurrency = if has "concurrency" then Binding.get concurrencyBinding context.Config else run.Concurrency
        let maxCharacters = if has "max-batch-characters" then Binding.get maxCharactersBinding context.Config else run.MaxBatchCharacters
        let maxTokens = if has "max-batch-tokens" then
                            let value = Binding.get maxTokensBinding context.Config
                            if value <= 0 then Nullable<int>() else Nullable<int>(value)
                        else run.MaxBatchTokens
        let interval = if has "request-interval" then Nullable<TimeSpan>(TimeSpan.FromSeconds(float (Binding.get requestIntervalBinding context.Config))) else run.RequestInterval
        let timeout = if has "request-timeout" then Nullable<TimeSpan>(TimeSpan.FromSeconds(float (Binding.get requestTimeoutBinding context.Config))) else run.RequestTimeout
        let retries = if has "max-retries" then Binding.get maxRetriesBinding context.Config else run.MaxRetries
        let radius =
            match context.Config.Values.TryGetValue("window-radius") with
            | true, value ->
                match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, parsed -> Math.Clamp(parsed, 0, 5)
                | _ -> run.WindowRadius
            | _ -> run.WindowRadius
        let backfill =
            match context.Config.Values.TryGetValue("backfill") with
            | true, value ->
                match Boolean.TryParse(value) with
                | true, parsed -> parsed
                | _ -> run.BackfillPreviousWindowTranslation
            | _ -> run.BackfillPreviousWindowTranslation
        TranslationExtensionOptions(batchSize, maxCharacters, maxTokens, concurrency, interval, timeout, retries,
            radius, backfill, run.DocumentTitle, run.Glossary, run.PromptContext, run.ProviderId, run.Model)

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

    let private translationCacheKey (context: TranslationExtensionContext) (phase: string) (epoch: int64) (segment: Segment) =
        let options = effectiveOptions context
        use stream = new System.IO.MemoryStream()
        use writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true)
        let write (value: string | null) =
            writer.Write(value |> Option.ofObj |> Option.defaultValue String.Empty)
        write phase
        writer.Write(epoch)
        let fields: (string | null) list =
            [ sourceLanguage context; targetLanguage context; segment.Box.Type; segment.Text
              options.DocumentTitle; options.PromptContext; options.ProviderId; options.Model
              string options.MaxBatchTokens ]
        fields |> List.iter write
        let configuration = context.Config.Values |> Seq.sortBy (fun pair -> pair.Key) |> Seq.toArray
        writer.Write(configuration.Length)
        for pair in configuration do
            write pair.Key
            write pair.Value
        let references =
            context.ReferenceContexts |> Option.ofObj |> Option.map Seq.toArray |> Option.defaultValue [||]
        writer.Write(references.Length)
        for reference in references do write reference
        let terms = glossary context
        writer.Write(terms.Length)
        for term in terms do write term
        writer.Write(context.Boxes.Count)
        for box in context.Boxes do
            write box.Type
            write box.Text
        writer.Flush()
        Convert.ToHexString(SHA256.HashData(stream.ToArray()))

    let tryGetCachedTranslation (context: TranslationExtensionContext) phase epoch segment =
        let mutable text = ""
        if Binding.get cacheBinding context.Config && captureCacheEpoch () = epoch
           && translationCache.TryGetValue(translationCacheKey context phase epoch segment, &text) then Some text
        else None

    let cacheTranslation (context: TranslationExtensionContext) phase epoch segment text =
        if Binding.get cacheBinding context.Config && captureCacheEpoch () = epoch then
            cacheSuccess (translationCacheKey context phase epoch segment) text

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

    let configText key fallback (context: TranslationExtensionContext) =
        match context.Config.Values.TryGetValue(key) with
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

    let timeoutToken (options: TranslationExtensionOptions) (cancellationToken: CancellationToken) =
        let source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        if options.RequestTimeout.HasValue && options.RequestTimeout.Value > TimeSpan.Zero then
            source.CancelAfter(options.RequestTimeout.Value)
        source

    let pace (capabilities: IExtensionCapabilities) (options: TranslationExtensionOptions) cancellationToken = task {
        do! capabilities.WaitToProceedAsync(cancellationToken)
    }

    exception TransientTranslationException of string

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
        if statusCode = 429 || statusCode >= 500 && statusCode <= 599 then
            raise (TransientTranslationException(provider + " returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + "."))
        if statusCode < 200 || statusCode >= 300 then
            invalidOp (provider + " returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + ".")

    let report (context: TranslationExtensionContext) completed total phase =
        match context.Progress with
        | null -> ()
        | progress -> progress.Report(ExtensionProgress(phase, completed, total))

    let translateBySegmentsWithCharacterLimit (context: TranslationExtensionContext) phase characterLimit translateOne cancellationToken = task {
        let options = effectiveOptions context
        let maxCharacters = min options.MaxBatchCharacters characterLimit
        let boxes = translatableBoxes context
        let eligible = boxes |> filteredBoxes context
        let segments = eligible |> Array.collect (segment maxCharacters)
        let batches = buildSegmentBatches segments options.BatchSize maxCharacters options.MaxBatchTokens
        let epoch = captureCacheEpoch ()
        let cacheMisses = ConcurrentDictionary<string, Segment>(StringComparer.Ordinal)
        let mutable completed = 0
        let translateBatch (batch: Segment array) ct = task {
            let outcomes = ResizeArray<SegmentOutcome>()
            for segment in batch do
                try
                    let cachedText = tryGetCachedTranslation context phase epoch segment
                    let! outcome =
                        if segment.ErrorCode.IsSome then
                            Task.FromResult(failure segment.Id segment.ErrorCode.Value
                                (Option.defaultValue "The table is invalid." segment.ErrorMessage))
                        elif segment.Text.Length > maxCharacters then
                            Task.FromResult(failure segment.Id "translation_segment_exceeds_limit"
                                "A protected code or formula unit exceeds the provider's request character limit.")
                        elif cachedText.IsSome then
                            Task.FromResult(translated segment.Id cachedText.Value)
                        else
                            if segment.ErrorCode.IsNone then cacheMisses[segment.Id] <- segment
                            let pipeline = Step.sync id |>> Step.async (fun value token -> translateOne value token)
                            Pipeline.run context.Progress phase pipeline segment ct
                    let current = Interlocked.Increment(&completed)
                    report context current segments.Length phase
                    outcomes.Add(outcome)
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> raise (OperationCanceledException(ct))
                | error ->
                    let outcome = failure segment.Id "translation_request_failed" error.Message
                    let current = Interlocked.Increment(&completed)
                    report context current segments.Length phase
                    outcomes.Add(outcome)
            return outcomes.ToArray()
        }
        let! mapped = Parallel.map options.Concurrency translateBatch batches cancellationToken
        let outcomes = mapped |> Array.collect id
        let mergedOutcomes = mergeSegments boxes segments outcomes
        let translatedBoxes =
            HashSet<DocumentBoxId>(
                mergedOutcomes
                |> Seq.filter (fun outcome -> outcome.Kind = ExtensionBoxOutcomeKind.Translated)
                |> Seq.map (fun outcome -> outcome.BoxId))
        for outcome in outcomes do
            match cacheMisses.TryGetValue(outcome.SegmentId), outcome.TranslatedText with
            | (true, segment), Some text when translatedBoxes.Contains(segment.Box.Id) && outcome.Kind = ExtensionBoxOutcomeKind.Translated ->
                cacheTranslation context phase epoch segment text
            | _ -> ()
        return TranslationExtensionResult(mergedOutcomes)
    }

    let translateBySegments context phase translateOne cancellationToken =
        translateBySegmentsWithCharacterLimit context phase Int32.MaxValue translateOne cancellationToken

    let private jsonOptions =
        let options = JsonSerializerOptions()
        options.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        options

    let jsonBody<'T> (value: 'T) = JsonSerializer.Serialize(value, jsonOptions)

    let sendJson (capabilities: IExtensionCapabilities) (http: IHttpCapability) uri body options cancellationToken credentialReference extraHeaders = task {
        do! pace capabilities options cancellationToken
        use timeout = timeoutToken options cancellationToken
        let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        headers["Content-Type"] <- "application/json"
        for key, value in extraHeaders do headers[key] <- value
        let request = HttpRequest("POST", Uri(uri), headers, body,
            (if String.IsNullOrWhiteSpace credentialReference then null else credentialReference))
        let! response = http.SendAsync(request, timeout.Token)
        ensureHttpSuccess "Translation service" response.StatusCode
        return response.Body
    }

    let parseStringAt (element: JsonElement) (property: string) =
        let mutable value = JsonElement()
        if element.TryGetProperty(property, &value) && value.ValueKind = JsonValueKind.String then
            value.GetString() |> Option.ofObj
        else None

