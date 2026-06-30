# Keep Original Files Outside The Database

Status: accepted

Original PDFs, images, and large binary caches stay outside the database. The app stores FileAsset identity, fingerprints, known locations, OCR/layout/search artifacts, and evidence metadata, then relocates source files through hash checks and search roots.

**Consequences**

Moving or losing source files must not delete bibliographic metadata, OCR/layout history, search units, or evidence refs.
