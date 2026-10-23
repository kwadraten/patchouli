#load "Support.fsx"
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Patchouli.Core.Bibliography.MetadataLookup
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInMetadata.Support

[<Settings>]
type Settings = {
    [<Field("Google Books endpoint", Key = "endpoint"); Default("https://www.googleapis.com/books/v1/volumes")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("google-books", "Google Books", HashSet<string>([| "isbn"; "google_books" |], StringComparer.OrdinalIgnoreCase), 20, true)
let parseVolume (root: JsonElement) =
    let mutable current = root
    let mutable items = JsonElement()
    if current.ValueKind = JsonValueKind.Object && current.TryGetProperty("items", &items) then
        current <- items.EnumerateArray() |> Seq.tryHead |> Option.defaultValue (JsonElement())
    if current.ValueKind <> JsonValueKind.Object then None
    else
        let mutable info = JsonElement()
        if not (current.TryGetProperty("volumeInfo", &info)) || info.ValueKind <> JsonValueKind.Object then None
        else
            let volumeId = jsonString current "id"
            let authors =
                jsonStrings info "authors" |> Array.map (fun name -> MetadataCreator(authorRole, Literal = name))
            let identifiers = ResizeArray<MetadataIdentifier>()
            match volumeId with
            | Some id when not (String.IsNullOrWhiteSpace id) -> identifiers.Add(MetadataIdentifier("google_books", id))
            | _ -> ()
            let mutable industryIdentifiers = JsonElement()
            if info.TryGetProperty("industryIdentifiers", &industryIdentifiers) then
                for entry in industryIdentifiers.EnumerateArray() do
                    match jsonString entry "type" with
                    | Some kind when kind.StartsWith("ISBN", StringComparison.Ordinal) ->
                        match jsonString entry "identifier" with
                        | Some identifier -> identifiers.Add(MetadataIdentifier("isbn", identifier))
                        | None -> ()
                    | _ -> ()
            let mutable pages = JsonElement()
            let pagesText = if info.TryGetProperty("pageCount", &pages) then jsonScalar pages else None
            Some (MetadataCandidate("google-books", jsonString info "title" |> Option.toObj,
                Subtitle = (jsonString info "subtitle" |> Option.toObj),
                Creators = authors,
                Dates = (match jsonString info "publishedDate" with Some value -> literalDate value | None -> Array.empty),
                Publisher = (jsonString info "publisher" |> Option.toObj),
                Pages = (pagesText |> Option.toObj),
                Language = (jsonString info "language" |> Option.toObj),
                Abstract = (jsonString info "description" |> Option.toObj),
                Tags = jsonStrings info "categories",
                Identifiers = identifiers.ToArray(),
                SuggestedItemType = "book",
                TypeConfidence = 0.95))

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains identifier.Scheme) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "Google Books supports ISBN and Google Books volume identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Google Books endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Google Books endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let url =
            if String.Equals(identifier.Scheme, "google_books", StringComparison.OrdinalIgnoreCase) then
                endpoint + "/" + Uri.EscapeDataString(identifier.Value)
            else
                endpoint + "?q=isbn:" + Uri.EscapeDataString(identifier.Value) + "&maxResults=1"
        let! fetched = fetchSource resources http "Google Books" "application/json" (Uri(url)) cancellationToken
        if fetched.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
        else
            try
                use document = JsonDocument.Parse(fetched.Value)
                match parseVolume document.RootElement with
                | Some candidate ->
                    return Result<MetadataCandidate>.Success(candidate)
                | None ->
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                        "Google Books did not find a matching record.")
            with
            | ex when (ex :? JsonException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "Google Books response was invalid: " + ex.Message)
}

/// <summary>Google Books metadata extension.</summary>
/// <patchouli-extension id="metadata.google-books" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Google Books</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
