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
    [<Field("Deutsche Nationalbibliothek authorities endpoint", Key = "authorities-endpoint"); Default("https://services.dnb.de/sru/authorities")>] AuthoritiesEndpoint: string
}

// urn_nbn_de is intentionally not declared: the DNB SRU service has no URN index (its explain
// response lists none and an urn query returns an "Unsupported index" diagnostic), so the
// scheme must not be advertised as supported.
let sourceDefinition = MetadataSourceDefinition("dnb", "Deutsche Nationalbibliothek", HashSet<string>([| "dnb"; "gnd" |], StringComparer.OrdinalIgnoreCase), 5, true)

/// GND authority records have no bibliographic title field; the authorized heading (the 1XX $a:
/// 100/110/111/130 for names and works, 150 for subjects, 151 for places) is the record's name
/// and becomes the candidate title.
let parseGnd (document: XDocument) (requested: NormalizedIdentifier) =
    let record =
        document.Descendants()
        |> Seq.tryFind (fun item ->
            item.Name.LocalName = "record" &&
            item.Descendants() |> Seq.exists (fun child -> match child.Name.LocalName with "datafield" | "controlfield" -> true | _ -> false))
    match record with
    | None -> None
    | Some element ->
        let sub (tag: string) (code: string) =
            element.Descendants()
            |> Seq.filter (fun item ->
                item.Name.LocalName = "datafield" &&
                (match item.Attribute(XName.Get "tag") with null -> None | attribute -> Some attribute.Value) = Some tag)
            |> Seq.collect (fun field ->
                field.Elements()
                |> Seq.filter (fun child ->
                    child.Name.LocalName = "subfield" &&
                    (match child.Attribute(XName.Get "code") with null -> None | attribute -> Some attribute.Value) = Some code))
            |> Seq.choose (fun child -> clean (child.Value.Trim().TrimEnd('/', ':', ';')))
            |> Seq.tryHead
        let control (tag: string) =
            element.Descendants()
            |> Seq.filter (fun item ->
                item.Name.LocalName = "controlfield" &&
                (match item.Attribute(XName.Get "tag") with null -> None | attribute -> Some attribute.Value) = Some tag)
            |> Seq.choose (fun item -> clean item.Value)
            |> Seq.tryHead
        let name =
            sub "100" "a" |> Option.orElse (sub "110" "a") |> Option.orElse (sub "111" "a") |> Option.orElse (sub "130" "a")
            |> Option.orElse (sub "150" "a") |> Option.orElse (sub "151" "a")
        match name with
        | None -> None
        | Some heading ->
            let gndId = match control "001" with Some value -> value | None -> requested.Value
            Some (MetadataCandidate("dnb", heading,
                Identifiers = [| MetadataIdentifier("gnd", gndId) |]))

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains identifier.Scheme) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "Deutsche Nationalbibliothek supports DNB IDN and GND identifiers.")
    else
        let isGnd = String.Equals(identifier.Scheme, "gnd", StringComparison.OrdinalIgnoreCase)
        let endpoint =
            (if isGnd then settings.AuthoritiesEndpoint else settings.Endpoint)
            |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "Deutsche Nationalbibliothek endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "Deutsche Nationalbibliothek endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let index = if isGnd then "nid" else "idn"
        let url = sprintf "%s?version=1.1&operation=searchRetrieve&recordSchema=MARC21-xml&query=%s%%3D%s" endpoint index (Uri.EscapeDataString identifier.Value)
        let! fetched = fetchSource resources http "Deutsche Nationalbibliothek" "application/xml" (Uri(url)) cancellationToken
        if fetched.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode fetched, failureMessage fetched)
        else
            let document = XDocument.Parse(fetched.Value)
            match (if isGnd then parseGnd document identifier else parseMarc document) with
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
