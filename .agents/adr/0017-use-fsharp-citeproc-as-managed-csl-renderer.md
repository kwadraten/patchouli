# Use Fsharp.Citeproc As Managed CSL Renderer

Status: accepted

Patchouli uses `Fsharp.Citeproc` 1.1.0 as its CSL 1.0.2 citation and bibliography renderer. CSL-M locale layouts are also supported by the managed processor. The processor runs in-process as a managed `net10.0` dependency behind `ICslRenderer`; item editing, persistence, export, UI, and MCP continue to depend only on Patchouli's CSL contracts.

This decision supersedes the temporary Hayagriva renderer decision. The Rust Hayagriva sidecar, its JSON request file protocol, and platform-specific packaging steps are removed. Rust/Cargo remains a supported repository toolchain because bibliography import will later use `typst/biblatex` for BibLaTeX-to-CSL conversion; that converter is a separate import adapter and must not become the CSL rendering engine.

**Considered Options**

- `Fsharp.Citeproc`: selected because it is a managed, stateless whole-document processor with HTML and plain-text output, CSL fixture coverage, and no JavaScript or external-process runtime dependency.
- `typst/hayagriva`: replaced because the temporary sidecar added Cargo compilation, process execution, temporary request files, and per-platform packaging to the render path.
- `citeproc-js`: not selected because embedding a JavaScript runtime would add another runtime boundary to the desktop and MCP processes.

**Consequences**

Patchouli sends its pure CSL-JSON mapping to the package's JSON API and adapts the returned bibliography entries to the stable `CslRenderResult` contract. Requested locale overrides are applied to the in-memory style document before rendering. Processor diagnostics become `csl_render_failed` results; empty bibliography output remains a failure and must never replace clipboard content.

Bibliographic item languages are normalized at import, API/manual persistence, and CSL mapping boundaries. Recognized names and ISO 639-2 aliases become ISO 639-1 primary subtags; valid BCP 47 region and script subtags remain attached and are case-normalized; empty values stay null and unknown values are preserved. Rendering operates on copies and never replaces an item's source language with its output locale.

Device-local CSL settings persist fallback language mappings, defaulting Japanese and Korean to `zh-CN`. A matching bibliography layout or reachable macro takes precedence over these mappings; the language of the style's unqualified default layout is also retained when it matches the source. Otherwise a configured mapping is tried. Unmapped languages and unavailable mapping targets use the style's default layout, which may be any language. A requested or saved locale controls the root default for unqualified terms and fallback. For an unqualified single bibliography layout, Patchouli creates in-memory locale-specific copies for mapped output locales so entries remain in one whole-list render without splitting sorting, numbering, disambiguation, or repeated-author processing. These rendering copies never change persisted item languages.

The `Fsharp.Citeproc` package and its transitive `FSharp.Core` dependency ship with the application. Packaging no longer builds or copies a CSL sidecar. Future Rust bibliography converters live under `tools/`, use the generic Cargo target ignore rule, and are packaged only after a concrete application integration exists.
