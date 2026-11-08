# ADR 0055: Versioned built-ins with read-only source templates

- Status: Accepted by explicit user direction. The read-only/template UI, compatible /4 protocol, SemVer upgrade and NuGet/SDK preflight are implemented in the working tree. Recorded verification and acceptance limits are maintained in extension-validation.md.
- Date: 2026-11-05
- Product scope: [issue #8 — 插件化架构改造](https://github.com/kwadraten/patchouli/issues/8).

## Context

The installed OCR failure on 2026-11-05 happened before extension upgrade: the old package
factory included deployment-dependent binaries in package identity. Its framework classification
changed in a self-contained application, so the runtime requested an archive absent from the
bundle. The source-only/shared-library working changes address that immediate mismatch, but
editable built-ins still require source synchronization, historical fingerprint lists and
special handling of user modifications. Versioned compiled distribution with read-only source
templates removes that responsibility while preserving local examples and the existing editor.

Issue #8 retains Core commit authority, trusted in-process extensions, generation leases and
explicit LKG. Its historical editable-built-in and source-preservation decisions are replaced
below; this ADR does not define Plugin VFS, WebView containers (see
[ADR 0056](0056-plugin-webview-containers.md)), full retrieval backends or the separately implemented community store.
This ADR partially supersedes ADRs 0052 and 0053 on distribution and upgrade, without changing
task execution interfaces or the host's domain validation/transaction authority.

## Decision

### Compatible protocol revision

The new declaration protocol is `patchouli.extensions/4`. A new host accepts both `/3` and `/4`
through the same execution interfaces; accepting `/3` must be an explicit compatibility rule
used by declaration parsing, compilation, artifact validation, restoration and loading, rather
than equality with `Current`. Preserve the actual declared protocol in derived metadata.
An old `/3` host need not understand `/4`. Compatibility does not make an obsolete compiled
artifact ABI-compatible: source packages may need recompilation after a toolchain change.
Do not restore the earlier rejected YAML package format or remove validations unrelated to this change.

The existing XML documentation immediately before top-level `let extension` remains the one
authored identity declaration. `/4` requires a valid `version` under
[Semantic Versioning 2.0.0](https://semver.org/), for example:

```fsharp
/// <patchouli-extension id="ocr.ndlocr-lite" kind="ocr" version="1.1.0" host-api="patchouli.extensions/4">
/// <name>NDLOCR-Lite</name>
/// </patchouli-extension>
let extension = // existing SDK factory and task contract
```

Package version, declaration protocol and compiler/ABI identity are separate values. Compare
major/minor/patch numerically, follow SemVer prerelease ordering and ignore build metadata for
precedence. Reject invalid `/4` versions; do not retroactively tighten `/3` declaration validation.
Equal versions/equal artifact digests are idempotent. Equal SemVer precedence with different
published package content is a conflict, including differences only in build metadata; publish
a higher version. Toolchain rebuilds of unchanged sources are separately identified artifacts,
not new authored package content. Released source/declared dependencies must not change at the
same package version. New built-ins must receive versions higher than the outgoing packages;
the current `1.0.0` built-ins can migrate to `1.1.0` where their public contract remains compatible.

### Built-ins keep read-only sources, user extensions have independent IDs

Keep built-in FSX and helper sources in Git and in the installed application for development,
compilation and local examples. Installed applications ship compiled extensions, their actual
required non-shared dependencies, source templates and build-generated identity/configuration
metadata. This transition replaces the initial binary-only proposal: built-in source remains
viewable but cannot be edited, saved or applied through the editor. Do not create editable
built-in working copies or launch FCS/NuGet merely to view their sources or configuration.
Derive binary entrypoint identity from the source declaration at build time and validate its
association with the exported factory and configuration schema; do not add a second authored manifest.
The shared SDK/ONNX/Skia/OCR support assemblies continue to resolve from the host.

Built-in IDs are reserved to the application registry. Users create extensions with new IDs;
configuration remains editable for both origins, while source editing applies to user packages.
Each translation, metadata, OCR and PDF text-layer list has an icon-only Add button in its
upper-right toolbar. It opens a dialog selecting an existing extension of the same kind and a
new ID. Copy the complete source/helper/binary payload into an independent draft, rewrite only
the structured entry ID and open its editor. Creating a draft does not compile, activate, copy
credentials/configuration or make network requests. Users check/apply it explicitly. Existing
toolbar actions such as refresh and restore-defaults also use icons, tooltips and accessible names.
The host deals with declared kind, implementation interfaces, configuration, dependencies and
candidate results; it does not branch on algorithms, models or internal library choices.

### Version-based replacement

For a trusted bundled candidate with the same reserved ID and a higher SemVer version, validate
the candidate and atomically replace the old selection. This applies to old `/3` built-in
generations and working copies, including locally edited copies under that built-in ID. Do not
require a historical fingerprint allowlist, pristine-source comparison or user confirmation.
Before first migration, retain old local source as a non-active recovery copy, outside source
watching; do not delete or rewrite immutable historical snapshots. This is migration preservation,
not continued support for overriding built-ins. New custom work uses a new ID.

When a managed historical built-in has no comparable version, establish the migration baseline
only from its known built-in registration and legacy protocol/state. Do not treat arbitrary
third-party packages or malformed `/4` versions as version zero. Equal versions do not upgrade;
lower versions do not automatically replace a higher installed version. Application downgrade
must report an incompatible retained version explicitly if it cannot load it; never silently
select incompatible code. Explicit recovery remains distinct from automatic upgrade.

Preserve configuration by stable keys and report incompatible values. Validate DLL/dependencies,
supported protocol/ABI, factory kind and Settings schema before publishing Active. Failed candidates
leave Active/LKG and configuration intact. Already accepted tasks keep their original lease;
new work uses the replacement. Do not overwrite files used by a live generation, promote LKG
implicitly, or undo committed Library data. Content digests remain integrity/cache identities,
not upgrade precedence or a way to infer whether a user modified a built-in.

### NuGet requirement and SDK guidance

For source packages, scan real `#r "nuget: ..."` directives in `main.fsx` and package-local
`#load` inputs without executing code. Respect F# comments, string literals and compilation
conditions; text inside a comment/string is not a directive. Preserve fixed-version dependency
validation and source locations. This scan determines an environment prerequisite, not whether
the host approves the extension's implementation or network access.

When checking/importing/applying a source extension that uses NuGet, probe a usable .NET SDK
with the selected dotnet host (`dotnet --list-sdks`, architecture/target compatibility and the
existing discovery rules). A runtime, an executable named dotnet, or a warm NuGet cache alone
is insufficient. Distinguish missing dotnet, runtime-only and failed/unsupported probes. Show
an actionable message such as “此扩展通过 NuGet 引用依赖；本机没有可用的 .NET SDK，请安装与当前宿主兼容的 .NET SDK 后重试。”
Link to the [official .NET SDK download page](https://dotnet.microsoft.com/en-us/download)
and allow retry after installation; do not install it automatically.

Block only work that actually needs source compilation/NuGet restore. An already verified
binary/cache hit remains usable without an SDK; a source-management view may show the SDK
requirement as non-blocking feedback. Built-in binary activation and ordinary configuration
opens must not acquire a new SDK prerequisite or repeatedly probe/start the compiler. BCL and
package-local DLL source paths retain their existing SDK-free compilation behavior.

### Repository examples and future community distribution

The distributed Skill instructs Agents to read the existing installed built-in FSX and helper
sources as examples, then implement an independent extension under a new ID. Do not change a
built-in source or recovery snapshot. Local templates are available offline and match the
installed SDK; a Git revision link can supplement them but is not required to create a copy.
Source filesystem visibility is not a security boundary against trusted same-user code.

Stable ID, SemVer version, origin, declared protocol, kind, artifact identity and source revision
form the foundation for a separate community repository and built-in store. Origin is established
by installation/registry provenance, not a self-declared field or ID. Community packages cannot
claim reserved built-in IDs. Store discovery, download/update UI, repository hosting, publisher
verification and release-channel policy are later work; this change does not implement them.

## Implementation sequence and gates

1. **Documentation (this change):** record supersession in ADRs 0052/0053, add the `/4` target
   contract and link it from domain/validation docs. Keep issue #8 as the product tracker and
   distinguish planned contracts from delivered behavior. No remote issue edit is implied.
2. **Identity and compatibility:** add strict SemVer parsing/comparison for `/4`, a shared `/3`
   and `/4` compatibility predicate, and build-derived binary entrypoint metadata. Audit
   `ExtensionHostApi`, `ExtensionEntryMetadata`, `ExtensionCompiler`, `ExtensionArtifact`,
   runtime state persistence and toolchain identity. Test all readers, not only entry parsing.
3. **Binary delivery:** change `scripts/Patchouli.ExtensionSchemas` and Infrastructure resources
   to emit/load compiled definitions and configuration schemas alongside read-only source templates.
   Use generic kind/factory validation and the existing lazy activation boundaries. Validate
   both framework-dependent builds and actual self-contained publish outputs.
4. **Upgrade and migration:** update catalog/store/restore paths to select higher SemVer
   candidates, reserve built-in IDs and preserve configuration/old leases. Migrate legacy local
   files once; remove historical fingerprint upgrade machinery after replacement tests pass.
   Keep custom `/3` source packages functional and explicit LKG recovery available.
5. **Source preflight and UI:** strengthen directive scanning and reuse
   `ExtensionDependencies`/`NugetResolverPreflight` diagnostics. Show installation guidance and
   retry for missing SDK; keep binary/cache/configuration paths independent. Make built-in
   source editors read-only and hide save/apply actions while retaining settings. Add the shared
   template/new-ID dialog to every extension list and iconize its existing toolbar actions.
6. **Skill and acceptance:** keep local bundled FSX as the primary authoring reference, require
   independent user IDs, document both accepted protocol revisions, then record actual results in
   `extension-validation.md`. Do not implement the community store in this batch.

Required regression coverage:

- `/3` and `/4` source/compiled activation, restart/restore and LKG; unknown protocol rejection;
  old binaries invalidated by genuine ABI mismatch can recompile without rejecting valid `/3` source.
- SemVer strictness, numeric ordering (`1.9.0 < 1.10.0`), prerelease ordering and equal-precedence
  build-metadata conflicts; no accidental lexical comparison or automatic downgrade.
- New binary built-in replaces older `/3` built-in even after local source modification;
  independent user IDs are untouched; same-version identity is idempotent; failed staging
  preserves Active/config/LKG; old accepted OCR/translation jobs retain their leases.
- Published application retains read-only built-in FSX/helper templates; all declared built-ins have
  matching artifacts, schemas and source references; cold activation works without SDK/FCS
  in both framework-dependent and self-contained deployment layouts.
- NuGet directives in entry/helper files, comments/strings/conditional compilation, absent dotnet,
  runtime-only, incompatible SDK, failed probe, compatible SDK and retry after installation;
  verified cache hits and built-ins stay usable without SDK, including a warm NuGet-cache case.
- Every extension list opens the same kind-filtered Add dialog; duplicate/reserved IDs cannot
  overwrite packages; copying preserves helper/binary inputs and changes only entry identity.
  Built-in editor/save/apply paths are read-only; custom source remains editable, and toolbar
  icons retain tooltips/accessibility names. Creation alone never compiles or activates.
- Configuration/editor/Skill behavior matches package origin; domain result validation and
  transaction/credential/MCP boundaries remain unchanged.

Run repository cleanup on changed C#/AXAML before tests. Public F# SDK changes require normal
F# build, C# consumer rebuild, interop and SDK tests, and JetBrains inspection as specified in
AGENTS.md. Use recorded `scripts/run-tests.ps1` runs and Full acceptance after integration;
packaging verification must exercise actual publish outputs. No new-protocol acceptance is
claimed by the earlier 40-test bundle regression run.
