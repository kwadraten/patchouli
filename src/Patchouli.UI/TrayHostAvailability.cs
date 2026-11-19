using Tmds.DBus.Protocol;

namespace Patchouli.UI;

/// <summary>
/// Probes whether the session actually offers a tray host. On Windows and macOS the platform
/// shell always provides a notification area, so the answer is unconditional. On Linux, Avalonia's
/// XEmbed tray implementation is an unimplemented stub: the tray icon only works through
/// <c>DBusTrayIconImpl</c>, which registers with whatever owns
/// <c>org.kde.StatusNotifierWatcher</c> on the session bus (Plasma panels, waybar, GNOME shell
/// AppIndicator extensions). If no service owns that name — the Sway-without-waybar case — the
/// icon can never appear, and hiding the window would leave a process the user can no longer reach.
/// </summary>
internal static class TrayHostAvailability
{
    private const string StatusNotifierWatcherName = "org.kde.StatusNotifierWatcher";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Returns whether a tray host is reachable within <see cref="ProbeTimeout"/>. The Tmds
    /// session-bus operations used here do not accept a cancellation token, so the timeout is a
    /// wall-clock bound enforced by the caller, not a cooperative cancellation; every failure —
    /// missing bus, connection error, timeout — reports <see langword="false"/> so close-to-tray
    /// is only enabled when a host is positively observed.
    /// </summary>
    public static Task<bool> IsAvailableAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Task.FromResult(true);
        }

        // The D-Bus transport can do synchronous socket work before its first await, so the
        // probe body is queued to the thread pool: callers start this from the UI thread and
        // must never block on the bus there.
        Task<bool> probe = Task.Run(ProbeSessionBusAsync);
        return WaitForProbeAsync(probe);
    }

    private static async Task<bool> WaitForProbeAsync(Task<bool> probe)
    {
        if (await Task.WhenAny(probe, Task.Delay(ProbeTimeout)).ConfigureAwait(false) != probe)
        {
            return false;
        }

        return await probe.ConfigureAwait(false);
    }

    private static async Task<bool> ProbeSessionBusAsync()
    {
        try
        {
            DBusConnectionOptions options = new(DBusAddress.Session!) { AutoConnect = false };
            using DBusConnection connection = new(options);
            // Synchronous connect mirrors Avalonia.FreeDesktop DBusHelper.TryCreateNewConnection.
            await connection.ConnectAsync().AsTask().ConfigureAwait(false);
            using NameOwnerWatcher watcher = await connection
                .WatchNameOwnerAsync(StatusNotifierWatcherName)
                .ConfigureAwait(false);
            return watcher.GetCurrentOwner() is not null;
        }
        catch (Exception)
        {
            // The probe is best-effort and must never decide in favor of a tray it could not
            // observe; any transport or protocol failure means "no confirmed host".
            return false;
        }
    }
}
