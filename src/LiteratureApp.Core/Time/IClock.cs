namespace LiteratureApp.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
