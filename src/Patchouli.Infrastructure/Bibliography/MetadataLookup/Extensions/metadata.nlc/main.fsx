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
    [<Field("National Library of China endpoint", Key = "endpoint"); Default("http://opac.nlc.cn/F")>] Endpoint: string
}

let sourceDefinition = MetadataSourceDefinition("nlc", "National Library of China", HashSet<string>([| "isbn" |], StringComparer.OrdinalIgnoreCase), 2, true)
let fetchHtml (resources: IExtensionResources) (uri: Uri) (cancellationToken: CancellationToken) = task {
    do! resources.WaitToProceedAsync(cancellationToken)
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
    use linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token)
    use request = new HttpRequestMessage(HttpMethod.Get, uri)
    request.Headers.Accept.ParseAdd("text/html")
    request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9")
    use! response = resources.Http.SendAsync(request, linked.Token)
    let statusCode = int response.StatusCode
    if statusCode = 429 then
        return Result<string>.Failure(MetadataLookupErrorCodes.RateLimited, "National Library of China rate limit was reached.")
    elif not response.IsSuccessStatusCode then
        return Result<string>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
            sprintf "National Library of China returned HTTP %d." statusCode)
    else
        let! body = response.Content.ReadAsStringAsync(linked.Token)
        return Result<string>.Success(body)
}

let lookup (identifier: NormalizedIdentifier) (settings: Settings)
           (resources: IExtensionResources) (cancellationToken: CancellationToken) = task {
    if not (String.Equals(identifier.Scheme, "isbn", StringComparison.OrdinalIgnoreCase)) then
        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier, "National Library of China supports ISBN identifiers.")
    else
        let endpoint = settings.Endpoint
        if not (Uri.IsWellFormedUriString(endpoint, UriKind.Absolute)) then invalidArg "endpoint" "National Library of China endpoint must be an absolute HTTP(S) URL."
        let baseUri = Uri(endpoint)
        if baseUri.Scheme <> Uri.UriSchemeHttp && baseUri.Scheme <> Uri.UriSchemeHttps then invalidArg "endpoint" "National Library of China endpoint must use HTTP or HTTPS."
        try
            let searchUrl = sprintf "%s?func=find-b&find_code=ISB&request=%s&local_base=NLC01" endpoint (Uri.EscapeDataString identifier.Value)
            let! first = fetchHtml resources (Uri(searchUrl)) cancellationToken
            if first.IsFailure then
                return Result<MetadataCandidate>.Failure(failureCode first, failureMessage first)
            else
                let mutable html = first.Value
                if not (nlcHasMetadataTable html) then
                    match nlcFirstDetailUrl html with
                    | None ->
                        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "National Library of China did not find the ISBN.")
                    | Some detail ->
                        let! second = fetchHtml resources (Uri(detail)) cancellationToken
                        if second.IsFailure then
                            return Result<MetadataCandidate>.Failure(failureCode second, failureMessage second)
                        else
                            html <- second.Value
                            match parseNlcHtml html identifier with
                            | None ->
                                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                                    "National Library of China did not return usable metadata for the ISBN.")
                            | Some candidate ->
                                return Result<MetadataCandidate>.Success(withSource "nlc" candidate)
                else
                    match parseNlcHtml html identifier with
                    | None ->
                        return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound,
                            "National Library of China did not return usable metadata for the ISBN.")
                    | Some candidate ->
                        return Result<MetadataCandidate>.Success(withSource "nlc" candidate)
        with
        | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.Timeout, "National Library of China request timed out.")
        | :? OperationCanceledException as ex ->
            return raise ex
        | :? HttpRequestException as ex when (ex.StatusCode.HasValue && int ex.StatusCode.Value = 429) ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.RateLimited, "National Library of China rate limit was reached.")
        | :? HttpRequestException as ex ->
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                sprintf "National Library of China request failed: %s" ex.Message)
}

/// <summary>Nlc metadata extension.</summary>
/// <patchouli-extension id="metadata.nlc" kind="metadata" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Nlc</name>
/// </patchouli-extension>
let extension =
    Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token ->
        lookup identifier settings resources token)
