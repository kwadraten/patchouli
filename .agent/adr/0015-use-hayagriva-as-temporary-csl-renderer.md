# Use Hayagriva As Temporary CSL Renderer

Status: accepted

Patchouli v2 will not use `citeproc-js` for CSL rendering, even though it has strong correctness data, because embedding a JavaScript engine would increase package size, add extra memory overhead, and make the desktop/MCP architecture more complex. The current product decision is to use `typst/hayagriva` as the temporary CSL renderer while treating a future F# CSL compiler as the intended long-term implementation.

**Considered Options**

- `citeproc-js`: rejected because it requires embedding or depending on a JavaScript runtime in the product.
- `typst/hayagriva`: accepted as the temporary implementation because it is lightweight and locally integrable enough for v2, despite incomplete CSL test-suite coverage.
- F# CSL compiler: selected as the long-term direction so CSL rendering can live naturally inside Patchouli's .NET/F# architecture.

**Consequences**

The v2 CSL implementation should isolate Hayagriva behind a narrow rendering interface and avoid leaking Hayagriva-specific types into item editing, bibliography export, MCP tools, or persistence. Future migration work should use `jgm/citeproc` and `citeproc-typst` as design precedents for CSL processing behavior and compiler structure.
