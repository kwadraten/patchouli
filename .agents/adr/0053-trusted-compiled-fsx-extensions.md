# ADR 0053: Trusted compiled FSX extensions

- Status: Accepted for implementation; implementation and acceptance are in progress.
- Date: 2026-10-23

## Context

The current FSX extension execution design uses worker/FSI machinery that the product no longer
intends to retain. Extensions are trusted, user-installed code; process isolation is not a security
boundary requirement. The target should compile once, cache the resulting DLL, and execute it in
the host while preserving host-owned version checks and atomic data commits. The host provides
resources rather than approving extension permissions.

Existing exploratory evidence is in `.tmp/ext-spike/run-1/results/`; those are
preliminary experiments with limited smoke/build scope, not acceptance results. Subsequent
SDK-absence evidence and production-path results are tracked in
[extension-validation](../protocols/extension-validation.md).

## Decision

- Compile FSX packages and cache compiled DLLs for host execution in a per-generation collectible
  `AssemblyLoadContext` (ALC).
- Allow external BCL assemblies, package-local DLLs, and fixed-version NuGet dependencies.
  Cold NuGet restore requires an installed SDK; a runtime-only deployment is insufficient.
  BCL, package-local DLLs and verified compiled cache hits do not require NuGet restore.
- Compile with `--crossoptimize-` so SDK implementation changes are not silently bypassed by
  automatic cross-assembly inlining in cached extensions. Compiler flags and format revision
  participate in toolchain identity; public API identity does not hash implementation assemblies.
- Use standard host-managed HTTP resources. Resolve an unspecified LLM model through the configured
  default model. Resolve credentials as plaintext only when a credential reference is used.
- Remove extension capability declarations and network authorization. Extensions may use their own
  HTTP clients and libraries. Preserve the independent MCP permission model for external agents;
  an in-process extension is trusted code with the host's operating-system privileges.
- Declare configuration using attribute records. Keep editable `Support.fsx`, but separate pure
  function layers from effectful host integration.
- Preserve generation leases, draining, and explicit last-known-good (LKG) behavior. ALC unload is
  best-effort: emit a warning if collection cannot be confirmed, but do not block activation or
   require collection before switching. Recommend restarting the application when old code or
   resources cannot be released; do not build an automatic recovery subsystem.
- Remove the old FSI, worker, RPC, and authorization plumbing rather than maintaining parallel
  execution paths. Do not require legacy v2 capability claims or blacklists. Preserve metadata
  schema validation and compiler-warning enforcement.

## Consequences

Trusted extension code runs in-process and can affect the host; this is explicitly not a sandbox.
ALC unloading may be delayed by retained references or runtime behavior. Compilation, metadata
validation, and host contract checks remain required before activation. F# compiler diagnostics,
SDK tests, and compiled-extension integration are correctness checks; high-count endurance and
performance loops use a separate explicit runner entry point rather than normal Full acceptance.

## Verification state

Implementation and acceptance are ongoing. Do not describe this ADR as proven or the new runtime as
fully validated until the relevant compiler, SDK, integration, and lifecycle checks have run. The
pre-existing `.tmp/ext-spike/run-1/results/` experiments are exploratory evidence only
and must be cited with their limited smoke/build scope when used; they do not establish NuGet SDK
support or production unload behavior.
