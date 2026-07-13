# Use MinerU As First Product OCR Provider

Status: accepted

The first product OCR/layout path uses MinerU precise parsing. MinerU matches the product need for OCR plus structured import into page-local Document Box Trees and SearchUnits.

**Consequences**

The product UI and first-run flow should stop presenting any retired local CLI OCR path once the MinerU workflow lands.

**v2 Update**

The 0.2.0 model keeps MinerU as the preferred OCR provider while explicitly treating MinerU JSON as a short-lived import format:

- Mock OCR and local placeholder OCR are not production OCR providers and should not appear in final-user UI.
- MinerU content-list output is mapped to `OcrDocumentTreeCandidate`, then validated and staged through the shared Document Tree importer.
- Additional providers, including multimodal LLM OCR, must normalize output into the same candidate contract before entering DocumentTreeRevision, DocumentBox, SearchUnit, evidence, or MCP surfaces.
- Regular tables become one GFM table leaf; irregular tables become `[Table]` plus a diagnostic. Persistent table-cell rows are forbidden.
- Auxiliary and discarded blocks are preserved as typed Boxes with `suppressed=true`; phonetic annotations are flattened with a diagnostic.
- `full.md` is never accepted as a pseudo Box when no verifiable tree artifact exists.
- Complete provider responses and MinerU intermediate JSON are not retained as canonical database or snapshot data.
- Patchouli is responsible for storing and using user-provided OCR provider tokens/secrets, endpoints, model ids, and parameters.
- Patchouli is not responsible for provider account registration, quota purchase, balance checks, cost estimation, or billing policy.
- Provider secrets continue to follow the ProviderCredential boundary: never in MCP, never in logs, never in immutable historical shards.
