open System
open System.Text.Json
open Patchouli.Workflows
open Patchouli.Workflows.Scripting
open Patchouli.Agent.Sdk

type PageTarget = { document: string; page: int }
type DocumentPlan = { document: string; pages: int list }
type PageOutcome = Committed of operationId: string | Blocked of code: string * detail: string
type TranslationState =
    { Input: WorkflowInput
      Remaining: PageTarget list
      Results: (PageTarget * PageOutcome) list }

let info : WorkflowInfo =
    WorkflowInfo.create "全文翻译" "按文档和页码翻译并提交译文。"
    |> WorkflowInfo.selectionScope WorkflowSelectionScope.DocumentsAndPages
    |> WorkflowInfo.menu "Tools/Workflows" 10

let model = Parameter.model "model" "执行模型"
let documents = Parameter.documents "documents" "目标文档" |> Parameter.bindContext "documents"
let pageRange = Parameter.pageRange "pageRange" "页码范围" "" |> Parameter.bindContext "pageRange"
let targetLanguage = Parameter.language "targetLanguage" "目标语言" "en"
let windowRadius = Parameter.integer "windowRadius" "上下文窗口半径" 1 |> Parameter.intRange 0 5
let backfillPreviousWindowTranslation = Parameter.boolean "backfillPreviousWindowTranslation" "回填前一窗口译文" true

let selectedDocuments (input: WorkflowInput) =
    match Parameter.get documents input with
    | [] -> input.Documents
    | selected -> selected

let translationRules (input: WorkflowInput) =
    let pages = Parameter.get pageRange input
    let language = Parameter.get targetLanguage input
    let radius = Parameter.get windowRadius input
    let backfill = Parameter.get backfillPreviousWindowTranslation input
    """FULL-TEXT TRANSLATION RULES
Act through the existing agent tools. Discover real page resources before producing the page plan.
Translate requested pages in document/page order, including pages with existing translations.
Each translation turn owns only the specific page named by the workflow. Do not translate other pages in that turn.
Fetch that page and the sliding window of neighboring source pages; use the preceding translation for continuity.
Follow the launch window radius and backfill setting. Reuse previously fetched material where appropriate.
Preserve Markdown block structure, math, figures, citations and reference identifiers.
Persist the current page with commitTranslation({content = translatedMarkdown}). The page target is bound by the workflow and cannot be overridden.
Use native commitTranslation or AgentTools.commitTranslation in granted FSI; both use the same validation and write path.
Inspect the actual put result. Correct structure mismatches within the context retry limit.
Report whether this page was committed or blocked, then yield to the workflow for the next page.
The summary turn must report committed and failed page identifiers from the recorded tool outcomes.
Never claim completion before actually attempting the requested tools or explaining a concrete blocker.
""" +
    sprintf "Documents: %A; pages %s (empty means all); target language %s.\nWindow radius: %d; backfill previous window translation: %b."
        (selectedDocuments input) pages language radius backfill

let pageInRange (input: WorkflowInput) page =
    let range = Parameter.get pageRange input
    String.IsNullOrWhiteSpace range ||
    (range.Split(',') |> Array.exists (fun part ->
        match part.Trim().Split('-') |> Array.map (fun value -> Int32.TryParse(value.Trim())) with
        | [| (true, first) |] -> page = first
        | [| (true, first); (true, last) |] -> page >= first && page <= last
        | _ -> false))

let parsePlan (input: WorkflowInput) (text: string) =
    try
        use json = JsonDocument.Parse text
        let root = json.RootElement
        if root.ValueKind <> JsonValueKind.Array || root.GetArrayLength() = 0 || root.GetArrayLength() > 1024 then
            Error "Expected a non-empty JSON array of DocumentPlan records."
        else
            let targets = root.EnumerateArray() |> Seq.collect (fun item ->
                let fields = if item.ValueKind = JsonValueKind.Object then item.EnumerateObject() |> Seq.map (fun field -> field.Name) |> Seq.toList else []
                if List.sort fields <> [ "document"; "pages" ] then
                    invalidOp "DocumentPlan requires exactly document: string and pages: int list, without duplicate or unknown fields."
                let document = match item.GetProperty("document").GetString() with
                               | null -> "" | value -> value
                let pages = item.GetProperty("pages")
                if pages.ValueKind <> JsonValueKind.Array || pages.GetArrayLength() = 0 || pages.GetArrayLength() > 1024 then
                    invalidOp "DocumentPlan.pages must be a non-empty integer array of at most 1024 pages."
                let plan: DocumentPlan = { document = document; pages = pages.EnumerateArray() |> Seq.map (fun page -> page.GetInt32()) |> Seq.toList }
                plan.pages |> Seq.map (fun page -> { document = plan.document; page = page })) |> Seq.toList
            let documents = selectedDocuments input
            if targets.Length > 1024 then Error "The page plan cannot exceed 1024 targets."
            elif targets |> List.exists (fun target ->
                String.IsNullOrWhiteSpace target.document || target.page < 1 ||
                not (List.contains target.document documents) || not (pageInRange input target.page)) then
                Error "Every PageTarget must name a selected document and a positive page within the requested range."
            elif List.distinct targets <> targets then Error "The page plan must not contain duplicate targets."
            else
                let ordered = targets |> List.sortBy (fun target -> List.findIndex ((=) target.document) documents, target.page)
                Ok { Input = input; Remaining = ordered; Results = [] }
    with
    | :? JsonException as error -> Error ("Invalid page-plan JSON: " + error.Message)
    | :? InvalidOperationException as error -> Error ("Invalid DocumentPlan field type: " + error.Message)
    | :? Collections.Generic.KeyNotFoundException as error -> Error ("Missing DocumentPlan field: " + error.Message)
    | :? FormatException as error -> Error ("Invalid DocumentPlan page number: " + error.Message)

let discover =
    Agent.typed "translation-page-plan"
        """Use find/fetch to discover all requested real page resources. Do not translate yet.
Return only a compact JSON array matching this F# type: type DocumentPlan = { document: string; pages: int list }.
Example: [{"document":"document-id","pages":[1,2]}]. Use one record per document, not a repeated document ID for every page.
Include every requested page, with no duplicates or pages outside the selection."""
        (fun (_: WorkflowInput) -> "Discover the requested pages and return the page plan.") parsePlan
    |> Agent.withSharedInstructions translationRules
    |> Agent.withTools [ AgentTool.Find; AgentTool.Fetch; AgentTool.History; AgentTool.Fsi ]
    |> Agent.requireToolUse [ AgentTool.Fetch ]
    |> Agent.withBudget (AgentBudget.create 8 7)

let outcomeFor (target: PageTarget) (operations: SdkObservation[]) =
    let uri = LibraryUri.writable (WritableResource.TranslationPage(target.document, target.page))
    let writes = operations |> Array.filter (fun op ->
        if op.Name <> "put" then false else
        use arguments = JsonDocument.Parse op.Arguments
        arguments.RootElement.GetProperty("uri").GetString() = uri)
    match Array.tryLast writes with
    | Some op when op.Succeeded ->
        try
            use payload = JsonDocument.Parse op.Payload
            let committed = payload.RootElement.GetProperty("entries").EnumerateArray() |> Seq.exists (fun entry ->
                entry.GetProperty("uri").GetString() = uri && entry.GetProperty("committed").GetBoolean())
            if committed then Ok(Committed op.OperationId) else Error "The write receipt does not prove this page was committed."
        with _ -> Error "The write receipt is not a valid commit envelope."
    | Some op when op.ErrorCode = "SDK_OPERATION_UNKNOWN" -> Error "This page has an unknown write outcome; reconcile it before advancing."
    | Some op -> Ok(Blocked(op.ErrorCode, op.Payload))
    | None -> Error "No recorded commit attempt exists for the current page. Call commitTranslation before yielding."

let translatePage =
    Agent.verified "translate-page" "Translate and commit only the requested page; completion is validated from SDK receipts."
        (fun (state: TranslationState) ->
            let target = List.head state.Remaining
            sprintf "Translate document %s, page %d." target.document target.page)
        (fun state operations _ ->
            match state.Remaining with
            | target :: remaining -> outcomeFor target operations |> Result.map (fun outcome ->
                { state with Remaining = remaining; Results = (target, outcome) :: state.Results })
            | [] -> Error "No page remains to translate.")
    |> Agent.withSharedInstructions (fun state -> translationRules state.Input)
    |> Agent.withTools [ AgentTool.Fetch; AgentTool.History; AgentTool.Fsi ]
    |> Agent.withSdkTools (fun state ->
        let target = List.head state.Remaining
        [ LibraryTools.commitTranslation |> Tool.bind (WritableResource.TranslationPage(target.document, target.page)) |> Tool.export ])
    |> Agent.requireToolUse [ AgentTool.Fetch ]
    |> Agent.withBudget (AgentBudget.create 8 7)
    |> Agent.run

let summarize =
    Agent.text "translation-summary" "Summarize actual committed and failed pages from the recorded tool outcomes."
        (fun (state: TranslationState) ->
            let reports = state.Results |> List.rev |> List.map (fun (target, outcome) -> sprintf "%s/%d: %A" target.document target.page outcome)
            "Summarize committed and failed pages, checking the actual tool outcomes against these page reports:\n" + String.concat "\n" reports)
    |> Agent.withSharedInstructions (fun state -> translationRules state.Input)
    |> Agent.withTools [ AgentTool.History ]
    |> Agent.withBudget (AgentBudget.create 3 2)

let run : AgentWorkflow =
    workflow {
        step discover
        step (Workflow.repeatUntil 1024 (fun state -> List.isEmpty state.Remaining) translatePage)
        step summarize
    }
    |> Workflow.define
    |> Workflow.withModel model
