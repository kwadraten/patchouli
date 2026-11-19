# Manual verification: close-to-tray gating on Linux (issue #74)

> **Status: not yet executed on real hardware.** This checklist must be run on a
> physical Linux machine before issue #74 can be called verified. The fix landed
> without Linux access; every step below is unverified on that platform until
> checked. The Windows/macOS behavior is unchanged by design and needs no manual
> pass.

## Scope

Verifies `TrayHostAvailability.IsAvailableAsync` (the session D-Bus probe for an
owner of `org.kde.StatusNotifierWatcher`) and the `MainWindow.OnClosing` gate that
only hides the window when the probe has positively confirmed a tray host. On
Avalonia 12.1.3 the X11 XEmbed tray implementation is a no-op stub, so SNI name
ownership is the accurate availability signal: an icon can appear iff some service
owns the watcher name.

Out of scope on purpose: Windows and macOS always report a host (the OS shell
provides the notification area), and `RequestExit` / shutdown-initiated closes
bypass the gate exactly as before. No new setting exists; the user cannot
override the probe result.

## Test matrix

| # | Scenario | Expected |
|---|----------|----------|
| 1 | KDE Plasma (X11 or Wayland) with a system tray | Closing the window hides it; the tray icon restores it; quitting from the tray menu exits |
| 2 | GNOME with an AppIndicator/KStatusNotifierItem extension | Same as 1 — the extension owns the watcher name |
| 3 | Sway with waybar `tray` module loaded | Same as 1 — waybar owns the watcher name |
| 4 | Sway with **no** StatusNotifier host (no waybar tray, no nm-applet-style daemon) | Closing the window **exits the process**; no stray `patchouli` process remains; relaunch works |
| 5 | Sway, tray host starts *after* the app (e.g. waybar restarted later) | The close that raced the missing host exits; a later launch observes the host and hides correctly — the probe result is per-window-lifetime, not a global latch |
| 6 | `DBUS_SESSION_BUS_ADDRESS` unset / session bus unreachable | Closing exits the process; the app does not hang or strand |
| 7 | Host present but slow to claim the name at app start (close the window within ~2 s of launch) | The close waits up to ~2 s for the probe, then hides if a host was found or exits if not; the window is never left hidden-and-iconless |

## Procedure

### Preparation

1. Publish or `dotnet run` the UI project on Linux.
2. Confirm the environment: `loginctl show-session $XDG_SESSION_ID -p Type`,
   `echo $XDG_CURRENT_DESKTOP`, and `dbus-send --session --print-reply
   --dest=org.freedesktop.DBus / org.freedesktop.DBus.NameHasOwner
   string:org.kde.StatusNotifierWatcher` (or `busctl --user list | grep
   StatusNotifierWatcher`) to check watcher ownership independently of the app.

### Steps

1. **Baseline on Plasma** — launch the app, close the window. It must hide, show
   the tray icon, restore on click, and exit on the tray quit action. This is the
   regression case: the probe must not have broken a working tray.
2. **Sway without tray** — launch under Sway with no StatusNotifier host. Verify
   with `busctl` that nothing owns the watcher name. Close the window: the app
   must terminate fully (`pgrep -f patchouli` returns nothing after shutdown
   completes) instead of hiding into a zombie state.
3. **Sway with waybar tray** — enable the `tray` module in waybar, relaunch the
   app. Close must hide and the icon must appear in waybar's tray.
4. **Race the probe** — under a desktop *with* a tray host, close the window
   immediately at startup (before ~2 s elapse). The close must either hide (probe
   finished true in time) or exit — never hang and never hide without a host.
5. **No session bus** — launch with `DBUS_SESSION_BUS_ADDRESS` unset. The probe
   must fail, and closing must exit cleanly with no hang (probe wall-clock bound
   is 3 s; the early-close wait bound is 2 s).

### What to check for regressions

- A successful hide must still produce a working tray icon — the probe is a
  *gate*, not the icon creation path; verify the icon actually appears and
  restores the window (the probe proving name ownership does not guarantee
  `DBusTrayIconImpl` registration succeeds for other reasons).
- `RequestExit` paths (tray menu quit, single-instance "exit" forwarding,
  in-app quit) must still terminate regardless of probe state.
- `ApplicationShutdown`/`OSShutdown` close reasons must never be cancelled by
  the gate.

## Notes for the verifier

- The probe only inspects the session bus; it does not create a tray icon.
  Wayland compositors without any SNI implementation (bare Sway, bare Hyprland)
  are exactly the case where closing must exit.
- `XDG_CURRENT_DESKTOP` is intentionally **not** consulted — availability is
  determined solely by watcher name ownership, so a future compositor with a
  tray host works without code changes.
