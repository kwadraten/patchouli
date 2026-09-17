using System;
using Patchouli.Core.Time;

namespace Patchouli.Tests;

internal sealed class FixedClock : IClock
{
    private DateTimeOffset _utcNow;
    public event Action? Advanced;

    public FixedClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public DateTimeOffset UtcNow
    {
        get => _utcNow;
        set
        {
            _utcNow = value;
            Advanced?.Invoke();
        }
    }
}
