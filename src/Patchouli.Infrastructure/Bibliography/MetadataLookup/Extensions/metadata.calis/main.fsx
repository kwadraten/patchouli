#load "Support.fsx"
open System
open System.Collections.Generic
open System.Net
open System.Net.Http
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
    [<Field("CALIS Union Catalog endpoint", Key = "endpoint"); Default("https://opac2.calis.edu.cn/prod-api/opac/codex/ekb/opac/bibliography")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("calis", "CALIS Union Catalog", HashSet<string>([| "isbn" |], StringComparer.OrdinalIgnoreCase), 1, true)
let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (String.Equals(identifier.Scheme, "isbn", StringComparison.OrdinalIgnoreCase)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier, "CALIS Union Catalog supports ISBN identifiers.")
    else
        let endpoint = settings.Endpoint |> fun value -> value.Trim().TrimEnd('/')
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "CALIS Union Catalog endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "CALIS Union Catalog endpoint must use HTTP or HTTPS."
        let http = resources.Http
        try
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20.0))
            use linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token)
            let cql = sprintf "(bath.isbn=\"%s*\")" identifier.Value
            let query = Convert.ToBase64String(Encoding.UTF8.GetBytes(cql))
            let body = sprintf "{\"offset\":1,\"limit\":10,\"query\":\"%s\",\"sortkey\":\"relatesort\",\"lang\":\"zh-cn\"}" query
            do! resources.WaitToProceedAsync(cancellationToken)
            let searchUrl = endpoint + "/simple/instances/1.0"
            use request = new HttpRequestMessage(HttpMethod.Post, Uri(searchUrl))
            request.Headers.Accept.ParseAdd("application/json")
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            use! response = http.SendAsync(request, linked.Token)
            let statusCode = int response.StatusCode
            if statusCode = 429 then
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.RateLimited, "CALIS Union Catalog rate limit was reached.")
            elif not response.IsSuccessStatusCode then
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                    sprintf "CALIS Union Catalog returned HTTP %d." statusCode)
            else
                let! responseBody = response.Content.ReadAsStringAsync(linked.Token)
                use document = JsonDocument.Parse(responseBody)
                let instances = document.RootElement.GetProperty("data").GetProperty("instances")
                let rids = ResizeArray<string>()
                if instances.ValueKind = JsonValueKind.Array then
                    for instance in instances.EnumerateArray() do
                        match jsonString instance "rid" with
                        | Some rid -> rids.Add(rid)
                        | None -> ()
                let mutable found: Result<MetadataCandidate> option = None
                let mutable rateLimited = false
                let mutable index = 0
                while found.IsNone && not rateLimited && index < rids.Count do
                    let rid = rids[index]
                    index <- index + 1
                    do! resources.WaitToProceedAsync(cancellationToken)
                    let detailUrl = endpoint + "/marc/" + Uri.EscapeDataString(rid)
                    use detailRequest = new HttpRequestMessage(HttpMethod.Get, Uri(detailUrl))
                    detailRequest.Headers.Accept.ParseAdd("application/json")
                    use! detail = http.SendAsync(detailRequest, linked.Token)
                    let detailStatusCode = int detail.StatusCode
                    if detailStatusCode = 429 then
                        rateLimited <- true
                    elif detail.IsSuccessStatusCode then
                        let! detailBody = detail.Content.ReadAsStringAsync(linked.Token)
                        use envelope = JsonDocument.Parse(detailBody)
                        let marcJson = envelope.RootElement.GetProperty("data").GetProperty("MARC").GetString()
                        match clean marcJson with
                        | Some marcText ->
                            use marc = JsonDocument.Parse(marcText)
                            match parseCalisMarc marc.RootElement identifier rid with
                            | Some candidate -> found <- Some (Result<MetadataCandidate>.Success(withSource "calis" candidate))
                            | None -> ()
                        | None -> ()
                if rateLimited then
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.RateLimited, "CALIS Union Catalog rate limit was reached.")
                elif found.IsSome then
                    return found.Value
                else
                    return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "CALIS Union Catalog did not find an exact ISBN record.")
        with
        | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.Timeout, "CALIS Union Catalog request timed out.")
        | :? OperationCanceledException as ex ->
            return raise ex
        | :? HttpRequestException as ex when (ex.StatusCode.HasValue && int ex.StatusCode.Value = 429) ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.RateLimited, "CALIS Union Catalog rate limit was reached.")
        | ex when (ex :? JsonException || ex :? InvalidOperationException || ex :? KeyNotFoundException || ex :? FormatException) ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                sprintf "CALIS Union Catalog response was invalid: %s" ex.Message)
        | :? HttpRequestException as ex ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                sprintf "CALIS Union Catalog request failed: %s" ex.Message)
}

/// <summary>Calis metadata extension.</summary>
/// <patchouli-extension id="metadata.calis" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Calis</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
