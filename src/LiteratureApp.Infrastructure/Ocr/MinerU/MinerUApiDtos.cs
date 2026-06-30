using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteratureApp.Infrastructure.Ocr.MinerU;

internal sealed record MinerUBatchUrlRequest(
    [property: JsonPropertyName("files")] IReadOnlyList<MinerUBatchUrlFile> Files,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("enable_table")] bool EnableTable,
    [property: JsonPropertyName("enable_formula")] bool EnableFormula);

internal sealed record MinerUBatchUrlFile(
    [property: JsonPropertyName("name")] string FileName,
    [property: JsonPropertyName("is_ocr")] bool IsOcr,
    [property: JsonPropertyName("data_id")] string DataId);

internal sealed record MinerUBatchUrlResponse(
    [property: JsonPropertyName("data")] MinerUBatchUrlData? Data,
    [property: JsonPropertyName("code")] JsonElement Code,
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("message")] string? Message)
{
    public string? ProviderMessage => Msg ?? Message;
}

internal sealed record MinerUBatchUrlData(
    [property: JsonPropertyName("batch_id")] string? BatchId,
    [property: JsonPropertyName("file_urls")] IReadOnlyList<string>? FileUrls,
    [property: JsonPropertyName("files")] IReadOnlyList<MinerUFileUrlItem>? Files);

internal sealed record MinerUFileUrlItem(
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("upload_url")] string UploadUrl,
    [property: JsonPropertyName("file_id")] string FileId);

internal sealed record MinerUPollResponse(
    [property: JsonPropertyName("data")] MinerUPollData? Data,
    [property: JsonPropertyName("code")] JsonElement Code,
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("message")] string? Message)
{
    public string? ProviderMessage => Msg ?? Message;
}

internal sealed record MinerUPollData(
    [property: JsonPropertyName("batch_id")] string? BatchId,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("full_zip_url")] string? FullZipUrl,
    [property: JsonPropertyName("err_msg")] string? ErrorMessage,
    [property: JsonPropertyName("extract_result")] IReadOnlyList<MinerUPollExtractResultItem>? ExtractResult);

internal sealed record MinerUPollExtractResultItem(
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("full_zip_url")] string? FullZipUrl,
    [property: JsonPropertyName("err_msg")] string? ErrorMessage,
    [property: JsonPropertyName("data_id")] string? DataId);
