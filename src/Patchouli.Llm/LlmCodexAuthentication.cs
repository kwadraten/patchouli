using System.Buffers.Text;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSharp.ModelsDev;

namespace Patchouli.Llm;

// Property names match the credentials already serialized by Patchouli.
public sealed class LlmCodexCredentials
{
    public string IdToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string? AccountId { get; set; }
    public string? Email { get; set; }
    public string? PlanType { get; set; }
    public bool IsFedRamp { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset LastRefreshUtc { get; set; }
}

/// <summary>Host-owned Codex sign-in, token rotation and account discovery.</summary>
internal sealed class LlmCodexAuthentication(LlmCodexCredentialStore store, HttpClient? client = null)
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string Issuer = "https://auth.openai.com";
    private const string Originator = "patchouli";
    private const string ProtocolVersion = "0.160.0";
    internal const string BaseUrl = "https://chatgpt.com/backend-api/codex";

    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private HttpClient Http => client ?? SharedClient;

    internal async Task LoginAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using TcpListener listener = StartLoopbackListener();
        Uri origin = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        string callback = new Uri(origin, "/auth/callback").AbsoluteUri;
        string verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        string state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        using FormUrlEncodedContent query = new(new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = callback,
            ["scope"] = "openid profile email offline_access api.connectors.read api.connectors.invoke",
            ["code_challenge"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = Originator
        });
        openBrowser(new Uri(
            $"{Issuer}/oauth/authorize?{await query.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)}"));
        while (true)
        {
            using TcpClient browser = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            using NetworkStream stream = browser.GetStream();
            Uri? request = await ReadCallbackAsync(stream, origin, deadline.Token).ConfigureAwait(false);
            NameValueCollection parameters = System.Web.HttpUtility.ParseQueryString(request?.Query ?? "");
            string? receivedState = parameters["state"];
            if (request?.AbsolutePath != "/auth/callback" || receivedState is null ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state),
                    Encoding.UTF8.GetBytes(receivedState)))
            {
                await RespondAsync(stream, false, deadline.Token).ConfigureAwait(false);
                continue;
            }

            string? code = parameters["code"];
            if (string.IsNullOrEmpty(code))
            {
                await RespondAsync(stream, false, deadline.Token).ConfigureAwait(false);
                throw new InvalidOperationException("Subscription login did not complete. Try signing in again.");
            }

            using FormUrlEncodedContent content = new(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["redirect_uri"] = callback,
                ["code_verifier"] = verifier
            });
            await ExchangeAsync(content, null, deadline.Token).ConfigureAwait(false);
            await RespondAsync(stream, true, deadline.Token).ConfigureAwait(false);
            return;
        }
    }

    private static TcpListener StartLoopbackListener()
    {
        TcpListener preferred = new(IPAddress.Loopback, 1455);
        try
        {
            preferred.Start();
            return preferred;
        }
        catch (SocketException)
        {
            preferred.Stop();
            TcpListener fallback = new(IPAddress.Loopback, 1457);
            fallback.Start();
            return fallback;
        }
    }

    private static async Task<Uri?> ReadCallbackAsync(NetworkStream stream, Uri origin,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        char[] header = new char[16 * 1024];
        int length = 0;
        while (length < header.Length)
        {
            int read = await reader.ReadAsync(header.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            if (length >= 4 && header.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"))
            {
                int end = header.AsSpan(0, length).IndexOf('\r');
                string[] line = new string(header, 0, end).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return line is ["GET", var path, _] && path.StartsWith("/auth/callback?", StringComparison.Ordinal) &&
                       Uri.TryCreate(origin, path, out Uri? callback)
                    ? callback
                    : null;
            }
        }

        return null;
    }

    private static Task RespondAsync(NetworkStream stream, bool signedIn, CancellationToken cancellationToken)
    {
        string status = signedIn ? "200 OK" : "400 Bad Request";
        string body = signedIn ? "Signed in to Patchouli. You can close this tab." : "Invalid login callback.";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"),
            cancellationToken).AsTask();
    }

    internal async Task<IReadOnlyList<LlmSubscriptionModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await FetchModelsAsync(false, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return await ReadModelsAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using HttpResponseMessage retry = await FetchModelsAsync(true, cancellationToken).ConfigureAwait(false);
        return await ReadModelsAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> FetchModelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{BaseUrl}/models?client_version={ProtocolVersion}");
        foreach ((string name, string value) in await GetHeadersAsync(forceRefresh, cancellationToken)
                     .ConfigureAwait(false))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<LlmSubscriptionModel>> ReadModelsAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        using JsonDocument json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
            .ConfigureAwait(false) ?? throw new JsonException("Empty Codex model catalog.");
        List<LlmSubscriptionModel> models = [];
        foreach (JsonElement model in json.RootElement.GetProperty("models").EnumerateArray())
        {
            string? id = String(model, "slug") ?? String(model, "id");
            if (!string.IsNullOrWhiteSpace(id) &&
                (String(model, "visibility") ?? "list").Equals("list", StringComparison.OrdinalIgnoreCase) &&
                Property(model, "supported_in_api").ValueKind != JsonValueKind.False &&
                Property(model, "show_in_picker").ValueKind != JsonValueKind.False)
            {
                models.Add(new LlmSubscriptionModel(id, String(model, "display_name") ?? id,
                    Property(model, "is_default").ValueKind == JsonValueKind.True));
            }
        }

        return models;
    }

    internal async Task<IReadOnlyDictionary<string, string>> GetHeadersAsync(bool forceRefresh,
        CancellationToken cancellationToken)
    {
        LlmCodexCredentials credentials = await store.LoadAsync(cancellationToken).ConfigureAwait(false) ??
                                          throw new LlmChatException(LlmFailureCodes.AuthFailed,
                                              "Sign in to the provider subscription first.");
        if (forceRefresh || (credentials.ExpiresAtUtc ?? credentials.LastRefreshUtc.AddDays(8)) <=
            DateTimeOffset.UtcNow.AddMinutes(5))
        {
            using JsonContent content = JsonContent.Create(new
            {
                client_id = ClientId, grant_type = "refresh_token", refresh_token = credentials.RefreshToken
            });
            credentials = await ExchangeAsync(content, credentials, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, string> headers = new()
        {
            ["Authorization"] = $"Bearer {credentials.AccessToken}",
            ["ChatGPT-Account-ID"] = credentials.AccountId!,
            ["originator"] = Originator,
            ["User-Agent"] = $"{Originator}/{ProtocolVersion}",
            ["OpenAI-Beta"] = "responses=experimental"
        };
        if (credentials.IsFedRamp)
        {
            headers["X-OpenAI-Fedramp"] = "true";
        }

        return headers;
    }

    private async Task<LlmCodexCredentials> ExchangeAsync(HttpContent content, LlmCodexCredentials? previous,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.PostAsync($"{Issuer}/oauth/token", content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AiException("ChatGPT subscription token exchange failed.", (int)response.StatusCode);
        }

        // PostAsync has already buffered the response. Finish persisting any rotated token.
        using JsonDocument json = await response.Content.ReadFromJsonAsync<JsonDocument>(CancellationToken.None)
            .ConfigureAwait(false) ?? throw new JsonException("Empty OAuth response.");
        JsonElement root = json.RootElement;
        string? accessToken = String(root, "access_token");
        string? refreshToken = String(root, "refresh_token") ?? previous?.RefreshToken;
        string idToken = String(root, "id_token") ?? previous?.IdToken ?? "";
        using JsonDocument identity = ReadClaims(idToken);
        using JsonDocument access = ReadClaims(accessToken);
        JsonElement idAuth = Property(identity.RootElement, "https://api.openai.com/auth");
        JsonElement accessAuth = Property(access.RootElement, "https://api.openai.com/auth");
        string? accountId = String(accessAuth, "chatgpt_account_id") ?? String(idAuth, "chatgpt_account_id") ??
            previous?.AccountId;
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken) ||
            string.IsNullOrWhiteSpace(accountId))
        {
            throw new LlmChatException(LlmFailureCodes.AuthFailed,
                "OpenAI returned incomplete subscription credentials.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        JsonElement expiresIn = Property(root, "expires_in");
        JsonElement exp = Property(access.RootElement, "exp");
        LlmCodexCredentials credentials = new()
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            IdToken = idToken,
            AccountId = accountId,
            Email = String(identity.RootElement, "email") ??
                    String(Property(access.RootElement, "https://api.openai.com/profile"), "email") ?? previous?.Email,
            PlanType = String(accessAuth, "chatgpt_plan_type") ??
                       String(idAuth, "chatgpt_plan_type") ?? previous?.PlanType,
            IsFedRamp = Property(accessAuth, "chatgpt_account_is_fedramp").ValueKind == JsonValueKind.True ||
                        Property(idAuth, "chatgpt_account_is_fedramp").ValueKind == JsonValueKind.True,
            ExpiresAtUtc = exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out long timestamp)
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
                : expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetInt32(out int seconds)
                    ? now.AddSeconds(seconds)
                    : null,
            LastRefreshUtc = now
        };
        await store.SaveAsync(credentials, CancellationToken.None).ConfigureAwait(false);
        return credentials;
    }

    private static JsonDocument ReadClaims(string? token)
    {
        string[]? segments = token?.Split('.');
        return segments is { Length: 3 }
            ? JsonDocument.Parse(Base64Url.DecodeFromChars(segments[1]))
            : JsonDocument.Parse("{}");
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement property)
            ? property
            : default;
    }

    private static string? String(JsonElement element, string name)
    {
        JsonElement value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
