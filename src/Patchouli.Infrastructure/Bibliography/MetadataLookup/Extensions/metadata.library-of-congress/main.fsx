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
    [<Field("Library of Congress endpoint", Key = "endpoint"); Default("https://www.loc.gov/item")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("library-of-congress", "Library of Congress", HashSet<string>([| "lccn" |], StringComparer.OrdinalIgnoreCase), 5, true)
let parseLoc (root: JsonElement) (lccn: string) =
    let title =
        match jsonFirstString root "title" with
        | Some value -> Some value
        | None -> jsonChildString root "item" "title"
    let mutable item = JsonElement()
    let nestedContributors =
        if root.ValueKind = JsonValueKind.Object && root.TryGetProperty("item", &item) then
            jsonStrings item "contributors"
        else Array.empty
    let contributors =
        Array.append (jsonStrings root "contributor") nestedContributors
        |> Seq.distinctBy (fun name -> name.ToUpperInvariant())
        |> Seq.map (fun name -> MetadataCreator(authorRole, Literal = name))
        |> Seq.toArray
    let dateText =
        match jsonFirstString root "date" with
        | Some value -> Some value
        | None -> jsonChildString root "item" "date"
    MetadataCandidate("library-of-congress", title |> Option.toObj,
        Creators = contributors,
        Dates = (match dateText with Some value -> literalDate value | None -> Array.empty),
        Publisher = (jsonFirstString root "publisher" |> Option.toObj),
        Place = (jsonFirstString root "location" |> Option.toObj),
        Language = (jsonFirstString root "language" |> Option.toObj),
        Tags = jsonStrings root "subject",
        Identifiers = [| MetadataIdentifier("lccn", lccn) |])

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains identifier.Scheme) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "Library of Congress supports LCCN identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Library of Congress endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Library of Congress endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let url = endpoint + "/" + Uri.EscapeDataString(identifier.Value) + "/?fo=json"
        let! fetched = fetchSource resources http "Library of Congress" "application/json" (Uri(url)) cancellationToken
        if fetched.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
        else
            try
                use document = JsonDocument.Parse(fetched.Value)
                return Result<MetadataCandidate>.Success(parseLoc document.RootElement identifier.Value)
            with
            | ex when (ex :? JsonException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "Library of Congress response was invalid: " + ex.Message)
}

/// <summary>Library Of Congress metadata extension.</summary>
/// <patchouli-extension id="metadata.library-of-congress" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Library Of Congress</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
