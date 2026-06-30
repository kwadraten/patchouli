namespace Patchouli.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
