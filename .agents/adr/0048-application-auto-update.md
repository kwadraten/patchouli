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
- Release assets include a `<asset>.sha256` sidecar for each installer. The
  client must distinguish a confirmed missing sidecar from an unavailable or
  invalid sidecar and must never execute an asset whose integrity is unknown.
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

1. The GitHub release asset list marks checksum presence as `Present`,
   `Absent`, or `Unknown`. A `Present` sidecar is fetched and the downloaded
   file's SHA256 must match its 64-digit digest. HTTP errors, timeouts, invalid
   content and mismatches fail the download and delete the temporary file.
2. Only a release asset list that confirms `Absent` permits the legacy fallback:
   the published asset size must be positive and the downloaded byte count must
   match it exactly. Unknown status and missing/invalid size fail closed. The
   UI says that size was checked when a legacy release has no checksum.

`.github/workflows/release.yml` generates a `<asset>.sha256` sidecar
(`sha256sum` format) for each installer and uploads it with the release. Older
releases use tier 2 only when the release asset list confirms no sidecar.

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
- Turning it off is one checkbox in 「设置 → 更新设置」 and takes effect
  immediately; manual 「检查更新」 is unaffected by the switch.

### 4. Check interval: 24 hours, persisted

Automatic checks run at most once per 24 hours. `LastCheckUtc` is persisted
in the shared device settings file (same file/pattern as the issue-#11
behavior settings), so restarts do not re-check. The timestamp is recorded
even when a check fails, so a down network cannot cause retry storms. Manual
checks bypass both the switch and the throttle, and report all four states
explicitly （正在检查 / 已是最新 / 发现新版本 / 检查失败）.

### 5. Installation formats that require manual updates

Only a confirmed Windows Inno Setup installation may use the built-in download
and installer flow. The running install path must match the Inno uninstall
record, and recognized package-manager paths are excluded. A missing or
unreadable record, an unknown Windows layout, and every non-Windows format fall
back to a GitHub Release page link.

AppImage is a container-style single-file installation; deb/rpm and Flatpak are
owned by system or external package managers; macOS `.dmg` requires the user to
replace the app in Applications. Patchouli does not chmod/run an AppImage or
mount/open a DMG as an update. When a newer release exists for a manual-update
installation, the version dialog gives the user its Release page and does not
download an asset or start an installer.

### 6. Windows launch behavior

- **Windows**: start a verified `.exe` with `UseShellExecute`; Inno Setup
  elevates itself when needed. The `-smoke-setup` diagnostic variant is
  explicitly excluded from asset selection.

After the user confirms update installation, the normal window close path first
resolves MCP drafts and unsaved settings. Only then does it start the installer.
A failed launch leaves the window open and the verified download retained; a
successful launch completes the already-authorized normal close path, whose
`OnClosed` handler performs asynchronous service shutdown.

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
  settings section 「更新设置」, two dialogs, a Settings-menu 「检查更新」 entry.
- `HostServices` exposes `UpdateCheckSettings`, `UpdateSource`,
  `UpdateCoordinator` (constructed with `BuildInfo.Version`).
- UI dialog and close behavior are covered by headless tests; update installation
  has not been exercised by running a real installer.
- `scripts/cleanup-code.ps1` ran with JetBrains Command Line Tools 2026.1.4.
  The final targeted update/UI regression filter passed 219/219 tests. The final
  JetBrains InspectCode 2026.1.4 run passed with no blocking issues; its SARIF
  report is `.tmp/issue-15-fixes-20261020/inspectcode-final.sarif`.

## Alternatives considered

- **Bundling the checksum inside the release notes/body**: fragile to parse,
  and note bodies are human-edited; sidecar assets are machine-readable and
  travel with the file they describe.
- **Blocking updates entirely until checksums exist**: would ship a dead
  feature; a release listing that explicitly confirms a missing sidecar can
  still support a verified-size download, with the UI stating that SHA256 was
  unavailable. Unknown checksum state or missing/invalid size fails closed.
- **Defaulting auto-check to off**: rejected — it would leave most users on
  stale versions with security fixes unapplied; the throttle + silent design
  makes "on" cheap.
