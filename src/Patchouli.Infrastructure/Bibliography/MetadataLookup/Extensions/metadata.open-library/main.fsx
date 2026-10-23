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
    [<Field("Open Library endpoint", Key = "endpoint"); Default("https://openlibrary.org/api/books")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("open-library", "Open Library", HashSet<string>([| "isbn"; "oclc"; "lccn"; "olid" |], StringComparer.OrdinalIgnoreCase), 10, true)
let parseBook (book: JsonElement) =
    let authors = objectNames book "authors" authorRole
    let publishers = objectValues book "publishers" "name"
    let subjects = objectValues book "subjects" "name"
    let identifiers = ResizeArray<MetadataIdentifier>()
    let mutable identifierObject = JsonElement()
    if book.ValueKind = JsonValueKind.Object && book.TryGetProperty("identifiers", &identifierObject) &&
       identifierObject.ValueKind = JsonValueKind.Object then
        identifiers.AddRange(identifierFields identifierObject
            [ ("isbn_10", "isbn"); ("isbn_13", "isbn"); ("oclc", "oclc"); ("lccn", "lccn") ])
    match jsonString book "key" with
    | Some key -> identifiers.Add(MetadataIdentifier("olid", lastPath (key.Trim('/'))))
    | None -> ()
    let mutable pages = JsonElement()
    let pagesText = if book.TryGetProperty("number_of_pages", &pages) then jsonScalar pages else None
    MetadataCandidate("open-library", jsonString book "title" |> Option.toObj,
        Subtitle = (jsonString book "subtitle" |> Option.toObj),
        Creators = authors,
        Dates = (match jsonString book "publish_date" with Some value -> literalDate value | None -> Array.empty),
        Publisher = (publishers |> Array.tryHead |> Option.toObj),
        Place = (objectValues book "publish_places" "name" |> Array.tryHead |> Option.toObj),
        Pages = (pagesText |> Option.toObj),
        Tags = subjects,
        Identifiers = identifiers.ToArray(),
        SuggestedItemType = "book",
        TypeConfidence = 0.95)

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains identifier.Scheme) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "Open Library supports ISBN, OCLC, LCCN, and OLID identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Open Library endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Open Library endpoint must use HTTP or HTTPS."
        let prefix =
            match identifier.Scheme with
            | "isbn" -> "ISBN"
            | "oclc" -> "OCLC"
            | "lccn" -> "LCCN"
            | _ -> "OLID"
        let http = resources.Http
        let url = endpoint + "?bibkeys=" + prefix + ":" + Uri.EscapeDataString(identifier.Value) + "&jscmd=data&format=json"
        let! fetched = fetchSource resources http "Open Library" "application/json" (Uri(url)) cancellationToken
        if fetched.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
        else
            try
                use document = JsonDocument.Parse(fetched.Value)
                let book = document.RootElement.EnumerateObject() |> Seq.map (fun property -> property.Value) |> Seq.tryHead
                match book with
                | Some element when element.ValueKind = JsonValueKind.Object ->
                    return Result<MetadataCandidate>.Success(parseBook element)
                | _ ->
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                        "Open Library did not find a matching record.")
            with
            | ex when (ex :? JsonException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "Open Library response was invalid: " + ex.Message)
}

/// <summary>Open Library metadata extension.</summary>
/// <patchouli-extension id="metadata.open-library" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Open Library</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
