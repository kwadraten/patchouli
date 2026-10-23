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
    [<Field("Bibliotheque nationale de France endpoint", Key = "endpoint"); Default("https://catalogue.bnf.fr/api/SRU")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("bnf", "Bibliotheque nationale de France", HashSet<string>([| "ark"; "frbnf" |], StringComparer.OrdinalIgnoreCase), 5, true)
let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
    if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Bibliotheque nationale de France endpoint must be an absolute HTTP(S) URL."
    let baseUri = Uri(endpoint)
    if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Bibliotheque nationale de France endpoint must use HTTP or HTTPS."
    let http = resources.Http
    let query = if String.Equals(identifier.Scheme, "frbnf", StringComparison.OrdinalIgnoreCase) then "bib.fRBNF" else "bib.ark"
    let url = sprintf "%s?version=1.2&operation=searchRetrieve&recordSchema=intermarcXchange&query=%s%%20all%%20%%22%s%%22" endpoint query (Uri.EscapeDataString identifier.Value)
    let! fetched = fetchSource resources http "Bibliotheque nationale de France" "application/xml" (Uri(url)) cancellationToken
    if fetched.IsFailure then
        return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
    else
        let document = XDocument.Parse(fetched.Value)
        match parseMarc document with
        | None ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "Bibliotheque nationale de France did not find a matching record.")
        | Some candidate ->
            if isEmptyCandidate candidate then
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "Bibliotheque nationale de France response did not contain metadata or identifiers.")
            else
                return Result<MetadataCandidate>.Success(withSource "bnf" candidate)
}

/// <summary>Bnf metadata extension.</summary>
/// <patchouli-extension id="metadata.bnf" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Bnf</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
