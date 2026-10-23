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
    [<Field("Semantic Scholar paper endpoint", Key = "endpoint"); Default("https://api.semanticscholar.org/graph/v1/paper")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("semantic-scholar", "Semantic Scholar", HashSet<string>([| "semantic_scholar"; "doi"; "arxiv"; "pmid" |], StringComparer.OrdinalIgnoreCase), 50, true)
let parseCandidate (root: JsonElement) =
    let authors =
        let mutable list = JsonElement()
        if root.ValueKind = JsonValueKind.Object && root.TryGetProperty("authors", &list) &&
           list.ValueKind = JsonValueKind.Array then
            list.EnumerateArray()
            |> Seq.map (fun entry -> MetadataCreator(authorRole, Literal = (jsonString entry "name" |> Option.toObj)))
            |> Seq.toArray
        else Array.empty
    let year = jsonInt root "year"
    let identifiers =
        let mutable external = JsonElement()
        if root.ValueKind = JsonValueKind.Object && root.TryGetProperty("externalIds", &external) then
            identifierFields external [ ("DOI", "doi"); ("ArXiv", "arxiv"); ("PubMed", "pmid"); ("CorpusId", "semantic_scholar") ]
        else Array.empty
    let types = jsonStrings root "publicationTypes"
    let dates =
        match year with
        | Some value -> [| MetadataDate(issuedRole, [| value |]) |]
        | None -> Array.empty
    MetadataCandidate("semantic-scholar", jsonString root "title" |> Option.toObj,
        Creators = authors,
        Dates = dates,
        PublicationTitle = (jsonString root "venue" |> Option.toObj),
        Volume = (jsonChildString root "journal" "volume" |> Option.toObj),
        Pages = (jsonChildString root "journal" "pages" |> Option.toObj),
        Abstract = (jsonString root "abstract" |> Option.toObj),
        Identifiers = identifiers,
        SuggestedItemType = (types |> Array.tryHead |> Option.bind mapType |> Option.toObj),
        TypeConfidence = (if types.Length > 0 then 0.9 else 0.0))

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains(identifier.Scheme)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "Semantic Scholar supports semantic_scholar, DOI, arXiv, and PMID identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then
            invalidArg "endpoint" "Semantic Scholar endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then
            invalidArg "endpoint" "Semantic Scholar endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let prefix =
            if String.Equals(identifier.Scheme, "doi", StringComparison.OrdinalIgnoreCase) then "DOI:"
            elif String.Equals(identifier.Scheme, "arxiv", StringComparison.OrdinalIgnoreCase) then "ARXIV:"
            elif String.Equals(identifier.Scheme, "pmid", StringComparison.OrdinalIgnoreCase) then "PMID:"
            else String.Empty
        let url =
            endpoint + "/" + Uri.EscapeDataString(prefix + identifier.Value) +
            "?fields=title,abstract,authors,year,venue,journal,publicationTypes,externalIds"
        let! bodyResult = fetchSource resources http "Semantic Scholar" "application/json" (Uri(url)) cancellationToken
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
                    "Semantic Scholar response was invalid: " + ex.Message)
}

/// <summary>Semantic Scholar metadata extension.</summary>
/// <patchouli-extension id="metadata.semantic-scholar" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Semantic Scholar</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
