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
    [<Field("CiNii endpoint", Key = "endpoint"); Default("https://ci.nii.ac.jp")>] Endpoint: string
    [<Field("CiNii CRID endpoint", Key = "cir-endpoint"); Default("https://cir.nii.ac.jp/crid")>] CirEndpoint: string
}

let sourceDefinition = MetadataSourceDefinition("cinii", "CiNii Research", HashSet<string>([| "ncid"; "naid"; "crid" |], StringComparer.OrdinalIgnoreCase), 5, true)
let parseJsonLd (root: JsonElement) =
    let mutable current = root
    if current.ValueKind = JsonValueKind.Array then
        current <-
            current.EnumerateArray()
            |> Seq.tryFind (fun entry ->
                entry.ValueKind = JsonValueKind.Object &&
                ((jsonString entry "name").IsSome || (jsonString entry "headline").IsSome))
            |> Option.defaultValue (JsonElement())
    let mutable author = JsonElement()
    let authors =
        if current.TryGetProperty("author", &author) then jsonLdPeople author else Array.empty
    MetadataCandidate("cinii",
        (match jsonString current "name" with
         | Some value -> Some value
         | None -> jsonString current "headline") |> Option.toObj,
        Creators = authors,
        Dates = (match jsonString current "datePublished" with Some value -> literalDate value | None -> Array.empty),
        PublicationTitle = (jsonChildString current "isPartOf" "name" |> Option.toObj),
        Publisher = (jsonChildString current "publisher" "name" |> Option.toObj),
        Language = (jsonString current "inLanguage" |> Option.toObj),
        Abstract = (jsonString current "description" |> Option.toObj),
        Tags = jsonStrings current "keywords")

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains identifier.Scheme) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "CiNii Research supports NCID, NAID, and CRID identifiers.")
    else
        let http = resources.Http
        let url =
            if String.Equals(identifier.Scheme, "crid", StringComparison.OrdinalIgnoreCase) then
                let cirEndpoint = settings.CirEndpoint |> fun value -> value.Trim().TrimEnd('/')
                if not (Uri.IsWellFormedUriString(cirEndpoint, UriKind.Absolute)) then invalidArg "cir-endpoint" "CiNii CRID endpoint must be an absolute HTTP(S) URL."
                let cirUri = Uri(cirEndpoint)
                if cirUri.Scheme <> Uri.UriSchemeHttp && cirUri.Scheme <> Uri.UriSchemeHttps then invalidArg "cir-endpoint" "CiNii CRID endpoint must use HTTP or HTTPS."
                cirEndpoint + "/" + Uri.EscapeDataString(identifier.Value) + ".json"
            else
                let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
                if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "CiNii endpoint must be an absolute HTTP(S) URL."
                let baseUri = Uri(endpoint)
                if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "CiNii endpoint must use HTTP or HTTPS."
                let branch = if String.Equals(identifier.Scheme, "naid", StringComparison.OrdinalIgnoreCase) then "naid" else "ncid"
                endpoint + "/" + branch + "/" + Uri.EscapeDataString(identifier.Value) + ".json"
        let! fetched = fetchSource resources http "CiNii Research" "application/json" (Uri(url)) cancellationToken
        if fetched.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
        else
            try
                use document = JsonDocument.Parse(fetched.Value)
                return Result<MetadataCandidate>.Success(parseJsonLd document.RootElement)
            with
            | ex when (ex :? JsonException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "CiNii Research response was invalid: " + ex.Message)
}

/// <summary>Cinii metadata extension.</summary>
/// <patchouli-extension id="metadata.cinii" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Cinii</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
