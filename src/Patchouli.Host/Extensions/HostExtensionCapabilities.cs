using System.Text;
using System.Globalization;
using System.Diagnostics;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Core.Translation;
using Patchouli.Llm;

namespace Patchouli.Host.Extensions;

/// <summary>Managed transports. Every run captures model settings independently of subsequent UI edits.</summary>
public sealed class HostExtensionCapabilities : IExtensionCapabilities, ILlmCapability, IHttpCapability
{
    private readonly Func<LlmAppSettings> _settings;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly LlmAppSettings? _frozen;
    private readonly ExtensionConfigSnapshot? _config;
    private readonly RequestPolicy? _policy;
    private readonly TranslationExecutionGate? _dispatchGate;

    public HostExtensionCapabilities(Func<LlmAppSettings> settings, ICredentialStore credentials, HttpClient http)
        : this(settings, credentials, http, null)
    {
    }

    private HostExtensionCapabilities(Func<LlmAppSettings> settings, ICredentialStore credentials, HttpClient http,
        LlmAppSettings? frozen, ExtensionConfigSnapshot? config = null, TranslationExecutionGate? dispatchGate = null)
    {
        _settings = settings;
        _credentials = credentials;
        _http = http;
        _frozen = frozen;
        _config = config;
        _dispatchGate = dispatchGate;
        _policy = frozen is null ? null : new RequestPolicy(frozen.Translation, config, dispatchGate);
    }

    public HostExtensionCapabilities WithDispatchGate(TranslationExecutionGate? gate)
    {
        return new HostExtensionCapabilities(_settings, _credentials, _http, _frozen, _config, gate);
    }

    public ILlmCapability Llm => this;
    public IHttpCapability Http => this;

    public ILlmCapability ResolveLlm(string? domain, ExtensionModelSelection? selection)
    {
        LlmAppSettings settings = _frozen ?? _settings();
        (string? provider, string? model) = domain switch
        {
            null or "metadata" => (settings.ChatProviderId, settings.ChatModel),
            "translation" => (First(settings.Translation.ProviderId, settings.ChatProviderId),
                First(settings.Translation.Model,
                    string.IsNullOrWhiteSpace(settings.Translation.ProviderId) ||
                    settings.Translation.ProviderId == settings.ChatProviderId
                        ? settings.ChatModel
                        : null)),
            _ => throw new ArgumentException($"Unknown default model domain '{domain}'.", nameof(domain))
        };
        string resolvedProvider = First(selection?.ProviderId, provider) ?? LlmAppSettings.DefaultProviderId;
        string? resolvedModel = First(selection?.Model, resolvedProvider == provider ? model : null,
            settings.FindProvider(resolvedProvider)?.Model);
        if (LlmProviderCatalog.Find(resolvedProvider) is null || string.IsNullOrWhiteSpace(resolvedModel))
        {
            throw new InvalidOperationException(
                "The bound model provider or model is unavailable. Configure this slot explicitly or repair its default.");
        }

        return new BoundLlm(this, resolvedProvider, resolvedModel);
    }

    public IHttpCapability ResolveHttp(string? domain, ExtensionConfigSnapshot config)
    {
        return this;
    }

    private sealed class BoundLlm(ILlmCapability inner, string provider, string model) : ILlmCapability
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return inner.CompleteAsync(request with { ProviderId = provider, Model = model }, cancellationToken);
        }
    }

    public string ConfigurationFingerprint => PackageFingerprint.Compute(new Dictionary<string, string>
    {
        ["model-settings"] = System.Text.Json.JsonSerializer.Serialize(_frozen ?? _settings())
    });

    public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatchGate?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
    {
        if (_frozen is not null)
        {
            // Rewrapping a run must retain its shared transport budget and frozen settings.
            return this;
        }

        LlmAppSettings settings = _frozen ?? _settings();
        return new HostExtensionCapabilities(_settings, _credentials, _http,
            settings with { Providers = settings.Providers.ToArray() }, config, _dispatchGate);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        using IDisposable? slot =
            _policy is null ? null : await _policy.EnterAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await CompleteCoreAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The extension model request timed out.");
        }
    }

    private async Task<LlmResponse> CompleteCoreAsync(LlmRequest request, CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        Result<BoundLlmCall> prepared = await PrepareLlmCallAsync(request, cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            throw CreateLlmFailure(prepared.ErrorCode!, prepared.ErrorMessage!);
        }

        BoundLlmCall call = prepared.Value;
        Result<LlmChatCompletion> completion = await DispatchAsync(token => call.Client.CompleteAsync(
            call.ConversationKey, call.Request, token), timeout, cancellationToken).ConfigureAwait(false);
        if (completion.IsFailure)
        {
            throw CreateLlmFailure(completion.ErrorCode!, completion.ErrorMessage!);
        }

        return new LlmResponse(completion.Value.Text, completion.Value.Model,
            completion.Value.Usage.PromptTokens, completion.Value.Usage.CompletionTokens);
    }

    private async Task<Result<BoundLlmCall>> PrepareLlmCallAsync(LlmRequest request,
        CancellationToken cancellationToken)
    {
        LlmAppSettings settings = _frozen ?? _settings();
        string provider = First(request.ProviderId, settings.Translation.ProviderId, settings.ChatProviderId)
                          ?? LlmAppSettings.DefaultProviderId;
        string translationProvider = First(settings.Translation.ProviderId, settings.ChatProviderId)
                                     ?? LlmAppSettings.DefaultProviderId;
        string? model = First(request.Model,
            provider == translationProvider ? settings.Translation.Model : null,
            provider == settings.ChatProviderId ? settings.ChatModel : null);
        Result<LlmProviderRuntimeSettings> resolved = await LlmProviderClientFactory.ResolveAsync(settings, provider,
            model, _credentials, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result<BoundLlmCall>.Failure(resolved.ErrorCode!, resolved.ErrorMessage!);
        }

        string instructions = string.Join("\n", request.Messages.Where(message => message.Role == "system")
            .Select(message => message.Content));
        LlmChatHistory history = LlmChatHistory.Create(instructions);
        history = history.Append(request.Messages.Where(message => message.Role != "system").Select(message =>
            message.Role switch
            {
                "user" => LlmChatMessage.User(message.Content),
                "assistant" => LlmChatMessage.Assistant(message.Content),
                _ => throw new ArgumentException("Unsupported extension LLM message role.", nameof(request))
            }).ToArray());
        LlmChatClient client = new(resolved.Value, LlmClientFactory.CreateTransport(resolved.Value));
        LlmChatRequest chatRequest = new(history, Options: new LlmChatRequestOptions(resolved.Value.Model,
            request.Temperature is { } temperature ? (double)temperature : null, request.MaxOutputTokens));
        return Result<BoundLlmCall>.Success(new BoundLlmCall(client, Guid.NewGuid().ToString("N"), chatRequest));
    }

    private static Exception CreateLlmFailure(string code, string message)
    {
        string fullMessage = $"{code}: {message}";
        return code switch
        {
            LlmFailureCodes.NetworkTimeout => new TimeoutException(fullMessage),
            LlmFailureCodes.RateLimited => new HttpRequestException(fullMessage, null,
                System.Net.HttpStatusCode.TooManyRequests),
            LlmFailureCodes.TemporaryProviderError => new HttpRequestException(fullMessage, null,
                System.Net.HttpStatusCode.ServiceUnavailable),
            _ => new InvalidOperationException(fullMessage)
        };
    }

    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        using IDisposable? slot =
            _policy is null ? null : await _policy.EnterAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await SendCoreAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The extension HTTP request timed out.");
        }
    }

    private async Task<HttpResponse> SendCoreAsync(HttpRequest request, CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        if (!request.Uri.IsAbsoluteUri || request.Uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(request.Uri.UserInfo))
        {
            throw new ArgumentException("An absolute HTTP(S) endpoint without embedded credentials is required.",
                nameof(request));
        }

        using HttpRequestMessage message = new(new HttpMethod(request.Method), request.Uri);
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8);
        }

        foreach ((string name, string value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                if (message.Content is null)
                {
                    throw new ArgumentException("Content headers require a request body.", nameof(request));
                }

                message.Content.Headers.Remove(name);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.CredentialReference))
        {
            if (_config is null ||
                !_config.Values.TryGetValue("credential-reference", out string? configuredReference) ||
                configuredReference != request.CredentialReference ||
                !_config.Values.TryGetValue("endpoint", out string? configuredEndpoint) ||
                !Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out Uri? endpoint) ||
                endpoint.GetLeftPart(UriPartial.Authority) != request.Uri.GetLeftPart(UriPartial.Authority))
            {
                throw new InvalidOperationException("The credential is not bound to this configured endpoint.");
            }

            Result<string> secret = await _credentials.GetActiveSecretForProviderAsync(request.CredentialReference,
                cancellationToken).ConfigureAwait(false);
            if (secret.IsFailure)
            {
                throw new InvalidOperationException("The configured credential is unavailable.");
            }

            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret.Value);
        }

        // DispatchAsync awaits this callback before the enclosing using scope disposes the request.
        // ReSharper disable once AccessToDisposedClosure
        using HttpResponseMessage response = await DispatchAsync(token => _http.SendAsync(message,
            HttpCompletionOption.ResponseHeadersRead, token), timeout, cancellationToken).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return new HttpResponse((int)response.StatusCode, headers, body);
    }

    private static string? First(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private Task<T> DispatchAsync<T>(Func<CancellationToken, Task<T>> dispatch, CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        if (_policy is not null)
        {
            return _policy.DispatchAsync(dispatch, timeout, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        return dispatch(timeout.Token);
    }

    private sealed class RequestPolicy
    {
        private readonly SemaphoreSlim _slots;
        private readonly SemaphoreSlim _pacer = new(1, 1);
        private readonly TimeSpan _interval;
        private readonly TranslationExecutionGate? _dispatchGate;
        private long _nextStart;

        public RequestPolicy(TranslationAppSettings settings, ExtensionConfigSnapshot? config,
            TranslationExecutionGate? dispatchGate)
        {
            _dispatchGate = dispatchGate;

            decimal Read(string key, decimal fallback, decimal minimum, decimal maximum)
            {
                if (config is null || !config.Values.TryGetValue(key, out string? text))
                {
                    return fallback;
                }

                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ||
                    value < minimum || value > maximum)
                {
                    throw new ArgumentException($"Invalid extension request policy field: {key}.");
                }

                return value;
            }

            decimal concurrency = Read("concurrency", settings.Concurrency, 1, 32);
            if (decimal.Truncate(concurrency) != concurrency)
            {
                throw new ArgumentException("Extension concurrency must be an integer.");
            }

            _slots = new SemaphoreSlim((int)concurrency, (int)concurrency);
            _interval = TimeSpan.FromSeconds((double)Read("request-interval", settings.RequestIntervalSeconds, 0,
                3600));
            Timeout = TimeSpan.FromSeconds((double)Read("request-timeout", settings.RequestTimeoutSeconds, 1, 3600));
        }

        public TimeSpan Timeout { get; }

        public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new RequestSlot(_slots);
        }

        public async Task<T> DispatchAsync<T>(Func<CancellationToken, Task<T>> dispatch,
            CancellationTokenSource timeout, CancellationToken cancellationToken)
        {
            Task<T> pending;
            await _pacer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long remaining;
                while ((remaining = _nextStart - Stopwatch.GetTimestamp()) > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency),
                        cancellationToken).ConfigureAwait(false);
                }

                if (_dispatchGate is not null)
                {
                    await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                timeout.CancelAfter(Timeout);
                pending = dispatch(timeout.Token);
                _nextStart = Stopwatch.GetTimestamp() + (long)(_interval.TotalSeconds * Stopwatch.Frequency);
            }
            finally
            {
                _pacer.Release();
            }

            return await pending.ConfigureAwait(false);
        }
    }

    private sealed record BoundLlmCall(LlmChatClient Client, string ConversationKey, LlmChatRequest Request);

    private sealed class RequestSlot(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
