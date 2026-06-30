# Separate Item, FileAsset, And DocumentInstance

Status: accepted

The bibliographic model has three distinct layers: Item is citation identity, FileAsset is source file identity, and DocumentInstance is a concrete manifestation that owns pages, OCR/layout/search artifacts, and evidence refs. This supports alternate scans, OCR PDFs, supplements, missing pages, and moved files without collapsing them into one attachment concept.

**Consequences**

Default search can target a primary DocumentInstance while advanced workflows can include alternates or partials.
