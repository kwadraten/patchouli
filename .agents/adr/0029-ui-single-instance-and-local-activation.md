# UI Single Instance Election and Local Activation Control Plane

Status: accepted (2026-08-29)

## Context

Running multiple concurrent UI instances of the desktop application against the same user environment leads to resource contention, port collisions, and fragmented user state. When a user launches Patchouli while a desktop instance is already running, the secondary launch should bring the existing primary window to the foreground rather than launching a redundant second UI, opening SQLite databases concurrently, running redundant migrations, or conflicting over MCP HTTP server binds.

At the same time, Patchouli must clearly separate process lifecycle/activation from domain data operations and per-Library host authority.

## Decision

Adopt a decoupled three-tier boundary model for instance election, local process control, and domain operations:

1. **Named Mutex for UI instance election**:
   - Fixed mutex name: `net.patchouli.app.ui.single-instance.v1`.
   - Constructed with `NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }` and `initiallyOwned: false`.
   - Uses atomic `createdNew` to identify the single primary desktop UI instance.
   - Global/Local prefixes and Windows-only naming conventions are strictly avoided for cross-platform portability.

2. **Named Pipe for local process lifecycle and activation control**:
   - Fixed pipe name: `net.patchouli.app.ui.control.v1` configured with `PipeOptions.CurrentUserOnly` and `PipeOptions.Asynchronous`.
   - Minimal framing protocol using a 4-byte length prefix with a strict 1 KiB maximum payload.
   - Semantics allow `{ "version": 1, "command": "activate_ui", "request_id": "<guid>" }` and, since 2026-11-06, `open_uri` with an additional `uri` field containing a validated `patchouli://` resource address. Both use matching ACK `{ "version": 1, "request_id": "<guid>", "ok": true }`. Parameterless activation omits `uri` and remains wire-compatible.
   - The control channel is scoped to window activation and UI navigation to existing virtual resources; it never accepts file paths, MCP calls, database operations, arbitrary commands, shutdown, or host takeover. An `activate_ui` message carrying a URI, an `open_uri` message without a valid Patchouli URI, unknown fields and oversized messages are rejected. Up to 32 pre-window links are retained in arrival order. UI navigation waits for Library startup and serializes requests.
   - Secondary processes retry connection for up to 2 seconds. A successful ACK results in a clean exit (code 0). Any connection, handshake, or ACK failure fails closed with exit code 1, guaranteeing that an unprotected second UI is never spawned.

3. **MCP HTTP for domain data plane**:
   - MCP HTTP remains the dedicated data plane for CLI, local agents, and remote agent collaboration as defined in ADR `0024`.
   - `patchouli-cli` continues to interact with Library data via MCP HTTP endpoints.

### Relationship to ADR 0024 and Future Per-Library Host Ownership

This decision governs UI application process single-instance election only. It does **not** replace the ADR `0024` per-Library host takeover design.

- Future Library ownership locking will derive from the canonical database path or persisted `library_id`, distinct from the desktop UI single-instance mutex.
- Host discovery records will continue to publish host type, PID, MCP HTTP endpoint, Library ID, and protocol version.
- Authenticated lifecycle control commands (e.g. headless host takeover and graceful shutdown) remain separate future work and are deliberately not added here.
- gRPC and alternative heavy RPC mechanisms are explicitly deferred; gRPC will only be re-evaluated if the local control plane becomes bidirectional, streaming, high-throughput, or strongly typed beyond simple lifecycle control.

### Unix Named Pipe Caveat

On Unix platforms (Linux and macOS), `PipeOptions.CurrentUserOnly` relies on filesystem permissions and standard Unix domain sockets / FIFOs, which can be affected by process umask configurations. The control pipe accepts only activation and validated Patchouli resource navigation, executes no arbitrary commands and transfers no credentials or host-local file paths; this trade-off is accepted for now.

### Foreground activation preference

`UiPreferences.ActivateWindowOnProtocolOpen` (default `true`, persisted in the `Ui` section) narrows
only the window-activation side effect of an external `open_uri`/startup URI. When `false`,
`MainWindow.OpenProtocolUri` skips `App.ActivateWindow`; navigation and `open_uri` ACK semantics are
unchanged. A minimized or tray-resident window receives non-activating visible feedback: Windows
flashes the taskbar button, and every platform flashes the window title plus the tray tooltip for a
short interval. `activate_ui` still activates the primary window. The CAYW
`DesktopCitationBridge` minimize/restore path is a separate interaction and is not governed by this
preference. ADR `0033`'s UI-thread rule still applies to the feedback.

## Headless lifecycle regression (2026-10-14)

GitHub run [38571691325](https://github.com/kwadraten/patchouli/actions/runs/38571691325)
failed on Windows while the Linux and macOS jobs passed. The exception was in
`HeadlessUnitTestSession.Dispose`, after the window activation assertions. Avalonia
12.0.5 and 12.1.2 publish the session from a worker before its captured Task is necessarily
assigned, so disposal can dereference a null Task. Completing the queue before the worker
exits introduces a separate shutdown race. The repeated immediate-disposal regression
reproduces failures with 12.1.2, including synchronous and asynchronous disposal.

Commit `82b1516` introduced these headless activation tests with Avalonia 12.0.5;
`c802de6` upgraded to 12.1.2, which retained the races. The later Linux-packaging merge
did not alter either the test lifecycle or the Avalonia version. The repair upgrades
the aligned Avalonia packages to 12.1.3, the first patch containing upstream
[fix #22222](https://github.com/AvaloniaUI/Avalonia/pull/22222). The production single-instance
protocol is unchanged. The Windows/Linux/macOS CI matrix now also exercises immediate
session disposal and retains TRX evidence; Unix tests keep their short pipe names to
stay within the Unix domain socket path limits.

## Consequences

- The desktop UI guarantees at most one running instance per operating-system user across terminal sessions (`CurrentUserOnly = true`, `CurrentSessionOnly = false`). Different OS users are independently elected by the user-scoped mutex.
- Secondary invocations reliably signal the primary window to restore/activate and terminate without side effects.
- No heavy RPC frameworks (such as gRPC or protobuf) or additional runtime dependencies are introduced.
