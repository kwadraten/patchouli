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
    [<Field("NDL Search endpoint", Key = "endpoint"); Default("https://ndlsearch.ndl.go.jp/api/opensearch")>] Endpoint: string
    [<Field("NDL IIIF endpoint", Key = "iiif-endpoint"); Default("https://dl.ndl.go.jp/api/iiif")>] IiifEndpoint: string
}

let sourceDefinition = MetadataSourceDefinition("ndl", "National Diet Library", HashSet<string>([| "ndlbibid"; "jpno"; "ndljp"; "isbn" |], StringComparer.OrdinalIgnoreCase), 5, true)
let parseIiif (root: JsonElement) (idValue: string) =
    let mutable values = JsonElement()
    let metadata = if root.TryGetProperty("metadata", &values) then values else JsonElement()
    let find (label: string) =
        if metadata.ValueKind = JsonValueKind.Array then
            metadata.EnumerateArray()
            |> Seq.tryFind (fun entry ->
                match jsonString entry "label" with
                | Some text -> String.Equals(text, label, StringComparison.OrdinalIgnoreCase)
                | None -> false)
            |> Option.bind (fun entry -> jsonString entry "value")
        else None
    let findEither first second =
        match find first with
        | Some value -> Some value
        | None -> find second
    let creators =
        match findEither "Creator" "著者" with
        | Some value -> jsonLiteralPeople value
        | None -> Array.empty
    MetadataCandidate("ndl", jsonString root "label" |> Option.toObj,
        Creators = creators,
        Dates = (match findEither "Date" "出版年月日" with Some value -> literalDate value | None -> Array.empty),
        Publisher = (findEither "Publisher" "出版者" |> Option.toObj),
        Language = (findEither "Language" "言語" |> Option.toObj),
        Note = (jsonString root "description" |> Option.toObj),
        Identifiers = [| MetadataIdentifier("ndljp", idValue) |])

let validateEndpoint (name: string) (endpoint: string) =
    if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" (name + " endpoint must be an absolute HTTP(S) URL.")
    let baseUri = Uri(endpoint)
    if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" (name + " endpoint must use HTTP or HTTPS.")

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains identifier.Scheme) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "National Diet Library supports NDL Bib ID, JPNO, NDL JP, and ISBN identifiers.")
    else
        let http = resources.Http
        if String.Equals(identifier.Scheme, "ndljp", StringComparison.OrdinalIgnoreCase) then
            let iiifEndpoint = settings.IiifEndpoint |> fun value -> value.Trim().TrimEnd('/')
            validateEndpoint "National Diet Library IIIF" iiifEndpoint
            let url = iiifEndpoint + "/" + Uri.EscapeDataString(identifier.Value) + "/manifest.json"
            let! fetched = fetchSource resources http "National Diet Library" "application/json" (Uri(url)) cancellationToken
            if fetched.IsFailure then
                return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
            else
                try
                    use document = JsonDocument.Parse(fetched.Value)
                    return Result<MetadataCandidate>.Success(parseIiif document.RootElement identifier.Value)
                with
                | ex when (ex :? JsonException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                        "National Diet Library response was invalid: " + ex.Message)
        else
            let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
            validateEndpoint "National Diet Library" endpoint
            let url =
                if String.Equals(identifier.Scheme, "isbn", StringComparison.OrdinalIgnoreCase) then
                    endpoint + "?cnt=10&isbn=" + Uri.EscapeDataString(identifier.Value)
                else
                    endpoint + "?cnt=10&any=" + Uri.EscapeDataString(identifier.Value)
            let! fetched = fetchSource resources http "National Diet Library" "application/json" (Uri(url)) cancellationToken
            if fetched.IsFailure then
                return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
            else
                try
                    let document = XDocument.Parse(fetched.Value)
                    match parseDublinCore document identifier with
                    | Some candidate ->
                        return Result<MetadataCandidate>.Success(
                            MetadataCandidate("ndl", candidate.Title, candidate.Subtitle, candidate.TitleShort,
                                candidate.Creators, candidate.Dates, candidate.PublicationTitle,
                                candidate.ContainerTitleShort, candidate.CollectionTitle, candidate.Publisher,
                                candidate.Place, candidate.Edition, candidate.Genre, candidate.Number,
                                candidate.ChapterNumber, candidate.Volume, candidate.Version, candidate.Issue,
                                candidate.Pages, candidate.Language, candidate.Status, candidate.Note,
                                candidate.Abstract, candidate.Tags, candidate.Identifiers,
                                candidate.SuggestedItemType, candidate.TypeConfidence))
                    | None ->
                        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                            "National Diet Library did not find a matching record.")
                with
                | ex when (ex :? System.Xml.XmlException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                        "National Diet Library response was invalid: " + ex.Message)
}

/// <summary>Ndl metadata extension.</summary>
/// <patchouli-extension id="metadata.ndl" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Ndl</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
