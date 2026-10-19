namespace Patchouli.Terminal;

/// <summary>
/// Tiered cleanup timings for <see cref="TerminalSessionManager"/>.
/// </summary>
public sealed record TerminalStopOptions(TimeSpan GracePeriod, TimeSpan ForceWait)
{
    /// <summary>
    /// Gets the default timings used for an explicit stop.
    /// </summary>
    public static TerminalStopOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

    /// <summary>
    /// Gets short timings used while the host is shutting down.
    /// </summary>
    public static TerminalStopOptions HostShutdown { get; } =
        new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(750));

    internal void Validate()
    {
        if (GracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(GracePeriod), GracePeriod,
                "The grace period cannot be negative.");
        }

        if (ForceWait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ForceWait), ForceWait,
                "The force wait cannot be negative.");
        }
    }
}
