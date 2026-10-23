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
    [<Field("arXiv API endpoint", Key = "endpoint"); Default("https://export.arxiv.org/api/query")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("arxiv", "arXiv", HashSet<string>([| "arxiv" |], StringComparer.OrdinalIgnoreCase), 10, true)
let withSourceId (sourceId: string) (candidate: MetadataCandidate) =
    MetadataCandidate(sourceId, candidate.Title, candidate.Subtitle, candidate.TitleShort, candidate.Creators,
        candidate.Dates, candidate.PublicationTitle, candidate.ContainerTitleShort, candidate.CollectionTitle,
        candidate.Publisher, candidate.Place, candidate.Edition, candidate.Genre, candidate.Number,
        candidate.ChapterNumber, candidate.Volume, candidate.Version, candidate.Issue, candidate.Pages,
        candidate.Language, candidate.Status, candidate.Note, candidate.Abstract, candidate.Tags,
        candidate.Identifiers, candidate.SuggestedItemType, candidate.TypeConfidence)

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (sourceDefinition.SupportedSchemes.Contains(identifier.Scheme)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
            "arXiv supports arXiv identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then
            invalidArg "endpoint" "arXiv endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then
            invalidArg "endpoint" "arXiv endpoint must use HTTP or HTTPS."
        let http = resources.Http
        let url = endpoint + "?id_list=" + Uri.EscapeDataString(identifier.Value)
        let! bodyResult = fetchSource resources http "arXiv" "application/atom+xml" (Uri(url)) cancellationToken
        if bodyResult.IsFailure then
            return Result<MetadataCandidate>.Failure(failureCode bodyResult, failureMessage bodyResult)
        else
            try
                let document = XDocument.Parse(bodyResult.Value)
                match parseArXiv document with
                | Some candidate ->
                    let candidate = withSource "arxiv" candidate
                    if isEmptyCandidate candidate then
                        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                            "The metadata source response did not contain metadata or identifiers.")
                    else
                        return Result<MetadataCandidate>.Success candidate
                | None ->
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                        "arXiv did not find a matching record.")
            with
            | :? OperationCanceledException as cancelled -> return raise cancelled
            | ex ->
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    "arXiv response was invalid: " + ex.Message)
}

/// <summary>Arxiv metadata extension.</summary>
/// <patchouli-extension id="metadata.arxiv" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Arxiv</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
