using System.Net;

namespace Patchouli.Tests;

/// <summary>Test-only HTTP wire snapshots used by offline compiled-extension protocol tests.</summary>
internal sealed record RecordedHttpRequest(
    string Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

internal sealed record TestHttpResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public HttpResponseMessage ToMessage()
    {
        HttpResponseMessage response = new((HttpStatusCode)StatusCode)
        {
            Content = new StringContent(Body)
        };
        foreach ((string name, string value) in Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}

internal sealed class ProtocolTestHttpHandler(
    Func<RecordedHttpRequest, CancellationToken, Task<TestHttpResponse>> respond,
    Action<RecordedHttpRequest>? record = null) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in request.Headers)
        {
            headers[name] = name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                ? request.Headers.UserAgent.ToString()
                : string.Join(", ", values);
        }

        if (request.Content is not null)
        {
            foreach ((string name, IEnumerable<string> values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        RecordedHttpRequest snapshot = new(request.Method.Method, request.RequestUri!, headers, body);
        record?.Invoke(snapshot);
        TestHttpResponse response = await respond(snapshot, cancellationToken).ConfigureAwait(false);
        return response.ToMessage();
    }
}

internal static class ProtocolTestHttp
{
    public static TestHttpResponse Response(string body, int statusCode = 200,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new TestHttpResponse(statusCode, headers ?? new Dictionary<string, string>(), body);
    }
}
