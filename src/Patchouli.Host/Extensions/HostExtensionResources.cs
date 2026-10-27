using System.Globalization;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Core.Translation;
using Patchouli.Llm;

namespace Patchouli.Host.Extensions;

/// <summary>Managed extension resources. Every run captures model settings independently of subsequent UI edits.</summary>
public sealed class HostExtensionResources : IExtensionResources, ILlmCapability
{
    private readonly Func<LlmAppSettings> _settings;
    private readonly ICredentialStore _credentials;
    private readonly HttpMessageInvoker _http;
    private readonly ExtensionHttpScheduler _scheduler;
    private readonly LlmAppSettings? _frozen;
    private readonly ExtensionConfigSnapshot? _config;
    private readonly RequestPolicy? _policy;
    private readonly TranslationExecutionGate? _dispatchGate;
    private readonly string? _domain;
    private readonly HttpClient _extensionHttp;
    private readonly Func<LlmProviderRuntimeSettings, ILlmChatTransport>? _transportFactory;

    public HostExtensionResources(Func<LlmAppSettings> settings, ICredentialStore credentials,
        HttpMessageInvoker http)
        : this(settings, credentials, http, new ExtensionHttpScheduler(), null)
    {
    }

    public HostExtensionResources(Func<LlmAppSettings> settings, ICredentialStore credentials,
        HttpMessageInvoker http,
        ExtensionHttpScheduler scheduler)
        : this(settings, credentials, http, scheduler, null)
    {
    }

    /// <summary>Fixture-only seam: substitutes the provider transport so tests can inspect
    /// the exact wire request without any network access. Production paths never set it.</summary>
    internal HostExtensionResources(Func<LlmAppSettings> settings, ICredentialStore credentials,
        HttpMessageInvoker http, Func<LlmProviderRuntimeSettings, ILlmChatTransport> transportFactory)
        : this(settings, credentials, http, new ExtensionHttpScheduler(), null,
            transportFactory: transportFactory)
    {
    }

    private HostExtensionResources(Func<LlmAppSettings> settings, ICredentialStore credentials,
        HttpMessageInvoker http,
        ExtensionHttpScheduler scheduler, LlmAppSettings? frozen, ExtensionConfigSnapshot? config = null,
        TranslationExecutionGate? dispatchGate = null, string? domain = null,
        Func<LlmProviderRuntimeSettings, ILlmChatTransport>? transportFactory = null)
    {
        _settings = settings;
        _credentials = credentials;
        _http = http;
        _scheduler = scheduler;
        _frozen = frozen;
        _config = config;
        _dispatchGate = dispatchGate;
        _domain = domain;
        _transportFactory = transportFactory;
        _policy = frozen is null ? null : new RequestPolicy(frozen.Translation, config, dispatchGate, domain);
        _extensionHttp = new HttpClient(new ExtensionHttpHandler(new InvokerHandler(http), scheduler, this),
            true);
        // Queueing, cooldown and pause time are not network time. The handler starts the configured
        // timeout only once a request is actually dispatched.
        _extensionHttp.Timeout = Timeout.InfiniteTimeSpan;
    }

    public ExtensionHttpScheduler Scheduler => _scheduler;

    public HostExtensionResources ForDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return new HostExtensionResources(_settings, _credentials, _http, _scheduler, _frozen, _config,
            _dispatchGate, domain, _transportFactory);
    }

    public HostExtensionResources WithDispatchGate(TranslationExecutionGate? gate)
    {
        return new HostExtensionResources(_settings, _credentials, _http, _scheduler, _frozen, _config, gate,
            _domain, _transportFactory);
    }

    public ILlmCapability Llm => this;
    public HttpClient Http => _extensionHttp;

    public ILlmCapability ResolveLlm(string? domain, ExtensionModelSelection? selection)
    {
        LlmAppSettings settings = _frozen ?? _settings();
        // A null request falls back to this instance's domain, matching PrepareLlmCallAsync.
        string? effectiveDomain = domain ?? _domain;
        (string provider, string model, string? reasoning) = ResolveModelSelection(settings,
            effectiveDomain, selection, true);
        return new BoundLlm(this, provider, model, effectiveDomain, reasoning);
    }

    private static (string Provider, string Model, string? Reasoning) ResolveModelSelection(
        LlmAppSettings settings, string? effectiveDomain, ExtensionModelSelection? selection,
        bool requireAvailable)
    {
        (string? provider, string? model, string? reasoning) = effectiveDomain switch
        {
            null or "metadata" => (settings.ChatProviderId, settings.ChatModel, (string?)null),
            "translation" => (First(settings.Translation.ProviderId, settings.ChatProviderId),
                First(settings.Translation.Model,
                    string.IsNullOrWhiteSpace(settings.Translation.ProviderId) ||
                    settings.Translation.ProviderId == settings.ChatProviderId
                        ? settings.ChatModel
                        : null),
                null),
            // The OCR domain is a separate frozen slot: it never inherits the chat or
            // translation defaults, and it carries its own reasoning effort.
            "ocr" => (settings.OcrSelection.ProviderId, settings.OcrSelection.Model,
                settings.OcrReasoningEffort),
            _ => throw new ArgumentException($"Unknown default model domain '{effectiveDomain}'.",
                nameof(effectiveDomain))
        };
        string resolvedProvider = First(selection?.ProviderId, provider) ?? LlmAppSettings.DefaultProviderId;
        string? resolvedModel = First(selection?.Model, resolvedProvider == provider ? model : null,
            settings.FindProvider(resolvedProvider)?.Model);
        if (requireAvailable && (LlmProviderCatalog.Find(resolvedProvider) is null ||
                                 string.IsNullOrWhiteSpace(resolvedModel)))
        {
            throw new InvalidOperationException(
                "The bound model provider or model is unavailable. Configure this slot explicitly or repair its default.");
        }

        return (resolvedProvider, resolvedModel ?? string.Empty, reasoning);
    }

    /// <summary>
    /// Captures followed model slots into the job's configuration and resources from ONE settings
    /// snapshot. Metadata resolution does not check credentials or require an unused image branch.
    /// </summary>
    internal (IExtensionResources Resources, ExtensionConfigSnapshot Config) ForRunWithModelDefaults(
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config)
    {
        LlmAppSettings settings = _frozen ?? _settings();
        settings = settings with { Providers = settings.Providers.ToArray() };
        Dictionary<string, string> values = new(config.Values, StringComparer.Ordinal);
        foreach (ConfigFieldDescriptor field in fields.Where(field =>
                     field.Kind == ConfigValueKind.Model && field.FollowDefault))
        {
            ExtensionModelSelection? selection = values.TryGetValue(field.Key, out string? raw) &&
                                                 !string.IsNullOrWhiteSpace(raw)
                ? JsonSerializer.Deserialize<ExtensionModelSelection>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : null;
            if (field.DefaultDomain == "ocr" && values.TryGetValue("model-id", out string? modelId) &&
                !string.IsNullOrWhiteSpace(modelId))
            {
                selection = new ExtensionModelSelection(selection?.ProviderId ?? string.Empty, modelId);
            }

            (string provider, string model, string? reasoning) = ResolveModelSelection(settings,
                field.DefaultDomain ?? _domain, selection, false);
            values[field.Key] = JsonSerializer.Serialize(new ExtensionModelSelection(provider, model));
            if (field.DefaultDomain == "ocr" &&
                (!values.TryGetValue("reasoning-effort", out string? configuredReasoning) ||
                 string.IsNullOrWhiteSpace(configuredReasoning) || configuredReasoning == "default"))
            {
                values["reasoning-effort"] = reasoning ?? "default";
            }
        }

        ExtensionConfigSnapshot frozen = new(new ReadOnlyDictionary<string, string>(values), config.Revision);
        return (new HostExtensionResources(_settings, _credentials, _http, _scheduler, settings,
            frozen, _dispatchGate, _domain, _transportFactory), frozen);
    }

    private sealed class BoundLlm(
        ILlmCapability inner,
        string provider,
        string model,
        string? domain,
        string? reasoning) : ILlmCapability
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return inner.CompleteAsync(request with
            {
                ProviderId = provider,
                Model = model,
                Domain = domain ?? request.Domain,
                ReasoningEffort = request.ReasoningEffort ?? reasoning
            }, cancellationToken);
        }
    }

    public string ConfigurationFingerprint => PackageFingerprint.Compute(new Dictionary<string, string>
    {
        ["model-settings"] = JsonSerializer.Serialize(_frozen ?? _settings())
    });

    public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatchGate?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public IExtensionResources ForRun(ExtensionConfigSnapshot config)
    {
        if (_frozen is not null)
        {
            // Rewrapping a run must retain its shared transport budget and frozen settings.
            return this;
        }

        LlmAppSettings settings = _frozen ?? _settings();
        return new HostExtensionResources(_settings, _credentials, _http, _scheduler,
            settings with { Providers = settings.Providers.ToArray() }, config, _dispatchGate, _domain,
            _transportFactory);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Synchronous, before the first await: validate every image and copy its bytes into
        // owned arrays so caller-side mutation of a shared byte[] during pending credential or
        // transport work cannot change — or invalidate — what reaches the provider.
        request = SnapshotImages(request);
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
        // The request domain selects which frozen slot supplies provider/model/reasoning defaults.
        // Requests that never got a bound domain keep the historical translation fallback.
        string? domain = First(request.Domain, _domain);
        bool isOcr = string.Equals(domain, "ocr", StringComparison.Ordinal);
        bool isMetadata = string.Equals(domain, "metadata", StringComparison.Ordinal);
        if (domain is not null && !isOcr && !isMetadata &&
            !string.Equals(domain, "translation", StringComparison.Ordinal))
        {
            return Result<BoundLlmCall>.Failure(AppErrorCodes.ValidationFailed,
                $"Unknown default model domain '{domain}'.");
        }

        string translationProvider = First(settings.Translation.ProviderId, settings.ChatProviderId)
                                     ?? LlmAppSettings.DefaultProviderId;
        string ocrProvider = First(settings.OcrSelection.ProviderId) ?? LlmAppSettings.DefaultProviderId;
        string provider = First(request.ProviderId,
            isOcr ? ocrProvider
            : isMetadata ? settings.ChatProviderId
            : translationProvider) ?? LlmAppSettings.DefaultProviderId;
        string? model = First(request.Model,
            isOcr ? provider == ocrProvider ? settings.OcrSelection.Model : null
            : provider == translationProvider ? settings.Translation.Model : null,
            provider == settings.ChatProviderId ? settings.ChatModel : null);
        string? reasoning = First(request.ReasoningEffort, isOcr ? settings.OcrReasoningEffort : null);
        reasoning = string.IsNullOrWhiteSpace(reasoning) || reasoning == "default"
            ? null
            : LlmAppSettings.NormalizeReasoningEffort(reasoning);
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
        {
            bool hasImages = message.Images is { Count: > 0 };
            List<LlmMessagePart> parts = [new LlmMessagePart.LlmTextPart(message.Content)];
            if (hasImages)
            {
                foreach (LlmImage image in message.Images!)
                {
                    parts.Add(CreateImagePart(image));
                }
            }

            return message.Role switch
            {
                "user" => new LlmChatMessage(LlmChatRole.User, parts),
                "assistant" when !hasImages => new LlmChatMessage(LlmChatRole.Assistant, parts),
                "assistant" => throw new InvalidOperationException(
                    "Extension LLM images are only supported on user messages."),
                _ => throw new ArgumentException("Unsupported extension LLM message role.", nameof(request))
            };
        }).ToArray());
        Func<LlmProviderRuntimeSettings, ILlmChatTransport> createTransport =
            _transportFactory ?? LlmClientFactory.CreateTransport;
        LlmChatClient client = new(resolved.Value, createTransport(resolved.Value));
        LlmChatRequest chatRequest = new(history, Options: new LlmChatRequestOptions(resolved.Value.Model,
            request.Temperature is { } temperature ? (double)temperature : null, request.MaxOutputTokens)
        {
            ReasoningEffort = reasoning
        });
        return Result<BoundLlmCall>.Success(new BoundLlmCall(client, Guid.NewGuid().ToString("N"), chatRequest));
    }

    /// <summary>
    /// Encodes an already-validated, already-owned image as a provider image part. Validation and
    /// the defensive byte copy happen once in <see cref="SnapshotImages"/> at call entry.
    /// </summary>
    private static LlmMessagePart.LlmImagePart CreateImagePart(LlmImage image)
    {
        return new LlmMessagePart.LlmImagePart(
            $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}",
            image.MediaType, image.Detail ?? "auto");
    }

    /// <summary>
    /// Rebuilds <paramref name="request"/> with every message image validated and replaced by an
    /// owned byte copy carrying a normalized media type and detail. Runs synchronously at call
    /// entry — before credentials, dispatch gates, or HTTP work can observe the caller's buffers —
    /// so a mutable <c>byte[]</c> passed by an extension cannot be reshaped under an in-flight
    /// request. Returns the request unchanged when it carries no images.
    /// </summary>
    private static LlmRequest SnapshotImages(LlmRequest request)
    {
        bool hasImages = false;
        foreach (LlmMessage message in request.Messages)
        {
            if (message.Images is { Count: > 0 })
            {
                hasImages = true;
                break;
            }
        }

        if (!hasImages)
        {
            return request;
        }

        return request with
        {
            Messages = request.Messages.Select(message => message.Images is { Count: > 0 }
                ? message with { Images = message.Images.Select(SnapshotImage).ToArray() }
                : message).ToArray()
        };
    }

    /// <summary>
    /// Validates one extension-supplied image and returns an owned copy with a normalized media
    /// type and detail. The byte signature must match the declared media type — mislabeled
    /// arbitrary bytes are rejected, and no filesystem path or credential is ever accepted or
    /// reported.
    /// </summary>
    private static LlmImage SnapshotImage(LlmImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string media = image.MediaType?.Trim().ToLowerInvariant() ?? "";
        if (media is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
        {
            throw new InvalidOperationException(
                "Extension LLM images must declare media type image/png, image/jpeg, image/webp, or image/gif.");
        }

        byte[] data = image.Data;
        if (data is null || data.Length == 0)
        {
            throw new InvalidOperationException("Extension LLM image bytes are required.");
        }

        if (data.Length > MaxImageBytes)
        {
            throw new InvalidOperationException(
                $"Extension LLM images must stay within the {MaxImageBytes / (1024 * 1024)} MB provider size limit.");
        }

        if (!MatchesSignature(data, media))
        {
            throw new InvalidOperationException(
                $"Extension LLM image bytes do not match the declared media type '{media}'.");
        }

        string? detail = image.Detail?.Trim().ToLowerInvariant();
        detail = detail is null or "" ? "auto" : detail;
        if (detail is not ("auto" or "low" or "high"))
        {
            throw new InvalidOperationException(
                "Extension LLM image detail must be one of 'auto', 'low', or 'high'.");
        }

        byte[] owned = new byte[data.Length];
        Array.Copy(data, owned, data.Length);
        return new LlmImage(owned, media, detail);
    }

    /// <summary>File-signature check: declared media type against the actual leading bytes.</summary>
    private static bool MatchesSignature(byte[] data, string media)
    {
        return media switch
        {
            "image/png" => StartsWith(data, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "image/jpeg" => StartsWith(data, [0xFF, 0xD8, 0xFF]),
            "image/gif" => StartsWith(data, [(byte)'G', (byte)'I', (byte)'F', (byte)'8']),
            "image/webp" => data.Length >= 12 &&
                            StartsWith(data, [(byte)'R', (byte)'I', (byte)'F', (byte)'F']) &&
                            data[8] == (byte)'W' && data[9] == (byte)'E' &&
                            data[10] == (byte)'B' && data[11] == (byte)'P',
            _ => false
        };
    }

    private static bool StartsWith(byte[] data, byte[] signature)
    {
        return data.AsSpan().StartsWith(signature);
    }

    private const int MaxImageBytes = 20 * 1024 * 1024;

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

    public async Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        Result<string> secret = await _credentials.GetActiveSecretForProviderAsync(reference, cancellationToken)
            .ConfigureAwait(false);
        return secret.IsSuccess ? secret.Value : null;
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
            TranslationExecutionGate? dispatchGate, string? domain = null)
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

            bool isTranslation = domain is null or "translation";
            decimal concurrencyFallback = isTranslation ? settings.Concurrency : 4;
            decimal intervalFallback = isTranslation ? settings.RequestIntervalSeconds : 0;
            decimal timeoutFallback = isTranslation ? settings.RequestTimeoutSeconds : 120;

            decimal concurrency = Read("concurrency", concurrencyFallback, 1, 32);
            if (decimal.Truncate(concurrency) != concurrency)
            {
                throw new ArgumentException("Extension concurrency must be an integer.");
            }

            Concurrency = (int)concurrency;
            Interval = TimeSpan.FromSeconds((double)Read("request-interval", intervalFallback, 0, 3600));
            Timeout = TimeSpan.FromSeconds((double)Read("request-timeout", timeoutFallback, 1, 3600));

            _slots = new SemaphoreSlim(Concurrency, Concurrency);
            _interval = Interval;
        }

        public int Concurrency { get; }
        public TimeSpan Interval { get; }
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

    private sealed class ExtensionHttpHandler(
        HttpMessageHandler inner,
        ExtensionHttpScheduler scheduler,
        HostExtensionResources resources) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("An HTTP request URI is required.");
            if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException(
                    "An absolute HTTP(S) URI without embedded credentials is required.");
            }

            int concurrency = resources._policy?.Concurrency ?? 4;
            TimeSpan interval = resources._policy?.Interval ?? TimeSpan.Zero;
            TimeSpan timeout = resources._policy?.Timeout ?? TimeSpan.FromMinutes(2);
            Uri? configuredEndpoint = null;
            string? partition = null;
            if (resources._config is not null)
            {
                if (resources._config.Values.TryGetValue("endpoint", out string? configured) &&
                    Uri.TryCreate(configured, UriKind.Absolute, out Uri? endpoint))
                {
                    configuredEndpoint = endpoint;
                }

                if (resources._config.Values.TryGetValue("credential-reference", out string? reference) &&
                    !string.IsNullOrWhiteSpace(reference))
                {
                    partition = "credential-reference:" + reference;
                }
            }

            return await scheduler.ExecuteAsync(request, concurrency, interval, async token =>
                {
                    CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeoutSource.CancelAfter(timeout);
                    try
                    {
                        HttpResponseMessage response = await base.SendAsync(request, timeoutSource.Token)
                            .ConfigureAwait(false);
                        response.Content = new TimeoutContent(response.Content, timeoutSource);
                        return response;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        timeoutSource.Dispose();
                        throw new TimeoutException("The extension HTTP request timed out.");
                    }
                    catch
                    {
                        timeoutSource.Dispose();
                        throw;
                    }
                }, cancellationToken, resources._dispatchGate is null ? null : resources.WaitToProceedAsync,
                configuredEndpoint, partition).ConfigureAwait(false);
        }
    }

    private sealed class InvokerHandler(HttpMessageInvoker invoker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // HttpMessageInvoker dispatches directly to the shared handler and does not run
            // HttpClient's one-shot request guard a second time.
            return invoker.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class TimeoutContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _timeout;

        public TimeoutContent(HttpContent inner, CancellationTokenSource timeout)
        {
            _inner = inner;
            _timeout = timeout;
            foreach ((string name, IEnumerable<string> values) in inner.Headers)
            {
                Headers.TryAddWithoutValidation(name, values);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            return _inner.CopyToAsync(stream, _timeout.Token);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength.GetValueOrDefault();
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return _inner.ReadAsStreamAsync(_timeout.Token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _timeout.Dispose();
            }

            base.Dispose(disposing);
        }
    }

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
