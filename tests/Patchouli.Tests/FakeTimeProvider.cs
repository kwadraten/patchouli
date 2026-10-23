using System.Threading;

namespace Patchouli.Tests;

internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public FakeTimeProvider(DateTimeOffset startDateTime)
    {
        _utcNow = startDateTime;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> toFire = [];
        lock (_gate)
        {
            _utcNow += delta;
            _timestamp += delta.Ticks;
            foreach (FakeTimer timer in _timers.ToArray())
            {
                if (timer.DueTime <= _utcNow)
                {
                    toFire.Add(timer);
                }
            }
        }

        foreach (FakeTimer timer in toFire)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            FakeTimer timer = new(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    private void RemoveTimer(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly FakeTimeProvider _provider;
        private readonly object? _state;
        private bool _disposed;
        private TimeSpan _period;

        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime,
            TimeSpan period)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _period = period;
            DueTime = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : provider._utcNow + dueTime;
        }

        public DateTimeOffset DueTime { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_provider._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                DueTime = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : _provider._utcNow + dueTime;
                return true;
            }
        }

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            _callback(_state);

            lock (_provider._gate)
            {
                if (!_disposed && _period > TimeSpan.Zero)
                {
                    DueTime = _provider._utcNow + _period;
                }
                else
                {
                    DueTime = DateTimeOffset.MaxValue;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _provider.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class EmptyCredentialStore : Core.Credentials.ICredentialStore
{
    public Task<Core.Results.Result<Core.Credentials.ProviderCredentialMetadata>> SaveAsync(
        string providerId,
        string displayName,
        string secretValue,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<Core.Results.Result<string>> GetActiveSecretForProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Core.Results.Result<string>.Failure("not_found", "No credential."));
    }

    public Task<Core.Results.Result> RemoveAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<Core.Results.Result<IReadOnlyList<Core.Credentials.ProviderCredentialMetadata>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
