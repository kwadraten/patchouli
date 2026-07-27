# BibLaTeX Import/Export Via typst/biblatex Helper

Status: accepted

Patchouli parses and writes BibLaTeX/BibTeX through a locked Rust helper under `tools/biblatex-helper` that depends on [`typst/biblatex` 0.12.0](https://github.com/typst/biblatex). The helper exchanges stable UTF-8 JSON DTOs over stdin/stdout. C# owns item mapping, candidate matching, conflict descriptors, UI, file selection, and persistence. CSL formatting remains `Fsharp.Citeproc` and is not replaced.

**Upstream mapping sources**

- Entry-type tables are transplanted from Citation.js `@citation-js/plugin-bibtex` `src/mapping/biblatexTypes.json` (MIT). Locked upstream commit at implementation time: Citation.js monorepo `main` snapshot used for the reviewed table copy checked into `BiblatexEntryTypeMap`.
- Field mapping for model-expressible fields follows the same plugin's `src/mapping/biblatex.js` rules. Citation.js is a mapping source only and is not a runtime dependency.

**Consequences**

- Import uses `Bibliography::parse` plus per-entry `Entry::verify()`. Export constructs `biblatex::Entry` and calls `Entry::to_biblatex_string`.
- `@xdata` entries are inheritance containers only. Expanded fields are imported; `crossref`/`xdata` relationships are not persisted.
- Patchouli identity remains `ItemId`; BibLaTeX entry keys are preview/export labels only.
- Conflict codes `CF-07` and `CF-08` live in domain `bibliography_import`.
