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
    [<Field("OpenAlex works endpoint", Key = "endpoint"); Default("https://api.openalex.org/works")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("openalex", "OpenAlex", HashSet<string>([| "doi"; "pmid"; "pmcid"; "mag"; "openalex" |], StringComparer.OrdinalIgnoreCase), 40, true)
let parseCandidate (root: JsonElement) =
    let authors =
        let mutable list = JsonElement()
        if root.ValueKind = JsonValueKind.Object && root.TryGetProperty("authorships", &list) &&
           list.ValueKind = JsonValueKind.Array then
            list.EnumerateArray()
            |> Seq.map (fun entry ->
                let mutable author = JsonElement()
                let authorElement = if entry.TryGetProperty("author", &author) then author else JsonElement()
                MetadataCreator(authorRole, Literal = (jsonString authorElement "display_name" |> Option.toObj)))
            |> Seq.toArray
        else Array.empty
    let location =
        let mutable primaryLocation = JsonElement()
        let mutable sourceElement = JsonElement()
        if root.TryGetProperty("primary_location", &primaryLocation) &&
           primaryLocation.TryGetProperty("source", &sourceElement) then sourceElement
        else JsonElement()
    let identifiers =
        let mutable ids = JsonElement()
        if root.TryGetProperty("ids", &ids) then
            // Each ids value has its own shape: doi is a full https://doi.org/ URL whose path IS
            // the DOI (it contains a slash, so last-segment truncation would corrupt it), pmid is
            // a PubMed URL, and openalex/mag/pmcid values are single-segment identifiers.
            let stripPrefix (prefix: string) (value: string) =
                if value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                    value.Substring(prefix.Length)
                else value
            let normalizeId (identifier: MetadataIdentifier) =
                let value =
                    match identifier.Scheme with
                    | "doi" -> identifier.Value |> stripPrefix "https://doi.org/" |> stripPrefix "http://doi.org/"
                    | "pmid" | "pmcid" -> lastPath identifier.Value
                    | "openalex" -> lastPath identifier.Value
                    | _ -> identifier.Value
                MetadataIdentifier(identifier.Scheme, value)
            identifierFields ids [ ("doi", "doi"); ("pmid", "pmid"); ("pmcid", "pmcid"); ("mag", "mag"); ("openalex", "openalex") ]
            |> Array.map normalizeId
        else Array.empty
    let year = jsonInt root "publication_year"
    let itemType = jsonString root "type"
    let dates =
        match year with
        | Some value -> [| MetadataDate(issuedRole, [| value |]) |]
        | None -> Array.empty
    MetadataCandidate("openalex",
        // OpenAlex guarantees display_name; title can be null on some records.
        (match jsonString root "title" with
         | Some value -> Some value
         | None -> jsonString root "display_name") |> Option.toObj,
        Creators = authors,
        Dates = dates,
        PublicationTitle = (jsonString location "display_name" |> Option.toObj),
        Volume = (jsonChildString root "biblio" "volume" |> Option.toObj),
        Issue = (jsonChildString root "biblio" "issue" |> Option.toObj),
        Pages = (pageRange root |> Option.toObj),
        Language = (jsonString root "language" |> Option.toObj),
        Identifiers = identifiers,
        SuggestedItemType = (itemType |> Option.bind mapType |> Option.toObj),
        TypeConfidence = (if itemType.IsSome then 0.9 else 0.0))

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains(identifier.Scheme)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "OpenAlex supports DOI, PMID, PMCID, MAG, and OpenAlex identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then
            invalidArg "endpoint" "OpenAlex endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then
            invalidArg "endpoint" "OpenAlex endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let value =
            if String.Equals(identifier.Scheme, "doi", StringComparison.OrdinalIgnoreCase) then "https://doi.org/" + identifier.Value
            elif String.Equals(identifier.Scheme, "pmid", StringComparison.OrdinalIgnoreCase) then "pmid:" + identifier.Value
            elif String.Equals(identifier.Scheme, "pmcid", StringComparison.OrdinalIgnoreCase) then "pmcid:" + identifier.Value
            elif String.Equals(identifier.Scheme, "mag", StringComparison.OrdinalIgnoreCase) then "mag:" + identifier.Value
            else identifier.Value
        let url = endpoint + "/" + Uri.EscapeDataString(value)
        let! bodyResult = fetchSource resources http "OpenAlex" "application/json" (Uri(url)) cancellationToken
        if bodyResult.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode bodyResult, failureMessage bodyResult)
        else
            try
                use document = JsonDocument.Parse(bodyResult.Value)
                let candidate = parseCandidate document.RootElement
                if isEmptyCandidate candidate then
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                        "The metadata source response did not contain metadata or identifiers.")
                else
                    return Result<MetadataCandidate>.Success candidate
            with
            | :? OperationCanceledException as cancelled -> return raise cancelled
            | ex ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "OpenAlex response was invalid: " + ex.Message)
}

/// <summary>Openalex metadata extension.</summary>
/// <patchouli-extension id="metadata.openalex" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Openalex</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
