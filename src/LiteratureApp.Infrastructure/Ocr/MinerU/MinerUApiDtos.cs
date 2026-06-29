using System.Text.Json.Serialization;

namespace LiteratureApp.Infrastructure.Ocr.MinerU;

internal sealed record MinerUBatchUrlRequest(
    [property: JsonPropertyName("files")] IReadOnlyList<MinerUBatchUrlFile> Files);

internal sealed record MinerUBatchUrlFile(
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("file_size")] long FileSize);

internal sealed record MinerUBatchUrlResponse(
    [property: JsonPropertyName("data")] MinerUBatchUrlData? Data,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record MinerUBatchUrlData(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("files")] IReadOnlyList<MinerUFileUrlItem> Files);

internal sealed record MinerUFileUrlItem(
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("upload_url")] string UploadUrl,
    [property: JsonPropertyName("file_id")] string FileId);

internal sealed record MinerUPollResponse(
    [property: JsonPropertyName("data")] MinerUPollData? Data,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record MinerUPollData(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("full_zip_url")] string? FullZipUrl,
    [property: JsonPropertyName("err_msg")] string? ErrorMessage);
