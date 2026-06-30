using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LiteratureApp.Core.Results;
using LiteratureApp.Ocr.MinerU;

namespace LiteratureApp.Infrastructure.Ocr.MinerU;

public sealed class MinerUClient : IMinerUClient, IDisposable
{
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
            return Result<MinerUUploadBatch>.Failure(MinerUProviderStatus.NotConfigured, "MinerU token is not configured.");

        if (files.Count == 0)
            return Result<MinerUUploadBatch>.Failure("invalid_request", "At least one file is required.");

        try
        {
            var body = new MinerUBatchUrlRequest(
                files.Select(f => new MinerUBatchUrlFile(f.FileName, _options.IsOcr, f.FileName)).ToArray(),
                _options.ModelVersion,
                _options.Language,
                _options.EnableTable,
                _options.EnableFormula);

            var request = CreateRequest(HttpMethod.Post, "/api/v4/file-urls/batch", body);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    $"Failed to request upload URLs: {response.StatusCode}");

            var result = await response.Content.ReadFromJsonAsync<MinerUBatchUrlResponse>(cancellationToken: cancellationToken);

            if (result is null || result.Data is null || result.Code != 0)
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    SanitizeErrorMessage(result?.Message ?? "Invalid response from MinerU API."));

            var uploadUrls = MapUploadUrls(files, result.Data);
            if (uploadUrls.Count == 0)
                return Result<MinerUUploadBatch>.Failure(
                    MinerUProviderStatus.UploadUrlFailed,
                    "MinerU API did not return upload URLs.");

            var batch = new MinerUUploadBatch(result.Data.BatchId, uploadUrls);

            return Result<MinerUUploadBatch>.Success(batch);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            return Result<MinerUUploadBatch>.Failure(
                MinerUProviderStatus.UploadUrlFailed,
                SanitizeErrorMessage($"Network error: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            return Result<MinerUUploadBatch>.Failure(
                MinerUProviderStatus.UploadUrlFailed,
                SanitizeErrorMessage($"Response parsing error: {ex.Message}"));
        }
    }

    public async Task<Result> UploadFileAsync(string uploadUrl, string localPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
            return Result.Failure("file_not_found", "Local file was not found.");

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
            using var content = new ByteArrayContent(fileBytes);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(MinerUProviderStatus.UploadFailed,
                    SanitizeErrorMessage($"Upload failed: {response.StatusCode}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            return Result.Failure(MinerUProviderStatus.UploadFailed,
                SanitizeErrorMessage($"Upload network error: {ex.Message}"));
        }
        catch (IOException ex)
        {
            return Result.Failure(MinerUProviderStatus.UploadFailed,
                SanitizeErrorMessage($"File read error: {ex.Message}"));
        }
    }

    public async Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return Result<MinerUPollResult>.Failure(MinerUProviderStatus.NotConfigured, "MinerU token is not configured.");

        try
        {
            var request = CreateRequest(HttpMethod.Get, $"/api/v4/extract-results/batch/{batchId}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Result<MinerUPollResult>.Failure(
                    MinerUProviderStatus.Failed,
                    SanitizeErrorMessage($"Poll failed: {response.StatusCode}"));

            var result = await response.Content.ReadFromJsonAsync<MinerUPollResponse>(cancellationToken: cancellationToken);

            if (result is null || result.Data is null)
                return Result<MinerUPollResult>.Failure(
                    MinerUProviderStatus.Failed,
                    SanitizeErrorMessage("Invalid poll response from MinerU API."));

            var status = MapStatus(result.Data.Status);
            return Result<MinerUPollResult>.Success(new MinerUPollResult(
                result.Data.BatchId, status, result.Data.FullZipUrl, result.Data.ErrorMessage));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            return Result<MinerUPollResult>.Failure(MinerUProviderStatus.Failed,
                SanitizeErrorMessage($"Poll network error: {ex.Message}"));
        }
        catch (JsonException ex)
        {
            return Result<MinerUPollResult>.Failure(MinerUProviderStatus.Failed,
                SanitizeErrorMessage($"Poll response parsing error: {ex.Message}"));
        }
    }

    public async Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
        string batchId, string downloadDirectory, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.NotConfigured, "MinerU token is not configured.");

        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(_options.PollingTimeoutSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            var poll = await PollExtractResultAsync(batchId, cancellationToken);
            if (poll.IsFailure)
                return Result<MinerUDownloadedResult>.Failure(poll.ErrorCode!, poll.ErrorMessage!);

            var status = poll.Value.Status;

            if (status == MinerUProviderStatus.Done)
            {
                if (string.IsNullOrEmpty(poll.Value.FullZipUrl))
                    return Result<MinerUDownloadedResult>.Failure(
                        MinerUProviderStatus.Failed,
                        "Extraction completed but no result zip URL was provided.");

                return await DownloadZipAsync(batchId, poll.Value.FullZipUrl, downloadDirectory, cancellationToken);
            }

            if (status == MinerUProviderStatus.Failed)
                return Result<MinerUDownloadedResult>.Failure(
                    MinerUProviderStatus.Failed,
                    SanitizeErrorMessage(poll.Value.ErrorMessage ?? "MinerU extraction failed."));

            if (DateTimeOffset.UtcNow > timeoutAt)
                return Result<MinerUDownloadedResult>.Failure(
                    MinerUProviderStatus.Timeout,
                    "MinerU extraction timed out.");

            await Task.Delay(_options.PollingIntervalMs, cancellationToken);
        }

        return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.Timeout, "Polling was cancelled.");
    }

    private async Task<Result<MinerUDownloadedResult>> DownloadZipAsync(
        string batchId, string zipUrl, string downloadDirectory, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            var zipPath = Path.Combine(downloadDirectory, $"{batchId}.zip");

            using var request = new HttpRequestMessage(HttpMethod.Get, zipUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Result<MinerUDownloadedResult>.Failure(
                    MinerUProviderStatus.Failed,
                    SanitizeErrorMessage($"Download failed: {response.StatusCode}"));

            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destStream = File.Create(zipPath);
            await sourceStream.CopyToAsync(destStream, cancellationToken);

            return Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done));
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.Failed,
                SanitizeErrorMessage($"Download network error: {ex.Message}"));
        }
        catch (IOException ex)
        {
            return Result<MinerUDownloadedResult>.Failure(MinerUProviderStatus.Failed,
                SanitizeErrorMessage($"File write error: {ex.Message}"));
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{_options.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
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
                var file = requests[Math.Min(index, requests.Count - 1)];
                return new MinerUFileUploadUrl(file.FileName, url, file.FileName);
            }).ToArray();
        }

        if (data.Files is { Count: > 0 })
            return data.Files.Select(f => new MinerUFileUploadUrl(f.FileName, f.UploadUrl, f.FileId)).ToArray();

        return [];
    }

    private static string MapStatus(string mineruStatus) => mineruStatus switch
    {
        "waiting-file" => MinerUProviderStatus.WaitingFile,
        "pending" => MinerUProviderStatus.Pending,
        "running" => MinerUProviderStatus.Running,
        "converting" => MinerUProviderStatus.Converting,
        "done" => MinerUProviderStatus.Done,
        "failed" => MinerUProviderStatus.Failed,
        _ => MinerUProviderStatus.Failed
    };

    private static string SanitizeErrorMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "An unknown error occurred.";
        return "MinerU operation failed. Check configuration and try again.";
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
