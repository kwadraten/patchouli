using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Patchouli.Core.Results;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

public sealed class MinerUClient : IMinerUClient, IDisposable
{
    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BearerPattern =
        new(@"Bearer\s+[^\s,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WindowsPathPattern = new(@"[A-Za-z]:\\[^\s]+", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly MinerUOptions _options;
    private readonly bool _ownsClient;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

    public MinerUClient(HttpClient httpClient, MinerUOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _ownsClient = false;
    }

    public MinerUClient(MinerUOptions options)
    {
        _httpClient = new HttpClient();
        _options = options;
        _ownsClient = true;
    }

    public async Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
        IReadOnlyList<MinerUUploadRequest> files,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Result<MinerUUploadBatch>.Failure(MinerUProviderStatus.NotConfigured,
                "MinerU token is not configured.");
        }

        if (files.Count == 0)
        {
            return Result<MinerUUploadBatch>.Failure("invalid_request", "At least one file is required.");
        }

        try
        {
            MinerUBatchUrlRequest body = new(
                files.Select(f => new MinerUBatchUrlFile(f.FileName, _options.IsOcr, f.DataId)).ToArray(),
                _options.ModelVersion,
                _options.Language,
                _options.EnableTable,
                _options.EnableFormula);

            HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v4/file-urls/batch", body);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    await ReadHttpErrorMessageAsync(response, cancellationToken, _options.Token));
            }

            MinerUBatchUrlResponse? result =
                await response.Content.ReadFromJsonAsync<MinerUBatchUrlResponse>(cancellationToken);

            if (result is null)
            {
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    "Invalid response from MinerU API.");
            }

            if (!IsSuccessCode(result.Code))
            {
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    SanitizeErrorMessage(FormatProviderFailure(result.Code, result.ProviderMessage), _options.Token));
            }

            if (result.Data is null || string.IsNullOrWhiteSpace(result.Data.BatchId))
            {
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    "MinerU API did not return a batch ID.");
            }

            IReadOnlyList<MinerUFileUploadUrl> uploadUrls = MapUploadUrls(files, result.Data);
            if (uploadUrls.Count == 0)
            {
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    "MinerU API did not return upload URLs.");
            }

            MinerUUploadBatch batch = new(result.Data.BatchId, uploadUrls);

            return Result<MinerUUploadBatch>.Success(batch);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Result<MinerUUploadBatch>.Failure(
                MinerUProviderStatus.UploadUrlFailed,
                SanitizeErrorMessage($"Network error: {ex.Message}", _options.Token));
        }
        catch (JsonException ex)
        {
            return Result<MinerUUploadBatch>.Failure(
                MinerUProviderStatus.UploadUrlFailed,
                SanitizeErrorMessage($"Response parsing error: {ex.Message}", _options.Token));
        }
    }

    public async Task<Result> UploadFileAsync(string uploadUrl, string localPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
        {
            return Result.Failure("file_not_found", "Local file was not found.");
        }

        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
            using ByteArrayContent content = new(fileBytes);
            using HttpRequestMessage request = new(HttpMethod.Put, uploadUrl) { Content = content };

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(MinerUProviderStatus.UploadFailed,
                    SanitizeErrorMessage($"Upload failed: {response.StatusCode}", _options.Token));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure(MinerUProviderStatus.UploadFailed,
                SanitizeErrorMessage($"Upload network error: {ex.Message}", _options.Token));
        }
        catch (IOException ex)
        {
            return Result.Failure(MinerUProviderStatus.UploadFailed,
                SanitizeErrorMessage($"File read error: {ex.Message}", _options.Token));
        }
    }

    public async Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Result<MinerUPollResult>.Failure(MinerUProviderStatus.NotConfigured,
                "MinerU token is not configured.");
        }

        try
        {
            HttpRequestMessage request = CreateRequest(HttpMethod.Get, $"/api/v4/extract-results/batch/{batchId}");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result<MinerUPollResult>.Failure(
                    MinerUProviderStatus.Failed,
                    await ReadHttpErrorMessageAsync(response, cancellationToken, _options.Token));
            }

            MinerUPollResponse? result =
                await response.Content.ReadFromJsonAsync<MinerUPollResponse>(cancellationToken);

            if (result is null)
            {
                return Result<MinerUPollResult>.Failure(
                    MinerUProviderStatus.Failed,
                    "Invalid poll response from MinerU API.");
            }

            if (!IsSuccessCode(result.Code))
            {
                return Result<MinerUPollResult>.Failure(
                    MinerUProviderStatus.Failed,
                    SanitizeErrorMessage(FormatProviderFailure(result.Code, result.ProviderMessage), _options.Token));
            }

            if (result.Data is null)
            {
                return Result<MinerUPollResult>.Failure(
                    MinerUProviderStatus.Failed,
                    "MinerU API did not return extraction data.");
            }

            return Result<MinerUPollResult>.Success(MapPollResult(batchId, result.Data));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Result<MinerUPollResult>.Failure(MinerUProviderStatus.Failed,
                SanitizeErrorMessage($"Poll network error: {ex.Message}", _options.Token));
        }
        catch (JsonException ex)
        {
            return Result<MinerUPollResult>.Failure(MinerUProviderStatus.Failed,
                SanitizeErrorMessage($"Poll response parsing error: {ex.Message}", _options.Token));
        }
    }

    public async Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
        string batchId, string downloadDirectory, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.NotConfigured,
                "MinerU token is not configured.");
        }

        DateTimeOffset timeoutAt = DateTimeOffset.UtcNow.AddSeconds(_options.PollingTimeoutSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            Result<MinerUPollResult> poll = await PollExtractResultAsync(batchId, cancellationToken);
            if (poll.IsFailure)
            {
                return Result<MinerUDownloadedResult>.Failure(poll.ErrorCode!, poll.ErrorMessage!);
            }

            string status = poll.Value.Status;

            if (status == MinerUProviderStatus.Done)
            {
                if (string.IsNullOrEmpty(poll.Value.FullZipUrl))
                {
                    return Result<MinerUDownloadedResult>.Failure(
                        MinerUProviderStatus.Failed,
                        "Extraction completed but no result zip URL was provided.");
                }

                return await DownloadZipWithRetriesAsync(batchId, poll.Value.FullZipUrl, downloadDirectory,
                    cancellationToken);
            }

            if (status == MinerUProviderStatus.Failed)
            {
                return Result<MinerUDownloadedResult>.Failure(
                    MinerUProviderStatus.Failed,
                    SanitizeErrorMessage(poll.Value.ErrorMessage ?? "MinerU extraction failed.", _options.Token));
            }

            if (DateTimeOffset.UtcNow > timeoutAt)
            {
                return Result<MinerUDownloadedResult>.Failure(
                    MinerUProviderStatus.Timeout,
                    "MinerU extraction timed out.");
            }

            await Task.Delay(_options.PollingIntervalMs, cancellationToken);
        }

        return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.Timeout, "Polling was cancelled.");
    }

    private async Task<Result<MinerUDownloadedResult>> DownloadZipWithRetriesAsync(
        string batchId, string zipUrl, string downloadDirectory, CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, _options.DownloadMaxAttempts);
        Result<MinerUDownloadedResult> download =
            await DownloadZipAsync(batchId, zipUrl, downloadDirectory, cancellationToken);

        for (int attempt = 2; attempt <= maxAttempts && download.IsFailure &&
                                download.ErrorCode == MinerUProviderStatus.DownloadFailed; attempt++)
        {
            // Re-poll for a fresh URL before retrying: signed URLs can expire.
            Result<MinerUPollResult> repoll = await PollExtractResultAsync(batchId, cancellationToken);
            if (repoll.IsFailure || repoll.Value.Status != MinerUProviderStatus.Done ||
                string.IsNullOrEmpty(repoll.Value.FullZipUrl))
            {
                return download;
            }

            await Task.Delay(_options.DownloadRetryDelayMs * (attempt - 1), cancellationToken);

            download = await DownloadZipAsync(batchId, repoll.Value.FullZipUrl, downloadDirectory, cancellationToken);
        }

        return download;
    }

    private async Task<Result<MinerUDownloadedResult>> DownloadZipAsync(
        string batchId, string zipUrl, string downloadDirectory, CancellationToken cancellationToken)
    {
        string? zipPath = null;
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            zipPath = Path.Combine(downloadDirectory, $"{batchId}.zip");

            using CancellationTokenSource downloadTimeout = new();
            downloadTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.DownloadTimeoutSeconds)));
            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, downloadTimeout.Token);

            using HttpRequestMessage request = new(HttpMethod.Get, zipUrl);
            using HttpResponseMessage response =
                await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                return Result<MinerUDownloadedResult>.Failure(
                    MinerUProviderStatus.DownloadFailed,
                    SanitizeErrorMessage($"Download failed: {response.StatusCode}", _options.Token));
            }

            await using Stream sourceStream = await response.Content.ReadAsStreamAsync(linkedCancellation.Token);
            await using FileStream destStream = File.Create(zipPath);
            await sourceStream.CopyToAsync(destStream, linkedCancellation.Token);

            return Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DeletePartialDownload(zipPath);
            return Result<MinerUDownloadedResult>.Failure(
                MinerUProviderStatus.DownloadFailed,
                "MinerU result download timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            DeletePartialDownload(zipPath);
            return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.DownloadFailed,
                SanitizeErrorMessage($"Download network error: {ex.Message}", _options.Token));
        }
        catch (IOException ex)
        {
            DeletePartialDownload(zipPath);
            return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.DownloadFailed,
                SanitizeErrorMessage($"File write error: {ex.Message}", _options.Token));
        }
    }

    private static void DeletePartialDownload(string? zipPath)
    {
        if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body = null)
    {
        HttpRequestMessage request = new(method, $"{_options.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (body is not null)
        {
            string json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private static IReadOnlyList<MinerUFileUploadUrl> MapUploadUrls(
        IReadOnlyList<MinerUUploadRequest> requests,
        MinerUBatchUrlData data)
    {
        if (data.FileUrls is { Count: > 0 })
        {
            return data.FileUrls.Select((url, index) =>
            {
                MinerUUploadRequest file = requests[Math.Min(index, requests.Count - 1)];
                return new MinerUFileUploadUrl(file.FileName, url, file.DataId);
            }).ToArray();
        }

        if (data.Files is { Count: > 0 })
        {
            return data.Files.Select(f => new MinerUFileUploadUrl(f.FileName, f.UploadUrl, f.FileId)).ToArray();
        }

        return [];
    }

    private static MinerUPollResult MapPollResult(string requestedBatchId, MinerUPollData data)
    {
        string? batchId = string.IsNullOrWhiteSpace(data.BatchId) ? requestedBatchId : data.BatchId;
        MinerUPollExtractResultItem? item = data.ExtractResult?
                                                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.State))
                                            ?? data.ExtractResult?.FirstOrDefault();

        if (item is not null)
        {
            return new MinerUPollResult(
                batchId,
                MapStatus(item.State),
                item.FullZipUrl,
                string.IsNullOrWhiteSpace(item.ErrorMessage) ? null : item.ErrorMessage);
        }

        return new MinerUPollResult(
            batchId,
            MapStatus(data.State ?? data.Status),
            data.FullZipUrl,
            string.IsNullOrWhiteSpace(data.ErrorMessage) ? null : data.ErrorMessage);
    }

    private static string MapStatus(string? mineruStatus)
    {
        return mineruStatus switch
        {
            "waiting-file" => MinerUProviderStatus.WaitingFile,
            "pending" => MinerUProviderStatus.Pending,
            "running" => MinerUProviderStatus.Running,
            "converting" => MinerUProviderStatus.Converting,
            "done" => MinerUProviderStatus.Done,
            "failed" => MinerUProviderStatus.Failed,
            _ => MinerUProviderStatus.Running
        };
    }

    private static bool IsSuccessCode(JsonElement code)
    {
        if (code.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return false;
        }

        return code.ValueKind switch
        {
            JsonValueKind.Number => code.TryGetInt32(out int number) && number == 0,
            JsonValueKind.String => string.Equals(code.GetString(), "0", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string FormatProviderFailure(JsonElement code, string? message)
    {
        string? codeText = FormatProviderCode(code);
        if (string.IsNullOrWhiteSpace(message))
        {
            return codeText is null ? "MinerU API returned an error." : $"MinerU API error {codeText}.";
        }

        return codeText is null
            ? $"MinerU API error: {message}"
            : $"MinerU API error {codeText}: {message}";
    }

    private static string? FormatProviderCode(JsonElement code)
    {
        return code.ValueKind switch
        {
            JsonValueKind.Number => code.GetRawText(),
            JsonValueKind.String => code.GetString(),
            _ => null
        };
    }

    private static async Task<string> ReadHttpErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        string? token)
    {
        string? providerMessage = await TryReadProviderMessageAsync(response, cancellationToken);
        string message = string.IsNullOrWhiteSpace(providerMessage)
            ? $"MinerU API request failed: {response.StatusCode}."
            : $"MinerU API request failed: {response.StatusCode}. {providerMessage}";

        return SanitizeErrorMessage(message, token);
    }

    private static async Task<string?> TryReadProviderMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? code = root.TryGetProperty("code", out JsonElement codeElement)
                ? FormatProviderCode(codeElement)
                : null;
            string? message = root.TryGetProperty("msg", out JsonElement msgElement)
                ? msgElement.GetString()
                : root.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(code))
            {
                return message;
            }

            return string.IsNullOrWhiteSpace(message)
                ? $"MinerU API error {code}."
                : $"MinerU API error {code}: {message}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SanitizeErrorMessage(string message, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "An unknown error occurred.";
        }

        string sanitized = message;
        if (!string.IsNullOrWhiteSpace(token))
        {
            sanitized = sanitized.Replace(token, "[redacted]", StringComparison.Ordinal);
        }

        sanitized = BearerPattern.Replace(sanitized, "Bearer [redacted]");
        sanitized = UrlPattern.Replace(sanitized, "[redacted-url]");
        sanitized = WindowsPathPattern.Replace(sanitized, "[redacted-path]");
        sanitized = WhitespacePattern.Replace(sanitized, " ").Trim();

        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
