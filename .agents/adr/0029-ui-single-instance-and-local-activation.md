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
   - Semantics are restricted exclusively to `{ "version": 1, "command": "activate_ui", "request_id": "<guid>" }` and matching ACK `{ "version": 1, "request_id": "<guid>", "ok": true }`.
   - The control channel is strictly scoped to window activation; it never accepts file paths, MCP calls, database operations, arbitrary commands, shutdown, or host takeover.
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

On Unix platforms (Linux and macOS), `PipeOptions.CurrentUserOnly` relies on filesystem permissions and standard Unix domain sockets / FIFOs, which can be affected by process umask configurations. Because the control pipe only accepts parameterless `activate_ui` requests, executes no arbitrary commands, and transfers no sensitive arguments or tokens, this trade-off is accepted for now.

## Consequences

- The desktop UI guarantees at most one running instance per operating-system user across terminal sessions (`CurrentUserOnly = true`, `CurrentSessionOnly = false`). Different OS users are independently elected by the user-scoped mutex.
- Secondary invocations reliably signal the primary window to restore/activate and terminate without side effects.
- No heavy RPC frameworks (such as gRPC or protobuf) or additional runtime dependencies are introduced.
