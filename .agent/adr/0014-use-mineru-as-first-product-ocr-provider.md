# Use MinerU As First Product OCR Provider

Status: accepted

The first product OCR/layout path uses MinerU precise parsing. Tesseract remains a temporary alpha/developer surface unless explicitly retained for diagnostics. MinerU matches the product need for OCR plus structured layout import into LayoutRevisions and SearchUnits.

**Consequences**

The product UI and first-run flow should stop presenting Tesseract as the primary OCR path once the MinerU workflow lands.

**v2 Update**

The v2 PRD keeps MinerU as the preferred OCR provider and treats MinerU-style output as the compatibility baseline for OCR storage and editing:

- Mock OCR, local placeholder OCR, and Tesseract CLI are not production OCR providers and should not appear in final-user UI.
- MinerU content-list-style output remains the schema basis for OCR text storage, layout editing, table cells, bbox, SearchUnits, and evidence.
- Additional providers, including multimodal LLM OCR, must normalize their output into a MinerU-compatible intermediate shape before entering LayoutRevisions, LayoutNodes, SearchUnits, or MCP-visible evidence.
- Provider-specific raw responses may be retained for diagnostics/provenance, but they are not the direct editing/search/MCP data model.
- Patchouli is responsible for storing and using user-provided OCR provider tokens/secrets, endpoints, model ids, and parameters.
- Patchouli is not responsible for provider account registration, quota purchase, balance checks, cost estimation, or billing policy.
- Provider secrets continue to follow the ProviderCredential boundary: never in MCP, never in logs, never in immutable historical shards.
