using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace Patchouli.Host.Extensions;

/// <summary>
/// In-memory scheduler for extension HTTP requests owned by the Host capability root.
/// Coordinates rate limits, cooldowns, and concurrency per normalized endpoint identity.
/// </summary>
public sealed class ExtensionHttpScheduler
{
    private static readonly string[] HttpDateFormats =
    [
        "r",
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "ddd, dd MMM yyyy HH:mm:ss",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "dddd, dd-MMM-yy HH:mm:ss",
        "ddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd, dd-MMM-yy HH:mm:ss",
        "ddd MMM  d HH:mm:ss yyyy",
        "ddd MMM d HH:mm:ss yyyy"
    ];

    private readonly ConcurrentDictionary<string, EndpointBucket> _buckets = new(StringComparer.Ordinal);

    public ExtensionHttpScheduler(
        TimeSpan? defaultFallbackDelay = null,
        TimeProvider? timeProvider = null,
        int maxConsecutiveRateLimits = 3)
    {
        DefaultFallbackDelay = defaultFallbackDelay ?? TimeSpan.FromSeconds(2);
        if (DefaultFallbackDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultFallbackDelay));
        }

        if (maxConsecutiveRateLimits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveRateLimits));
        }

        TimeProvider = timeProvider ?? TimeProvider.System;
        MaxConsecutiveRateLimits = maxConsecutiveRateLimits;
    }

    public TimeSpan DefaultFallbackDelay { get; }
    public TimeProvider TimeProvider { get; }
    public int MaxConsecutiveRateLimits { get; }

    /// <summary>
    /// Normalizes endpoint URL to an origin + path quota identity, excluding query strings,
    /// fragments, and credentials to prevent leaking secrets into keys.
    /// </summary>
    public static string NormalizeQuotaKey(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("An absolute URI is required.", nameof(uri));
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host.ToLowerInvariant();
        string authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        string path = uri.AbsolutePath.TrimEnd('/');
        return $"{scheme}://{authority}{path}";
    }

    /// <summary>
    /// Parses Retry-After header as integer seconds or HTTP dates (RFC 1123, RFC 850, asctime).
    /// Applies deterministic fallback backoff when header is missing, malformed, or past.
    /// </summary>
    public TimeSpan ParseRetryAfter(string? retryAfterHeader, DateTimeOffset now, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(retryAfterHeader))
        {
            return fallback;
        }

        string trimmed = retryAfterHeader.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            if (seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            // Zero or negative seconds indicates an already expired or invalid value.
            return fallback;
        }

        if (TryParseHttpDate(trimmed, out DateTimeOffset date))
        {
            TimeSpan diff = date - now;
            if (diff > TimeSpan.Zero)
            {
                return diff;
            }

            // Expired date in the past.
            return fallback;
        }

        // Malformed header.
        return fallback;
    }

    public static bool TryParseHttpDate(string value, out DateTimeOffset date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = default;
            return false;
        }

        string trimmed = value.Trim();
        if (DateTimeOffset.TryParseExact(trimmed, HttpDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
        {
            return true;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }

    public bool IsInCooldown(Uri uri, out TimeSpan remaining)
    {
        string key = NormalizeQuotaKey(uri);
        if (_buckets.TryGetValue(key, out EndpointBucket? bucket))
        {
            return bucket.IsInCooldown(TimeProvider.GetUtcNow(), out remaining);
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    public DateTimeOffset? GetCooldownUntil(Uri uri)
    {
        string key = NormalizeQuotaKey(uri);
        if (_buckets.TryGetValue(key, out EndpointBucket? bucket))
        {
            DateTimeOffset until = bucket.CooldownUntil;
            return until == DateTimeOffset.MinValue ? null : until;
        }

        return null;
    }

    public int GetConsecutiveRateLimits(Uri uri)
    {
        string key = NormalizeQuotaKey(uri);
        return _buckets.TryGetValue(key, out EndpointBucket? bucket) ? bucket.ConsecutiveRateLimits : 0;
    }

    /// <summary>
    /// Executes a request through the shared scheduler, enforcing endpoint cooldown,
    /// concurrency limits, and pacing without confusing cooldown wait with network timeout.
    /// </summary>
    public async Task<HttpResponseMessage> ExecuteAsync(
        HttpRequestMessage request,
        int? concurrency,
        TimeSpan? interval,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeDispatch = null,
        Uri? quotaEndpoint = null,
        string? quotaPartition = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(send);

        Uri requestUri = request.RequestUri ??
                         throw new ArgumentException("An absolute request URI is required.", nameof(request));
        Uri identityEndpoint = quotaEndpoint is not null && IsWithinEndpoint(requestUri, quotaEndpoint)
            ? quotaEndpoint
            : requestUri;
        string key = NormalizeQuotaKey(identityEndpoint);
        if (!string.IsNullOrWhiteSpace(quotaPartition))
        {
            key += "|" + quotaPartition.Trim();
        }

        EndpointBucket bucket = _buckets.GetOrAdd(key, _ => new EndpointBucket());
        IDisposable policy = bucket.RegisterPolicy(concurrency ?? 4, interval ?? TimeSpan.Zero);
        bool transferredPolicy = false;
        try
        {
            while (true)
            {
                await bucket.WaitCooldownAsync(TimeProvider, cancellationToken).ConfigureAwait(false);
                IDisposable slot = await bucket.EnterConcurrencyAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await bucket.PaceAsync(TimeProvider, beforeDispatch, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    slot.Dispose();
                    throw;
                }

                // Re-check shared cooldown and probe budget after every potentially blocking gate.
                if (!bucket.TryBeginDispatch(TimeProvider.GetUtcNow(), MaxConsecutiveRateLimits,
                        out TimeSpan remaining, out bool rejected))
                {
                    if (rejected)
                    {
                        slot.Dispose();
                        return CreateRateLimitRejection();
                    }

                    slot.Dispose();
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, TimeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                HttpResponseMessage response;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    response = await send(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    bucket.EndProbeWithoutSuccess();
                    slot.Dispose();
                    throw;
                }

                // Inspect response and update quota tracking.
                if ((int)response.StatusCode == 429)
                {
                    string? retryAfter = response.Headers.RetryAfter?.ToString();

                    DateTimeOffset now = TimeProvider.GetUtcNow();
                    TimeSpan delay = ParseRetryAfter(retryAfter, now, DefaultFallbackDelay);
                    bucket.RecordRateLimit(delay, now);
                }
                else if (response.IsSuccessStatusCode)
                {
                    bucket.RecordSuccess();
                }
                else
                {
                    bucket.EndProbeWithoutSuccess();
                }

                HttpContent originalContent = response.Content;
                response.Content = new ScheduledContent(originalContent, new ResponseLease(slot, policy));
                transferredPolicy = true;
                return response;
            }
        }
        finally
        {
            if (!transferredPolicy)
            {
                policy.Dispose();
            }
        }
    }

    private static bool IsWithinEndpoint(Uri requestUri, Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.Equals(requestUri.GetLeftPart(UriPartial.Authority),
                endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string basePath = endpoint.AbsolutePath.TrimEnd('/');
        return requestUri.AbsolutePath.Equals(basePath, StringComparison.OrdinalIgnoreCase) ||
               requestUri.AbsolutePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CreateRateLimitRejection()
    {
        return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("Host is waiting for a rate-limit cooldown probe.")
        };
    }

    private sealed class ResponseLease(IDisposable slot, IDisposable policy) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    slot.Dispose();
                }
                finally
                {
                    policy.Dispose();
                }
            }
        }
    }

    private sealed class ScheduledContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly IDisposable _slot;

        public ScheduledContent(HttpContent inner, IDisposable slot)
        {
            _inner = inner;
            _slot = slot;
            foreach ((string name, IEnumerable<string> values) in inner.Headers)
            {
                Headers.TryAddWithoutValidation(name, values);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            try
            {
                await _inner.CopyToAsync(stream).ConfigureAwait(false);
            }
            finally
            {
                _slot.Dispose();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength.GetValueOrDefault();
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return _inner.ReadAsStreamAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _slot.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class EndpointBucket
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _pacer = new(1, 1);
        private readonly Dictionary<long, (int Concurrency, TimeSpan Interval)> _policies = [];
        private TaskCompletionSource _changed = NewSignal();
        private int _active;
        private long _nextPolicyId;
        private long _nextStartTimestamp;
        private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
        private int _consecutiveRateLimits;
        private bool _probeInFlight;

        public EndpointBucket()
        {
        }

        private static TaskCompletionSource NewSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IDisposable RegisterPolicy(int concurrency, TimeSpan interval)
        {
            lock (_gate)
            {
                long id = ++_nextPolicyId;
                _policies.Add(id,
                    (Math.Clamp(concurrency, 1, 32), interval > TimeSpan.Zero ? interval : TimeSpan.Zero));
                PulseChanged();
                return new PolicyRegistration(this, id);
            }
        }

        private void UnregisterPolicy(long id)
        {
            lock (_gate)
            {
                _policies.Remove(id);
                PulseChanged();
            }
        }

        private void PulseChanged()
        {
            TaskCompletionSource previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }

        private int EffectiveConcurrency =>
            _policies.Count == 0 ? 4 : _policies.Values.Min(policy => policy.Concurrency);

        private TimeSpan EffectiveInterval =>
            _policies.Count == 0 ? TimeSpan.Zero : _policies.Values.Max(policy => policy.Interval);

        public DateTimeOffset CooldownUntil
        {
            get
            {
                lock (_gate)
                {
                    return _cooldownUntil;
                }
            }
        }

        public int ConsecutiveRateLimits
        {
            get
            {
                lock (_gate)
                {
                    return _consecutiveRateLimits;
                }
            }
        }

        public bool IsInCooldown(DateTimeOffset now, out TimeSpan remaining)
        {
            lock (_gate)
            {
                if (_cooldownUntil > now)
                {
                    remaining = _cooldownUntil - now;
                    return true;
                }

                remaining = TimeSpan.Zero;
                return false;
            }
        }

        public void RecordRateLimit(TimeSpan cooldown, DateTimeOffset now)
        {
            lock (_gate)
            {
                _consecutiveRateLimits++;
                _probeInFlight = false;
                DateTimeOffset until = now + cooldown;
                if (until > _cooldownUntil)
                {
                    _cooldownUntil = until;
                }
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _consecutiveRateLimits = 0;
                _probeInFlight = false;
            }
        }

        public void EndProbeWithoutSuccess()
        {
            lock (_gate)
            {
                _probeInFlight = false;
            }
        }

        public bool TryBeginDispatch(DateTimeOffset now, int maxRateLimits, out TimeSpan remaining,
            out bool rejected)
        {
            lock (_gate)
            {
                remaining = _cooldownUntil > now ? _cooldownUntil - now : TimeSpan.Zero;
                rejected = false;
                if (remaining > TimeSpan.Zero)
                {
                    return false;
                }

                if (_consecutiveRateLimits >= maxRateLimits)
                {
                    if (_probeInFlight)
                    {
                        rejected = true;
                        return false;
                    }

                    _probeInFlight = true;
                }

                return true;
            }
        }

        public async Task WaitCooldownAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (_gate)
                {
                    DateTimeOffset now = timeProvider.GetUtcNow();
                    if (_cooldownUntil <= now)
                    {
                        return;
                    }

                    delay = _cooldownUntil - now;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async Task<IDisposable> EnterConcurrencyAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_active < EffectiveConcurrency)
                    {
                        _active++;
                        return new ConcurrencySlot(this);
                    }

                    wait = _changed.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void ReleaseConcurrency()
        {
            lock (_gate)
            {
                _active--;
                PulseChanged();
            }
        }

        public async Task PaceAsync(TimeProvider timeProvider, Func<CancellationToken, Task>? beforeDispatch,
            CancellationToken cancellationToken)
        {
            await _pacer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    TimeSpan interval;
                    lock (_gate)
                    {
                        interval = EffectiveInterval;
                    }

                    long now = timeProvider.GetTimestamp();
                    TimeSpan remaining = _nextStartTimestamp > now
                        ? timeProvider.GetElapsedTime(now, _nextStartTimestamp)
                        : TimeSpan.Zero;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    if (beforeDispatch is not null)
                    {
                        await beforeDispatch(cancellationToken).ConfigureAwait(false);
                    }

                    now = timeProvider.GetTimestamp();
                    remaining = _nextStartTimestamp > now
                        ? timeProvider.GetElapsedTime(now, _nextStartTimestamp)
                        : TimeSpan.Zero;
                    if (remaining > TimeSpan.Zero)
                    {
                        continue;
                    }

                    lock (_gate)
                    {
                        if (EffectiveInterval > interval)
                        {
                            continue;
                        }
                    }

                    _nextStartTimestamp = timeProvider.GetTimestamp() +
                                          (long)(interval.TotalSeconds * timeProvider.TimestampFrequency);
                    return;
                }
            }
            finally
            {
                _pacer.Release();
            }
        }

        private sealed class PolicyRegistration(EndpointBucket bucket, long id) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    bucket.UnregisterPolicy(id);
                }
            }
        }

        private sealed class ConcurrencySlot(EndpointBucket bucket) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    bucket.ReleaseConcurrency();
                }
            }
        }
    }
}
