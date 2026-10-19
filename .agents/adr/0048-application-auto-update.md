# 0048. Application Auto-Update (Issue #15, client direct-to-GitHub)

Date: 2026-10-20

## Status

Accepted (implementation on `feature/issue-15-auto-update`). Cloud-function
reverse-proxy work is explicitly out of scope for issue #15 and is not designed here.

## Context

Patchouli ships installers through GitHub Releases
(`kwadraten/patchouli`): a Windows Inno Setup `.exe`, a macOS `.dmg` and four
Linux formats (deb, rpm, AppImage, flatpak). Users had no in-app way to learn
about or install new versions. Issue #15 asks for a complete client-side
auto-update: startup check, version prompt, download with progress, integrity
verification, install confirmation, installer launch.

Constraints from the issue:

- Cloud-function implementation, deployment, docs and proxy selection/fallback
  are all skipped — but the "version metadata query + download URL resolution"
  must converge on a **single service interface** so a future proxy can slot in.
- The release workflow publishes **no checksum files** today (only the 6
  installers). The SHA256 strategy must be explicit and must guarantee a
  corrupt file is never executed.
- Decisions required here: the single service interface, the SHA256 policy,
  the auto-check default, the check interval and the Linux default package.

## Decision

### 1. Single service interface: `IUpdateSource`

`Patchouli.Core/Update/IUpdateSource.cs` exposes one method:

```csharp
Task<Result<ReleaseInfo?>> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
```

It returns the newest **stable** release (never drafts/pre-releases) with its
version and per-platform installer download URLs (`ReleaseInfo` /
`ReleaseAssetInfo`). All client code (check orchestration, asset selection,
UI) depends only on this interface. `GitHubReleaseSource`
(`Patchouli.Infrastructure/Update`) is the direct-to-GitHub implementation,
querying `https://api.github.com/repos/kwadraten/patchouli/releases`. A future
reverse proxy implements the same interface — no client call sites change.

### 2. SHA256 verification policy

Two-tier, explicit about the missing-checksum reality:

1. **Prefer a `{asset}.sha256` sidecar.** The client requests
   `{download_url}.sha256`; if the release carries it, the downloaded file's
   SHA256 must match byte-for-byte (hex, case-insensitive), otherwise the
   download fails and the file is deleted.
2. **Fallback when no sidecar exists** (all releases before this change):
   the downloaded byte count must equal the asset `size` from the release
   metadata exactly, and the file must be non-empty. Transport integrity comes
   from HTTPS. The install-confirmation dialog states honestly which tier was
   used.

To make tier 1 real going forward, `.github/workflows/release.yml` now
generates a `<asset>.sha256` sidecar (`sha256sum` format) for each of the 6
installers and uploads them with the release. Older releases keep working
through tier 2.

Hard guarantees regardless of tier: downloads stream to a uniquely-named temp
file; failure, cancellation or verification failure deletes it; the launcher
only ever receives a path produced by the verified downloader; nothing
partial or corrupt is ever executed. The temp file is only launched after the
user confirms installation a second time.

### 3. Auto-check default: ON

`UpdateCheckSettings.AutoCheckEnabled` defaults to **true**. Rationale:

- Security and stability fixes reach users without manual polling; this is the
  standard behavior of desktop apps (browsers, VS Code, etc.).
- The cost of "on" is kept negligible by design: checks are throttled (see 4),
  silent when up to date, and network errors never nag.
- Turning it off is one checkbox in 「设置 → 应用更新」 and takes effect
  immediately; manual 「检查更新」 is unaffected by the switch.

### 4. Check interval: 24 hours, persisted

Automatic checks run at most once per 24 hours. `LastCheckUtc` is persisted
in the shared device settings file (same file/pattern as the issue-#11
behavior settings), so restarts do not re-check. The timestamp is recorded
even when a check fails, so a down network cannot cause retry storms. Manual
checks bypass both the switch and the throttle, and report all four states
explicitly （正在检查 / 已是最新 / 发现新版本 / 检查失败）.

### 5. Linux default package: AppImage

Of the four Linux formats, the updater downloads and launches
`Patchouli.NET-{version}-linux-x64.AppImage`. Rationale:

- deb/rpm require root and a distro-specific installer (`apt`/`dnf`) — the
  app cannot complete that flow itself and must not escalate privileges
  silently.
- flatpak requires the flathub remote and the Freedesktop runtime to be
  present, plus `flatpak install` mediation.
- AppImage is a single user-space executable: `chmod +x` and run. It is the
  only format the app can launch directly for "立即安装并退出" without
  privilege escalation or external package managers.

Limitation recorded: the updater launches the *downloaded* AppImage copy; it
never overwrites the running binary in place (the running path is not
reliably known outside the `APPIMAGE` env var, and in-place replacement of a
running executable is unsafe). The dialog tells the user where the file is.

### 6. Platform launch behavior

- **Windows**: start the `.exe` with `UseShellExecute`; Inno Setup elevates
  itself when needed. The `-smoke-setup` diagnostic variant is explicitly
  excluded from asset selection.
- **macOS**: `open` the `.dmg` (mounts it); the user drags the app to
  `/Applications`. This is the standard DMG update step — the client cannot
  install a `.dmg` unattended, and does not pretend to.
- **Linux**: mark the AppImage executable and start it directly.

Patchouli exits **only after** the installer process started successfully; a
failed launch keeps the app running with an error dialog and the retained
download path.

### 7. Version comparison and release filtering

- `UpdateVersion` parses tolerantly (leading `v`, up to 4 numeric parts) and
  compares **numerically** — never lexicographically (`0.3.9 < 0.3.13`).
- Only stable releases are considered (drafts and pre-releases skipped); a
  target version `<=` the running version never prompts (covers "already
  latest", stale ordering and downgrades).
- Asset selection is pure and platform-explicit
  (`UpdateAssetSelector`): unsupported OS/arch combinations report clearly
  instead of downloading the wrong file.

### 8. Concurrency

`UpdateCoordinator` holds a single `SemaphoreSlim(1,1)` across check and
download. A second check/download attempt while one is in flight returns
"already running" instead of starting duplicate work.

## Consequences

- New code: `src/Patchouli.Core/Update/` (version, models, `IUpdateSource`,
  asset selector), `src/Patchouli.Core/Settings/UpdateCheckSettings.cs` +
  `IUpdateCheckSettingsService`, `src/Patchouli.Infrastructure/Update/`
  (`GitHubReleaseSource`, `UpdateCheckSettingsService`, `UpdateDownloader`,
  `UpdateLauncher`, `UpdateCoordinator`), `src/Patchouli.UI/Services/UpdateFlowService.cs`,
  settings section 「应用更新」, two dialogs, a Settings-menu 「检查更新」 entry.
- `HostServices` exposes `UpdateCheckSettings`, `UpdateSource`,
  `UpdateCoordinator` (constructed with `BuildInfo.Version`).
- UI dialog behavior is written but **not verified on a real machine** (no GUI
  in this environment); noted as unvalidated in the handoff.
- `scripts/cleanup-code.ps1` / `scripts/inspect-code.ps1` are Windows-only and
  were not run here; inspection status is unvalidated.

## Alternatives considered

- **Bundling the checksum inside the release notes/body**: fragile to parse,
  and note bodies are human-edited; sidecar assets are machine-readable and
  travel with the file they describe.
- **Blocking updates entirely until checksums exist**: would ship a dead
  feature; the two-tier policy keeps old releases updatable with an honest,
  size-anchored fallback while new releases get real SHA256 verification.
- **Defaulting auto-check to off**: rejected — it would leave most users on
  stale versions with security fixes unapplied; the throttle + silent design
  makes "on" cheap.
