using System;

namespace Patchouli.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    event Action? Advanced
    {
        add { }
        remove { }
    }
}
