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
    // The loc.gov JSON API keeps every bibliographic field inside the "item" object; the top
    // level only carries search/context data. Older flat shapes are still accepted as fallback.
    let mutable item = JsonElement()
    let hasItem = root.ValueKind = JsonValueKind.Object && root.TryGetProperty("item", &item) &&
                  item.ValueKind = JsonValueKind.Object
    let itemOrRoot (property: string) =
        match jsonFirstString root property with
        | Some value -> Some value
        | None -> if hasItem then jsonFirstString item property else None
    let title =
        match jsonFirstString root "title" with
        | Some value -> Some value
        | None -> jsonChildString root "item" "title"
    let contributors =
        // item.contributors is an array of single-key {name: url} objects; the plain name list
        // lives in item.contributor_names.
        let contributorNames =
            if hasItem then
                let fromNames = jsonStrings item "contributor_names"
                let mutable raw = JsonElement()
                let fromObjects =
                    if item.TryGetProperty("contributors", &raw) && raw.ValueKind = JsonValueKind.Array then
                        raw.EnumerateArray()
                        |> Seq.collect (fun entry ->
                            if entry.ValueKind = JsonValueKind.Object then
                                entry.EnumerateObject() |> Seq.map (fun property -> property.Name)
                            else Seq.empty)
                        |> Seq.toArray
                    else Array.empty
                Array.append fromNames fromObjects
            else Array.empty
        Array.append (jsonStrings root "contributor") contributorNames
        |> Seq.distinctBy (fun name -> name.ToUpperInvariant())
        |> Seq.map (fun name -> MetadataCreator(authorRole, Literal = name))
        |> Seq.toArray
    let dateText =
        match jsonFirstString root "date" with
        | Some value -> Some value
        | None -> jsonChildString root "item" "date"
    // The API has no structured publisher/place fields; they only appear in the
    // created_published free text, shaped "Place : Publisher, date." — parse that one pattern
    // and leave the fields empty when it does not match.
    let publishedPlace, publishedPublisher =
        let text = if hasItem then jsonFirstString item "created_published" else None
        match text with
        | None -> None, None
        | Some value ->
            let trimmed = value.Trim().TrimEnd('.')
            match trimmed.IndexOf(" : ", StringComparison.Ordinal) with
            | -1 -> None, None
            | separator ->
                let place = trimmed.Substring(0, separator).Trim()
                let segments =
                    trimmed.Substring(separator + 3).Split(',')
                    |> Array.map (fun segment -> segment.Trim())
                // A trailing comma segment that is just the date (bracketed, or containing a
                // four-digit year) is dropped; the publisher is everything before it, rejoined
                // ("Harper, an imprint of HarperCollins Publishers, [2017]" keeps the imprint).
                // Without such a date tail, keep the historical first-segment behaviour.
                let isDateTail (segment: string) =
                    (segment.StartsWith("[", StringComparison.Ordinal) &&
                     segment.EndsWith("]", StringComparison.Ordinal)) ||
                    System.Text.RegularExpressions.Regex.IsMatch(segment, "\\d{4}")
                let publisher =
                    if segments.Length > 1 && isDateTail segments[segments.Length - 1] then
                        String.Join(", ", segments[0 .. segments.Length - 2])
                    else segments[0]
                (clean place, clean publisher)
    let publisher =
        match jsonFirstString root "publisher" with
        | Some value -> Some value
        | None -> publishedPublisher
    let place =
        match jsonFirstString root "location" with
        | Some value -> Some value
        | None -> publishedPlace
    let tags =
        match jsonStrings root "subject" with
        | [||] -> if hasItem then jsonStrings item "subject" else Array.empty
        | values -> values
    MetadataCandidate("library-of-congress", title |> Option.toObj,
        Creators = contributors,
        Dates = (match dateText with Some value -> literalDate value | None -> Array.empty),
        Publisher = (publisher |> Option.toObj),
        Place = (place |> Option.toObj),
        Language = (itemOrRoot "language" |> Option.toObj),
        Tags = tags,
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
