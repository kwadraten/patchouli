# Version OCR Presets For Provenance

Status: accepted

OCR Presets are user-facing configurations, but each OCR Run records an immutable OCR Preset Version. Changing a model path, endpoint, provider binding, or parameters creates a new version so OCR provenance remains stable.

**Consequences**

Rebinding a missing local model or changed provider configuration never mutates the historical configuration used by prior OCR Runs.
