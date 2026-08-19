# Use PDFiumCore For Page Rendering

Status: accepted

Patchouli uses PDFiumCore as the only native PDF page-rendering backend. The application consumes PDFiumCore's RID-specific native assets and exposes page count, PNG rendering, in-memory preview pixels, cancellation, and structured failures through the shared page-rendering service.

MuPDF native assets, adapters, and packaging paths are not retained as a fallback. The renderer basis includes the PDFium version so that stale render caches are invalidated when the backend changes.

**Consequences**

- macOS arm64 release artifacts must contain the matching Mach-O `libpdfium.dylib` and must not include MuPDF binaries.
- OCR page-range splitting and the PDF workspace use the shared PDFium-backed service; they do not call a native renderer directly.
- Page renders remain local rebuildable cache data and never enter snapshots or MCP responses.
- Cross-platform unit tests cover the service contract. Signing, notarization, and release-artifact verification remain macOS release-runner responsibilities.
