# Keep Original Files Outside The Database

Status: accepted

Original PDFs, images, and large binary caches stay outside the database. The app stores FileAsset identity, fingerprints, known locations, OCR/layout/search artifacts, and evidence metadata, then relocates source files through hash checks and search roots.

**Consequences**

Moving or losing source files must not delete bibliographic metadata, OCR/layout history, search units, or evidence refs.

**Standing Constraints**

- Original PDFs/images are resolved through FileAsset identity, fingerprints, known locations, and FileSearchRoots.
- The database may store cache metadata, but not original source files or large render/cache payloads.
- `page_renders` is a local cache namespace only and is excluded from published snapshots.
- MCP never returns cached images or image paths.
