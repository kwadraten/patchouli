#load "Support.fsx"
open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open BuiltInTranslation.Support

[<Settings>]
type Settings = {
    Common: CommonSettings
    [<Field("DeepLX translation URL", Key = "endpoint"); Default("http://127.0.0.1:1188/translate")>]
    Endpoint: string
    [<Field("DeepLX API credential", Key = "credential-reference", Kind = ConfigValueKind.CredentialReference); Default("")>]
    CredentialReference: string
}

let translateOne (common: CommonSettings) (job: ITranslationJob) (resources: IExtensionResources) (http: HttpClient) (endpoint: string) (credentialReference: string) (segment: Segment) cancellationToken = task {
    let text, tokens = protect segment.Id segment.Text
    let source = sourceLanguage common job
    let body = jsonBody {| text = text; source_lang = source; target_lang = targetLanguage common job |}
    let options = effectiveEngineOptions common job (Some 2) (Some 0.25M)
    let send (token: CancellationToken) = task {
        do! resources.WaitToProceedAsync(token)
        use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        if not (String.IsNullOrWhiteSpace credentialReference) then
            let! credential = resources.GetCredentialAsync(credentialReference, token)
            if not (String.IsNullOrWhiteSpace credential) then
                request.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential)
            else invalidOp "The configured credential is unavailable."
        use! response = http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
        ensureHttpResponseSuccess "DeepLX" response
        return! response.Content.ReadAsStringAsync(token)
    }
    let! response = retryTransient 0 (TimeSpan.FromMilliseconds(350.0)) (TimeSpan.FromSeconds(5.0)) send cancellationToken
    return translated segment.Id (restore (parseDeepLxResponse response) tokens)
}

/// <summary>Deeplx translation extension.</summary>
/// <patchouli-extension id="translation.deeplx" kind="translation" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Deeplx</name>
/// </patchouli-extension>
let extension =
     Extension.createTranslation<Settings> (fun settings (job: ITranslationJob) (resources: IExtensionResources) cancellationToken -> task {
        let common = settings.Common
        let endpoint = validateHttpEndpoint settings.Endpoint
        let http = resources.Http
        let credentialReference = settings.CredentialReference
        let options = effectiveEngineOptions common job (Some 2) (Some 0.25M)
        let work = segmentTranslator common job "deeplx-translation" options.MaxBatchCharacters
                        (translateOne common job resources http endpoint credentialReference)
        let budget = { MaxItems = 1; MaxCharacters = max 1 options.MaxBatchCharacters; MaxTokens = None }
        do! runSegmentPages job options budget work cancellationToken
    })
    |> Extension.withDerivedState clearDerivedState
