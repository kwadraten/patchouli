open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Core.Bibliography.MetadataLookup
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Settings>]
type Settings = {
    [<Field("Crossref works endpoint", Key = "endpoint"); Default("https://api.crossref.org/works")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("crossref", "Crossref", HashSet<string>([| "doi" |], StringComparer.OrdinalIgnoreCase), 20, true)
let stringAt (element: JsonElement) (property: string) =
    let mutable value = JsonElement()
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) && value.ValueKind = JsonValueKind.String then
        value.GetString() |> Option.ofObj
    else None

let firstString (element: JsonElement) (property: string) =
    let mutable value = JsonElement()
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) && value.ValueKind = JsonValueKind.Array then
        value.EnumerateArray() |> Seq.tryPick (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None)
    else None

let strings (element: JsonElement) (property: string) =
    let mutable value = JsonElement()
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) && value.ValueKind = JsonValueKind.Array then
        value.EnumerateArray() |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None) |> Seq.toArray
    else Array.empty

let dateAt (element: JsonElement) (property: string) =
    let mutable value = JsonElement()
    let mutable dateParts = JsonElement()
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) &&
       value.ValueKind = JsonValueKind.Object && value.TryGetProperty("date-parts", &dateParts) &&
       dateParts.ValueKind = JsonValueKind.Array && dateParts.GetArrayLength() > 0 && dateParts[0].ValueKind = JsonValueKind.Array then
        let parts = dateParts[0].EnumerateArray() |> Seq.choose (fun part ->
            let mutable integer = 0
            if part.TryGetInt32(&integer) then Some integer else None) |> Seq.toArray
        if parts.Length > 0 then Some (MetadataDate("issued", parts)) else None
    else None

let creator (person: JsonElement) =
    let family = stringAt person "family"
    let given = stringAt person "given"
    let literal = stringAt person "name"
    if family.IsNone && given.IsNone && literal.IsNone then None
    else Some (MetadataCreator("author", Option.toObj family, Option.toObj given, Option.toObj literal))

let parseCandidate (requested: NormalizedIdentifier) (message: JsonElement) =
    let mutable authors = JsonElement()
    let creators =
        if message.TryGetProperty("author", &authors) && authors.ValueKind = JsonValueKind.Array then
            authors.EnumerateArray() |> Seq.choose creator |> Seq.toArray
        else Array.empty
    let dates = [ "published-print"; "published-online"; "issued" ] |> List.tryPick (dateAt message)
    let identifiers = ResizeArray<MetadataIdentifier>()
    match stringAt message "DOI" with
    | Some doi -> identifiers.Add(MetadataIdentifier("doi", doi))
    | None -> identifiers.Add(MetadataIdentifier("doi", requested.Value))
    for isbn in strings message "ISBN" do identifiers.Add(MetadataIdentifier("isbn", isbn))
    let title = firstString message "title"
    let ``type`` = stringAt message "type"
    let suggestedType =
        match ``type`` with
        | Some "journal-article" | Some "article" | Some "article-journal" -> Some "article-journal"
        | Some "book" | Some "monograph" -> Some "book"
        | Some "book-chapter" -> Some "chapter"
        | Some "proceedings-article" | Some "conference" | Some "conference-paper" -> Some "paper-conference"
        | Some "dissertation" | Some "thesis" -> Some "thesis"
        | Some "report" -> Some "report"
        | Some "posted-content" | Some "preprint" -> Some "manuscript"
        | _ -> None
    let confidence = if suggestedType.IsSome then 0.95 else 0.0
    MetadataCandidate("crossref", Option.toObj title,
        Option.toObj (firstString message "subtitle"), null, creators,
        (dates |> Option.toList |> List.toArray), firstString message "container-title" |> Option.toObj,
        null, null, stringAt message "publisher" |> Option.toObj, null, null, null, null, null,
        stringAt message "volume" |> Option.toObj, null, stringAt message "issue" |> Option.toObj,
        stringAt message "page" |> Option.toObj, stringAt message "language" |> Option.toObj, null, null,
        stringAt message "abstract" |> Option.toObj, strings message "subject", identifiers.ToArray(),
        Option.toObj suggestedType, confidence)

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (String.Equals(identifier.Scheme, "doi", StringComparison.OrdinalIgnoreCase)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier, "Crossref supports DOI identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Crossref endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Crossref endpoint must use HTTP or HTTPS."
        let http = resources.Http
        do! resources.WaitToProceedAsync(cancellationToken)
        let uri = Uri(endpoint + "/" + Uri.EscapeDataString(identifier.Value))
        use request = new HttpRequestMessage(HttpMethod.Get, uri)
        request.Headers.Accept.ParseAdd("application/json")
        use! response = http.SendAsync(request, cancellationToken)
        let statusCode = int response.StatusCode
        if statusCode = 404 then
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "Crossref did not find this DOI.")
        elif statusCode = 429 then
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.RateLimited, "Crossref rate limited the lookup.")
        elif not response.IsSuccessStatusCode then
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable, "Crossref returned HTTP " + string statusCode + ".")
        else
            let! body = response.Content.ReadAsStringAsync(cancellationToken)
            use document = JsonDocument.Parse(body)
            let mutable message = JsonElement()
            if not (document.RootElement.TryGetProperty("message", &message)) || message.ValueKind <> JsonValueKind.Object then
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse, "Crossref response has no message object.")
            else
                return Result<MetadataCandidate>.Success(parseCandidate identifier message)
}

/// <summary>Crossref metadata extension.</summary>
/// <patchouli-extension id="metadata.crossref" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Crossref</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
