# Use Fsharp.Citeproc As Managed CSL Renderer

Status: accepted

Patchouli uses `Fsharp.Citeproc` 1.0 as its CSL 1.0.2 citation and bibliography renderer. The processor runs in-process as a managed `net10.0` dependency behind `ICslRenderer`; item editing, persistence, export, UI, and MCP continue to depend only on Patchouli's CSL contracts.

This decision supersedes the temporary Hayagriva renderer decision. The Rust Hayagriva sidecar, its JSON request file protocol, and platform-specific packaging steps are removed. Rust/Cargo remains a supported repository toolchain because bibliography import will later use `typst/biblatex` for BibLaTeX-to-CSL conversion; that converter is a separate import adapter and must not become the CSL rendering engine.

**Considered Options**

- `Fsharp.Citeproc`: selected because it is a managed, stateless whole-document processor with HTML and plain-text output, CSL fixture coverage, and no JavaScript or external-process runtime dependency.
- `typst/hayagriva`: replaced because the temporary sidecar added Cargo compilation, process execution, temporary request files, and per-platform packaging to the render path.
- `citeproc-js`: not selected because embedding a JavaScript runtime would add another runtime boundary to the desktop and MCP processes.

**Consequences**

Patchouli sends its pure CSL-JSON mapping to the package's JSON API and adapts the returned bibliography entries to the stable `CslRenderResult` contract. Requested locale overrides are applied to the in-memory style document before rendering. Processor diagnostics become `csl_render_failed` results; empty bibliography output remains a failure and must never replace clipboard content.

The `Fsharp.Citeproc` package and its transitive `FSharp.Core` dependency ship with the application. Packaging no longer builds or copies a CSL sidecar. Future Rust bibliography converters live under `tools/`, use the generic Cargo target ignore rule, and are packaged only after a concrete application integration exists.
