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
    [<Field("PMC ID converter endpoint", Key = "endpoint"); Default("https://pmc.ncbi.nlm.nih.gov/tools/idconv/api/v1/articles/")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("pmc-id-converter", "PMC ID Converter", HashSet<string>([| "doi"; "pmid"; "pmcid"; "mid" |], StringComparer.OrdinalIgnoreCase), 15, true)
let parseCandidate (root: JsonElement) =
    let records = root.GetProperty("records")
    let record =
        if records.ValueKind = JsonValueKind.Array && records.GetArrayLength() > 0 then records[0]
        else JsonElement()
    if record.ValueKind <> JsonValueKind.Object then None
    else
        let identifiers =
            identifierFields record [ ("doi", "doi"); ("pmid", "pmid"); ("pmcid", "pmcid"); ("mid", "mid") ]
        Some (MetadataCandidate("pmc-id-converter", jsonString record "title" |> Option.toObj,
            Identifiers = identifiers))

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains(identifier.Scheme)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "PMC ID Converter supports DOI, PMID, PMCID, and MID identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then
            invalidArg "endpoint" "PMC ID Converter endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then
            invalidArg "endpoint" "PMC ID Converter endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let url = endpoint + "/?format=json&ids=" + Uri.EscapeDataString(identifier.Value)
        let! bodyResult = fetchSource resources http "PMC ID Converter" "application/json" (Uri(url)) cancellationToken
        if bodyResult.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode bodyResult, failureMessage bodyResult)
        else
            try
                use document = JsonDocument.Parse(bodyResult.Value)
                match parseCandidate document.RootElement with
                | Some candidate ->
                    return Result<MetadataCandidate>.Success candidate
                | None ->
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                        "PMC ID Converter did not find a matching record.")
            with
            | :? OperationCanceledException as cancelled -> return raise cancelled
            | ex ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "PMC ID Converter response was invalid: " + ex.Message)
}

/// <summary>Pmc Id Converter metadata extension.</summary>
/// <patchouli-extension id="metadata.pmc-id-converter" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Pmc Id Converter</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
