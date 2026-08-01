using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Patchouli.Cli;

/// <summary>
/// A structured tool response: the raw text the host returned (TOON by default, JSON when
/// the request used <c>format=json</c>), plus the stable exit/error code the process should
/// report. Clean successes carry <see cref="CliExitCode.Ok"/>; failures carry the matching
/// PRD error code read from the unified envelope.
/// </summary>
internal sealed record CliToolResponse(string Text, bool IsError, int ExitCode);

/// <summary>
/// Thin local MCP HTTP client. It speaks JSON-RPC over the host's <c>/mcp</c> endpoint and
/// never touches a SQLite database or a second domain implementation.
/// </summary>
internal sealed class McpHttpClient
{
    private const int RequestTimeoutSeconds = 130;
    private readonly HttpClient _http;
    private string? _sessionId;

    public McpHttpClient(string endpoint, string? token)
    {
        _http = new HttpClient
            { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        (JsonDocument document, string? sessionId) = await PostAsync(new
        {
            jsonrpc = "2.0",
            id = 0,
            method = "initialize",
            @params = new { protocolVersion = "2025-06-18" }
        }, cancellationToken);
        using (document)
        {
            if (document.RootElement.TryGetProperty("result", out _))
            {
                _sessionId = sessionId;
            }
        }
    }

    public async Task<CliToolResponse> CallToolAsync(
        string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        (JsonDocument document, string? sessionId) = await PostAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments }
        }, cancellationToken);
        using (document)
        {
            if (sessionId is not null)
            {
                _sessionId = sessionId;
            }

            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement rpcError))
            {
                int code = rpcError.TryGetProperty("code", out JsonElement errorCode)
                    ? errorCode.GetInt32()
                    : -32603;
                string message = rpcError.TryGetProperty("message", out JsonElement errorMessage)
                    ? errorMessage.GetString() ?? string.Empty
                    : string.Empty;
                return new CliToolResponse(message, true, MapRpcError(code));
            }

            if (!root.TryGetProperty("result", out JsonElement result))
            {
                return new CliToolResponse(string.Empty, true, CliExitCode.Internal);
            }

            bool isError = result.TryGetProperty("isError", out JsonElement isErrorElement) &&
                           isErrorElement.GetBoolean();
            string text = ExtractText(result);
            return new CliToolResponse(text, isError, ExtractExitCode(text, isError));
        }
    }

    private async Task<(JsonDocument Document, string? SessionId)> PostAsync(object request,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(request);
        using HttpRequestMessage message = new(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (_sessionId is not null)
        {
            message.Headers.Add("Mcp-Session-Id", _sessionId);
        }

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken);
        if ((int)response.StatusCode == StatusCodes.PayloadTooLarge)
        {
            throw new CliOverLimitException();
        }

        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new CliUnavailableException(
                $"the host returned HTTP {(int)response.StatusCode}: {Truncate(detail, 200)}");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new CliUnavailableException("the host returned an empty response.");
        }

        string? sessionId = null;
        if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values))
        {
            sessionId = values.FirstOrDefault();
        }

        return (JsonDocument.Parse(body), sessionId);
    }

    public static string ExtractText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out JsonElement text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    public static int ExtractExitCode(string text, bool isError)
    {
        if (!isError)
        {
            return CliExitCode.Ok;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("message", out JsonElement message) &&
                message.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("code", out JsonElement code))
            {
                return code.GetInt32();
            }
        }
        catch (JsonException)
        {
            // TOON (or a partial body) is not JSON; fall back to the scan below.
        }

        // The default encoding is TOON. The error object is the closed shape
        // { code, name, correlation_id } inside a "message:" block.
        const string marker = "code: ";
        int index = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (index >= 0 && int.TryParse(ReadDigits(text, index + marker.Length), out int toonCode))
        {
            return toonCode;
        }

        return CliExitCode.Internal;
    }

    private static string ReadDigits(string text, int start)
    {
        int end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return end > start ? text[start..end] : string.Empty;
    }

    private static int MapRpcError(int code)
    {
        return code is -32700 or -32600 or -32601 or -32602 ? CliExitCode.InvalidArgument : CliExitCode.Internal;
    }

    private static string Truncate(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum] + "…";
    }

    private static class StatusCodes
    {
        public const int PayloadTooLarge = 413;
    }
}

/// <summary>Raised when the host rejects the request as too large (HTTP 413) before invoking a tool.</summary>
internal sealed class CliOverLimitException : Exception
{
    public CliOverLimitException()
        : base("the request exceeds the host's max_mcp_request_bytes limit.")
    {
    }
}

/// <summary>Raised when the local host cannot be reached or returns a non-success HTTP status.</summary>
internal sealed class CliUnavailableException : Exception
{
    public CliUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>Exit codes shared with the host's PRD error table.</summary>
internal static class CliExitCode
{
    public const int Ok = 0;
    public const int Internal = 1;
    public const int InvalidArgument = 2;
    public const int NotFound = 3;
    public const int PermissionDenied = 4;
    public const int InvalidContent = 6;
    public const int ResponseTruncated = 7;
    public const int Unavailable = 8;
    public const int NotCitable = 9;
}
