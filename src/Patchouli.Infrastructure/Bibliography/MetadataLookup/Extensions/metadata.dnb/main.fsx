#load "Support.fsx"
open System
open System.Collections.Generic
open System.Text
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
    [<Field("Deutsche Nationalbibliothek endpoint", Key = "endpoint"); Default("https://services.dnb.de/sru/dnb")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("dnb", "Deutsche Nationalbibliothek", HashSet<string>([| "dnb"; "gnd"; "urn_nbn_de" |], StringComparer.OrdinalIgnoreCase), 5, true)
let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
    if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Deutsche Nationalbibliothek endpoint must be an absolute HTTP(S) URL."
    let baseUri = Uri(endpoint)
    if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Deutsche Nationalbibliothek endpoint must use HTTP or HTTPS."
    let http = resources.Http
    let index =
        match identifier.Scheme with
        | "gnd" -> "nid"
        | "urn_nbn_de" -> "urn"
        | _ -> "idn"
    let url = sprintf "%s?version=1.1&operation=searchRetrieve&recordSchema=MARC21-xml&query=%s%%3D%s" endpoint index (Uri.EscapeDataString identifier.Value)
    let! fetched = fetchSource resources http "Deutsche Nationalbibliothek" "application/xml" (Uri(url)) cancellationToken
    if fetched.IsFailure then
        return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
    else
        let document = XDocument.Parse(fetched.Value)
        match parseMarc document with
        | None ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "Deutsche Nationalbibliothek did not find a matching record.")
        | Some candidate ->
            if isEmptyCandidate candidate then
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "Deutsche Nationalbibliothek response did not contain metadata or identifiers.")
            else
                return Result<MetadataCandidate>.Success(withSource "dnb" candidate)
}

/// <summary>Dnb metadata extension.</summary>
/// <patchouli-extension id="metadata.dnb" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Dnb</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
