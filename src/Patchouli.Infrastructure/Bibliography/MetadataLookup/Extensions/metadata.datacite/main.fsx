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
    [<Field("DataCite DOI endpoint", Key = "endpoint"); Default("https://api.datacite.org/dois")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("datacite", "DataCite", HashSet<string>([| "doi" |], StringComparer.OrdinalIgnoreCase), 30, true)
let parseCandidate (attributes: JsonElement) =
    let title =
        let mutable titles = JsonElement()
        if attributes.ValueKind = JsonValueKind.Object && attributes.TryGetProperty("titles", &titles) &&
           titles.ValueKind = JsonValueKind.Array then
            titles.EnumerateArray() |> Seq.tryHead |> Option.bind (fun item -> jsonString item "title")
        else None
    let creators = jsonPeople attributes "creators"
    let year = jsonInt attributes "publicationYear"
    let subjects = objectValues attributes "subjects" "subject"
    let itemType = jsonChildString attributes "types" "resourceTypeGeneral"
    let identifiers = identifierFields attributes [ ("doi", "doi") ]
    let dates =
        match year with
        | Some value -> [| MetadataDate(issuedRole, [| value |]) |]
        | None -> Array.empty
    MetadataCandidate("datacite", Option.toObj title,
        Creators = creators,
        Dates = dates,
        Publisher = (jsonString attributes "publisher" |> Option.toObj),
        Language = (jsonString attributes "language" |> Option.toObj),
        Tags = subjects,
        Identifiers = identifiers,
        SuggestedItemType = (itemType |> Option.bind mapType |> Option.toObj),
        TypeConfidence = (if itemType.IsSome then 0.9 else 0.0))

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains(identifier.Scheme)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "DataCite supports DOI identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then
            invalidArg "endpoint" "DataCite endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then
            invalidArg "endpoint" "DataCite endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let url = endpoint + "/" + Uri.EscapeDataString(identifier.Value)
        let! bodyResult = fetchSource resources http "DataCite" "application/json" (Uri(url)) cancellationToken
        if bodyResult.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode bodyResult, failureMessage bodyResult)
        else
            try
                use document = JsonDocument.Parse(bodyResult.Value)
                let attributes = document.RootElement.GetProperty("data").GetProperty("attributes")
                let candidate = parseCandidate attributes
                if isEmptyCandidate candidate then
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                        "The metadata source response did not contain metadata or identifiers.")
                else
                    return Result<MetadataCandidate>.Success candidate
            with
            | :? OperationCanceledException as cancelled -> return raise cancelled
            | ex ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "DataCite response was invalid: " + ex.Message)
}

/// <summary>Datacite metadata extension.</summary>
/// <patchouli-extension id="metadata.datacite" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Datacite</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
