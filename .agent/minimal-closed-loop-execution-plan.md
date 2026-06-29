# Minimal Closed Loop Execution Plan

## Issue Draft

Title: Build first-run PDF import, MinerU OCR, search indexing, and MCP read loop

Goal: replace the current low-level tab-heavy alpha workflow with a Zotero-like desktop shell and a modal first-run/import wizard that gets one selected PDF from disk into searchable MCP-visible text.

User loop:

1. User opens the Avalonia UI.
2. A modal wizard guides the user to create/open a runtime SQLite database and create a library identity.
3. User selects a PDF scan root.
4. The app scans that root and lists discovered PDFs.
5. User selects one PDF and enters minimal bibliography metadata.
6. The app creates `file_asset`, `item`, `document_instance`, and `pages`.
7. The app sends the PDF to MinerU for OCR/layout extraction.
8. The app imports MinerU output into one current document-level layout revision.
9. The app rebuilds `search_units` and FTS.
10. MCP can read the indexed content through `search_library` and page text/block tools.

Explicit test scope: unit tests only for this issue. Do not add end-to-end tests yet. End-to-end workflow tests should be a later issue after the UI and MinerU integration surfaces settle.

## Product Constraints

- The main UI should feel like Zotero or similar literature/file management software, not like an internal diagnostic dashboard.
- First-run setup must be a popup/modal wizard, not another tab.
- Keep developer/debug surfaces available only if they do not dominate the first experience.
- Tesseract is not a product OCR path. Remove it from the user-facing UI and from the first-run flow.
- MinerU is the primary OCR/layout provider.
- MCP remains read-only and text-only. It must not expose local paths, cache paths, upload URLs, provider tokens, signed URLs, or provider configuration.
- All agent-readable planning and domain docs stay under `.agent/`; do not create `docs/`.

## MinerU API Notes

Use the official MinerU precise parsing API as the first production provider.

Official docs: https://mineru.net/apiManage/docs

Relevant API facts:

- Precise API requires `Authorization: Bearer <token>`.
- For local files, request upload URLs with `POST https://mineru.net/api/v4/file-urls/batch`.
- Upload each local file with `PUT` to the returned signed URL. Do not set `Content-Type` on upload.
- Poll results with `GET https://mineru.net/api/v4/extract-results/batch/{batch_id}`.
- Result states include `waiting-file`, `pending`, `running`, `converting`, `done`, and `failed`.
- Successful results include `full_zip_url`.
- Result zip includes `full.md`, `*_content_list.json`, `layout.json`/`middle.json`, and `*_model.json`/`model.json` style outputs.
- Precise API supports PDF and common image/Office formats, up to 200 MB and 200 pages.
- Prefer `model_version = "vlm"` for the first product path unless there is a local reason to use `pipeline`.
- Request `is_ocr = true`, `enable_table = true`, `enable_formula = true` by default.

Do not use the Agent lightweight API for the product path; it only returns Markdown and has smaller limits. It may be considered later for diagnostics or demos.

## Target Architecture

Create two separate layers:

1. MinerU transport/client layer
   - Knows HTTP, token auth, upload URLs, polling, zip downloads, provider errors.
   - Does not know app domain tables beyond request identifiers.

2. MinerU import/workflow layer
   - Converts downloaded MinerU result files into app layout revisions and layout nodes.
   - Rebuilds search units and FTS after successful import.
   - Does not perform HTTP calls directly.

This split is mandatory so unit tests can cover parsing/import without real MinerU network calls.

## File-Level Plan

### 1. Solution and package configuration

Modify:

- `LiteratureApp.sln`
- `Directory.Packages.props`

Work:

- Add any package versions needed for HTTP resilience and zip/JSON parsing only if the BCL is insufficient.
- Prefer built-in `HttpClient`, `System.Text.Json`, and `System.IO.Compression` first.
- If adding a new project, include it in the solution.

Default decision:

- Do not create a new project for the first pass.
- Put orchestration under `src/LiteratureApp.Infrastructure/Workflows/` because the workflow needs concrete DB, file, OCR, and search services already owned by Infrastructure.

### 2. Core workflow contracts

Add:

- `src/LiteratureApp.Core/Import/PdfDiscoveryModels.cs`
- `src/LiteratureApp.Core/Import/FirstRunWorkflowModels.cs`

Contents:

- `PdfCandidate`
  - `string Path`
  - `string FileName`
  - `long SizeBytes`
  - `DateTimeOffset? ModifiedAt`
  - `int? PageCount`
  - `string Status`
- `FirstRunStep`
  - string constants for `database`, `library`, `scan`, `import`, `mineru_config`, `extract`, `index`, `mcp_verify`, `complete`
- `FirstRunWorkflowState`
  - current step, progress text, selected PDF, created IDs, last error
- request/response records for:
  - scanning PDF roots
  - importing one PDF
  - running MinerU extraction
  - verifying MCP search

Keep these as data contracts only. Do not put filesystem or HTTP logic in Core.

### 3. MinerU provider contracts

Add:

- `src/LiteratureApp.Ocr/MinerU/MinerUOptions.cs`
- `src/LiteratureApp.Ocr/MinerU/MinerUContracts.cs`
- `src/LiteratureApp.Ocr/MinerU/IMinerUClient.cs`
- `src/LiteratureApp.Ocr/MinerU/IMinerUResultImporter.cs`

Contents:

- `MinerUOptions`
  - `BaseUrl = "https://mineru.net"`
  - `Token`
  - `ModelVersion = "vlm"`
  - `Language = "ch"`
  - `IsOcr = true`
  - `EnableTable = true`
  - `EnableFormula = true`
  - polling timeout/interval settings
- upload request/result records:
  - `MinerUUploadRequest`
  - `MinerUUploadBatch`
  - `MinerUExtractResult`
  - `MinerUPollResult`
  - `MinerUDownloadedResult`
- importer request/result records:
  - `MinerUImportRequest`
  - `MinerUImportResult`

Do not store token in any record that can be returned through MCP or serialized into user-visible logs.

### 4. MinerU HTTP client

Add:

- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUClient.cs`
- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUApiDtos.cs`
- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUResultDownloader.cs`

Responsibilities:

- Request signed upload URLs from `/api/v4/file-urls/batch`.
- Upload local PDF bytes to signed URLs.
- Poll `/api/v4/extract-results/batch/{batch_id}`.
- Download `full_zip_url` to a local cache directory.
- Sanitize errors before returning them to UI.

Important:

- Signed upload URLs and `full_zip_url` are sensitive transient provider artifacts. They may be used internally but must not be persisted into domain tables or emitted through MCP.
- Store downloaded zips under the app cache root, not next to user PDFs.
- Return structured provider status:
  - `not_configured`
  - `upload_url_failed`
  - `upload_failed`
  - `waiting_file`
  - `pending`
  - `running`
  - `converting`
  - `done`
  - `failed`
  - `timeout`

Unit tests:

- Use a fake `HttpMessageHandler`.
- Verify Authorization header format.
- Verify signed upload uses PUT and does not set `Content-Type`.
- Verify polling maps states correctly.
- Verify provider URLs/tokens are not included in failure messages returned by public methods.

### 5. MinerU result importer

Add:

- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUResultImporter.cs`
- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUZipReader.cs`
- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUContentListParser.cs`
- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerULayoutNodeMapper.cs`

Responsibilities:

- Read the result zip.
- Prefer structured JSON over Markdown when available.
- Parse `*_content_list.json` as the first importer target.
- Use `full.md` as fallback text when structured blocks are insufficient.
- Create exactly one `layout_revision` for the whole document.
- Insert `layout_nodes` for all pages into that one revision.
- Set that revision current only after all importable nodes are inserted.
- Mark/search rebuild should happen after the revision transaction commits.

Mapping rules for MVP:

- Text blocks -> `LayoutNodeType.Paragraph`, `TextPolicy.Own`.
- Titles/headings -> use available heading type if current model has one; otherwise paragraph.
- Tables -> `LayoutNodeType.Table`, text as Markdown/HTML degraded string, `TextPolicy.Own`.
- Images without text -> skip for default search, but leave a TODO for later figure nodes.
- Formula blocks -> paragraph with LaTeX text if available.
- Missing bbox -> use a conservative normalized full-width block for MVP only if MinerU gives page-level ordering; otherwise skip block and record warning.
- Page numbers are zero-based internally; convert MinerU one-based page indexes carefully.

Unit tests:

- Parser handles fixture content-list JSON.
- Mapper creates stable page/node ordering.
- Importer creates one current revision for all pages.
- Importer does not create one current revision per page.
- Importer handles missing optional files in the zip.
- Importer marks warnings without failing the whole import when non-critical blocks cannot be mapped.

Fixtures:

- Put test fixture zips/json under `tests/LiteratureApp.Tests/Fixtures/MinerU/`.
- Keep fixtures small and synthetic.
- Do not include real copyrighted PDFs.

### 6. Remove Tesseract from product surface

Modify:

- `src/LiteratureApp.UI/MainWindow.axaml`
- `src/LiteratureApp.UI/ViewModels.cs`
- `src/LiteratureApp.UI/PdfRenderViewModel.cs`
- `src/LiteratureApp.UI/AppServices.cs`
- `src/LiteratureApp.Ocr/TesseractCliAdapter.cs`
- `tests/LiteratureApp.Tests/LocalImageOcrAdapterTests.cs`
- `tests/LiteratureApp.Tests/OcrAdapterReadinessTests.cs`

Work:

- Remove Tesseract controls from UI.
- Remove "Run Local Image OCR" from user-facing workflow.
- Remove Tesseract registration from `AppServices` product startup.
- Keep `TesseractCliAdapter.cs` only if tests still need it temporarily; otherwise delete it and update project references.
- Rename mock OCR UI language to "Developer Mock OCR" if retained.
- Update tests to target MinerU contracts/fakes instead of Tesseract readiness.

Acceptance:

- No user-facing UI copy says Tesseract.
- `AppServices` product composition registers MinerU and Developer Mock only.
- Tesseract is not part of the first-run loop.

### 7. PDF discovery and import workflow

Add:

- `src/LiteratureApp.Infrastructure/Workflows/PdfDiscoveryService.cs`
- `src/LiteratureApp.Infrastructure/Workflows/PdfImportWorkflow.cs`
- `src/LiteratureApp.Infrastructure/Workflows/FirstRunWorkflow.cs`

Modify if needed:

- `src/LiteratureApp.Infrastructure/Files/FileAssetService.cs`
- `src/LiteratureApp.Infrastructure/Documents/DocumentInstanceService.cs`
- `src/LiteratureApp.Infrastructure/Layout/PageService.cs`
- `src/LiteratureApp.Infrastructure/Bibliography/ItemService.cs`

Responsibilities:

- Scan a selected root recursively for `.pdf`.
- Return candidates without registering everything automatically.
- Import exactly one selected PDF for MVP.
- Register file asset.
- Create minimal item.
- Attach document instance.
- Determine page count.
- Create page rows.

Page count options:

- Prefer a small `IPdfMetadataReader` abstraction.
- Implement `PdfMetadataReader` using `pdfinfo` only if available, or a small managed parser if already feasible.
- If page count cannot be determined, fail import with a user-actionable message. Do not silently create one page for an unknown PDF.

Unit tests:

- Discovery filters only PDFs and ignores `bin/obj`.
- Import creates the expected item/file/document/page rows.
- Import fails cleanly when page count is unavailable.
- Import does not copy or move the original PDF.

### 8. Search indexing after MinerU import

Modify:

- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUResultImporter.cs`
- `src/LiteratureApp.Infrastructure/Search/SearchUnitBuilder.cs`
- `src/LiteratureApp.Infrastructure/Search/SearchIndexRebuilder.cs`

Work:

- After successful MinerU import, call:
  - `SearchUnits.RebuildForDocumentInstanceAsync(documentInstanceId)`
  - `SearchIndex.RebuildFtsForDocumentInstanceAsync(documentInstanceId)`
- For the first closed loop, this can be synchronous inside the workflow after import.
- Preserve existing status semantics: stale while search units/FTS are rebuilding, current for the document after FTS rebuild.

Unit tests:

- Workflow calls rebuild after successful import.
- Workflow does not rebuild FTS after failed import.
- MCP search can be tested at the service level using seeded DB state, not via UI or external process.

Important: this is still unit/integration-at-service-boundary testing inside the test process. Do not add full end-to-end UI/MCP-process tests in this issue.

### 9. First-run modal UI

Add:

- `src/LiteratureApp.UI/FirstRunWindow.axaml`
- `src/LiteratureApp.UI/FirstRunWindow.axaml.cs`
- `src/LiteratureApp.UI/FirstRunViewModel.cs`
- `src/LiteratureApp.UI/PdfCandidateViewModel.cs`
- `src/LiteratureApp.UI/ZoteroShellViewModel.cs`

Modify:

- `src/LiteratureApp.UI/MainWindow.axaml`
- `src/LiteratureApp.UI/MainWindow.axaml.cs`
- `src/LiteratureApp.UI/ViewModels.cs`
- `src/LiteratureApp.UI/App.axaml.cs`

UI shape:

- Main window becomes a Zotero-like shell:
  - left navigation/sidebar
  - center item/document list
  - right metadata/status inspector
  - lower or right preview/status region
- Existing debug tabs should be removed from the primary UI or moved behind a clearly secondary developer surface.
- On empty/new runtime DB, show `FirstRunWindow` as modal.
- The modal should expose one next action at a time.
- Raw IDs should be hidden by default; provide a "technical details" expander for debugging.

First-run modal steps:

1. Database path
2. Library display name
3. PDF folder
4. PDF candidate selection
5. Minimal item metadata
6. MinerU token/options
7. Import progress
8. MinerU extraction progress
9. Index progress
10. MCP verification result and MCP server command

Unit tests:

- ViewModel step transitions.
- Cannot advance without required inputs.
- Successful workflow updates selected IDs/status.
- Failure keeps user on recoverable step with a non-sensitive error.

No Playwright or UI automation tests in this issue.

### 10. MCP verification

Add:

- `src/LiteratureApp.Infrastructure/Workflows/McpVerificationService.cs`

Responsibilities:

- Given a runtime DB and document instance, issue an in-process `McpReadApi.SearchLibraryAsync` query.
- Use a known term from imported MinerU text, or the first non-empty imported text block.
- Return a simple verification result:
  - searchable yes/no
  - index status
  - matched unit count
  - sanitized sample text

Modify:

- `src/LiteratureApp.McpServer/Program.cs` only if command output/help needs to better show `--db <runtime.sqlite>`.

Unit tests:

- Verification succeeds when seeded search units/FTS contain a term.
- Verification reports not searchable when FTS is empty.
- Verification result contains no local path, token, signed URL, cache path, or provider secret.

### 11. Tests to add or update

Add:

- `tests/LiteratureApp.Tests/PdfDiscoveryServiceTests.cs`
- `tests/LiteratureApp.Tests/PdfImportWorkflowTests.cs`
- `tests/LiteratureApp.Tests/MinerUClientTests.cs`
- `tests/LiteratureApp.Tests/MinerUZipReaderTests.cs`
- `tests/LiteratureApp.Tests/MinerUContentListParserTests.cs`
- `tests/LiteratureApp.Tests/MinerUResultImporterTests.cs`
- `tests/LiteratureApp.Tests/FirstRunWorkflowTests.cs`
- `tests/LiteratureApp.Tests/FirstRunViewModelTests.cs`
- `tests/LiteratureApp.Tests/McpVerificationServiceTests.cs`

Modify:

- `tests/LiteratureApp.Tests/ProjectBoundaryTests.cs`
- `tests/LiteratureApp.Tests/OcrAdapterReadinessTests.cs`
- `tests/LiteratureApp.Tests/LocalImageOcrAdapterTests.cs`

Remove or rewrite:

- Tesseract-specific tests that no longer represent product behavior.

Testing rule:

- Unit tests only.
- No network calls to MinerU.
- No real GitHub/MCP external process tests.
- No UI automation.
- No end-to-end test for the whole first-run flow.
- Use fake `IMinerUClient`, fake importer inputs, fake HTTP handlers, and SQLite temp DB service-level tests.

### 12. Security and privacy

Modify/add tests around:

- `src/LiteratureApp.McpServer/McpTransport.cs`
- `src/LiteratureApp.Infrastructure/Mcp/McpReadApi.cs`
- `src/LiteratureApp.Infrastructure/Ocr/MinerU/MinerUClient.cs`
- `tests/LiteratureApp.Tests/AlphaSecurityBoundaryTests.cs`

Rules:

- MinerU token is never returned by UI workflow result objects.
- Signed upload URLs are never persisted in domain DB tables.
- `full_zip_url` is not exposed through MCP.
- Cache paths are not exposed through MCP.
- MCP outputs no local path, provider token, signed URL, model path, or cache path.

### 13. Obsolete/deferred work

Do not implement in this issue:

- Snapshot sync.
- File watcher.
- Bulk import of all PDFs.
- Bibliography metadata auto-detection.
- Cloud provider cost estimation.
- Candidate adoption UI.
- Evidence successor links.
- Search profiles/query rewrite UI.
- End-to-end tests.
- Local/offline MinerU deployment.
- Agent lightweight MinerU API path.

## Suggested Commit Slices

1. `docs: add minimal closed loop implementation plan`
2. `feat(ocr): add MinerU provider contracts`
3. `feat(ocr): add MinerU client and result download plumbing`
4. `feat(ocr): import MinerU result zip into layout revisions`
5. `feat(import): scan and import selected PDFs`
6. `feat(workflow): orchestrate first-run PDF import and indexing`
7. `feat(ui): add Zotero-like shell and first-run modal`
8. `chore(ocr): remove Tesseract from product UI path`
9. `test: cover MinerU import and first-run workflow units`

## Definition of Done

- User-facing UI starts from a literature-manager shell, not a tabbed diagnostics panel.
- First-run setup is modal.
- User can scan a folder and choose one PDF.
- User can create a minimal item for that PDF.
- App creates file asset, document instance, pages, one current layout revision, search units, and FTS.
- MinerU is the primary provider in the first-run OCR path.
- Tesseract is absent from product UI.
- MCP in-process verification can read the indexed text.
- All new coverage is unit/service-level tests only.
- No end-to-end tests are added in this issue.
