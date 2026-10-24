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

/// CiNii serves Dublin Core JSON-LD, not schema.org: titles live in dc:title as language-tagged
/// {"@value": ...} entries (the untagged entry is the original; tagged ones are readings),
/// creators in dc:creator/creator, and identifiers in productIdentifier / dc:identifier.
/// Verified against cir.nii.ac.jp CRID 1910302385685827200 and ci.nii.ac.jp NCID BN0368958X.
let langValues (element: JsonElement) (property: string) =
    let mutable value = JsonElement()
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) then
        let entries =
            match value.ValueKind with
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
            | _ -> [ value ]
        entries
        |> List.choose (fun entry ->
            match entry.ValueKind with
            | JsonValueKind.String -> clean (entry.GetString()) |> Option.map (fun text -> text, None)
            | JsonValueKind.Object ->
                match jsonString entry "@value" with
                | Some text -> Some (text, jsonString entry "@language")
                | None -> None
            | _ -> None)
    else []

let preferredText (values: (string * string option) list) =
    values
    |> List.tryFind (fun (_, language) -> language.IsNone)
    |> Option.orElse (List.tryHead values)
    |> Option.map fst

let dcText (element: JsonElement) (property: string) =
    match preferredText (langValues element property) with
    | Some value -> Some value
    | None -> jsonString element property

let dcPeople (element: JsonElement) (property: string) =
    let mutable value = JsonElement()
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(property, &value) then
        let entries =
            match value.ValueKind with
            | JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
            | _ -> [ value ]
        entries
        |> List.choose (fun entry ->
            match entry.ValueKind with
            | JsonValueKind.String -> clean (entry.GetString())
            | JsonValueKind.Object ->
                match preferredText (langValues entry "foaf:name") with
                | Some name -> Some name
                | None -> dcText entry "name"
            | _ -> None)
        |> List.map (fun name -> MetadataCreator(authorRole, Literal = name))
    else []

let parseJsonLd (root: JsonElement) (requested: NormalizedIdentifier) =
    // CRID documents are a single object; legacy NCID documents wrap the entity in @graph.
    let nodes =
        let mutable graph = JsonElement()
        if root.ValueKind = JsonValueKind.Array then root.EnumerateArray() |> Seq.toList
        elif root.ValueKind = JsonValueKind.Object && root.TryGetProperty("@graph", &graph) &&
             graph.ValueKind = JsonValueKind.Array then graph.EnumerateArray() |> Seq.toList
        else [ root ]
    let current =
        nodes
        |> List.tryFind (fun node -> (dcText node "dc:title").IsSome)
        |> Option.orElse (nodes |> List.tryFind (fun node -> node.ValueKind = JsonValueKind.Object))
        |> Option.defaultValue (JsonElement())
    let typedIdentifier (rawType: string) (value: string) =
        let kind = rawType.Substring(rawType.LastIndexOf(':') + 1).ToUpperInvariant()
        match kind with
        | "DOI" -> Some (MetadataIdentifier("doi", value))
        | "NAID" -> Some (MetadataIdentifier("naid", value))
        | "NCID" -> Some (MetadataIdentifier("ncid", value))
        | "NDL_BIB_ID" | "NDLBIBID" -> Some (MetadataIdentifier("ndlbibid", value))
        | _ -> None
    let identifiers =
        let found = ResizeArray<MetadataIdentifier>()
        let mutable products = JsonElement()
        if current.TryGetProperty("productIdentifier", &products) && products.ValueKind = JsonValueKind.Array then
            for entry in products.EnumerateArray() do
                let mutable identifier = JsonElement()
                if entry.TryGetProperty("identifier", &identifier) then
                    match jsonString identifier "@type", jsonString identifier "@value" with
                    | Some kind, Some value ->
                        match typedIdentifier kind value with
                        | Some result -> found.Add(result)
                        | None -> ()
                    | _ -> ()
        let mutable declared = JsonElement()
        if current.TryGetProperty("dc:identifier", &declared) then
            let entries =
                match declared.ValueKind with
                | JsonValueKind.Array -> declared.EnumerateArray() |> Seq.toList
                | _ -> [ declared ]
            for entry in entries do
                if entry.ValueKind = JsonValueKind.Object then
                    match jsonString entry "@type", jsonString entry "@value" with
                    | Some kind, Some value ->
                        match typedIdentifier kind value with
                        | Some result -> found.Add(result)
                        | None -> ()
                    | _ -> ()
        match jsonString current "cinii:ncid" with
        | Some value -> found.Add(MetadataIdentifier("ncid", value))
        | None -> ()
        found.Add(MetadataIdentifier(requested.Scheme, requested.Value))
        found
        |> Seq.distinctBy (fun identifier -> (identifier.Scheme + "\u0000" + identifier.Value).ToLowerInvariant())
        |> Seq.toArray
    let creators =
        Array.append (Array.append (dcPeople current "creator" |> List.toArray)
                          (dcPeople current "dc:creator" |> List.toArray))
            (dcPeople current "foaf:maker" |> List.toArray)
        |> Seq.distinctBy (fun creator -> match creator.Literal with null -> String.Empty | literal -> literal.ToUpperInvariant())
        |> Seq.toArray
    let dateText =
        [ "prism:publicationDate"; "dc:date"; "dcterms:issued"; "ndl:dateGranted" ]
        |> List.tryPick (fun property -> dcText current property)
    // CiNii Research "description" entries carrying "notation" are classification notes, not
    // abstracts; only plain-text descriptions are mapped.
    let abstractText =
        let mutable value = JsonElement()
        if current.TryGetProperty("description", &value) then
            match value.ValueKind with
            | JsonValueKind.String -> clean (value.GetString())
            | JsonValueKind.Array ->
                value.EnumerateArray()
                |> Seq.tryPick (fun entry ->
                    match entry.ValueKind with
                    | JsonValueKind.String -> clean (entry.GetString())
                    | JsonValueKind.Object ->
                        let mutable ignored = JsonElement()
                        if entry.TryGetProperty("notation", &ignored) then None
                        else jsonString entry "@value"
                    | _ -> None)
            | JsonValueKind.Object -> jsonString value "@value"
            | _ -> None
        else None
    MetadataCandidate("cinii",
        dcText current "dc:title" |> Option.toObj,
        Creators = creators,
        Dates = (match dateText with Some value -> literalDate value | None -> Array.empty),
        PublicationTitle = (dcText current "prism:publicationTitle" |> Option.toObj),
        Publisher = (dcText current "dc:publisher" |> Option.toObj),
        Language = (dcText current "dc:language" |> Option.toObj),
        Abstract = (abstractText |> Option.toObj),
        Tags = (langValues current "dc:subject" |> List.map fst |> List.toArray),
        Identifiers = identifiers)

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
                // No isEmptyCandidate gate here: parseJsonLd always attaches the requested
                // identifier, so "no title AND no identifiers" can never occur for this source.
                // A title-less candidate is returned as-is; MetadataLookupService discards it and
                // presents the missing title distinctly instead of a generic not-found.
                return Result<MetadataCandidate>.Success(parseJsonLd document.RootElement identifier)
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
