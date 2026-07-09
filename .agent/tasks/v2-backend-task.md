# Task: v2 Backend / Services Work

Source PRD: `.agent/PRD.md` (`Patchouli PRD v2 正式版`)

## Goal

Provide the service, storage, transport, and test boundaries needed for Patchouli v2 to become final-user usable. Backend work should make UI behavior reliable by exposing structured models instead of strings, and by enforcing product safety rules at service/transport boundaries.

## Primary Outcomes

- MCP server configuration is persisted, validated, and enforced, including per-tool enable/disable.
- CSL style management and bibliography rendering are available through tested services and MCP read tools.
- Initial PDF import uses `general` for unknown items and never silently confirms CSL `document`.
- Item editing is backed by type profiles, structured creator/date/identifier handling, and `extra_csl`.
- Production OCR providers normalize to a provider-neutral MinerU-compatible layout DTO before import/adoption.
- Blocking operations and conflicts are represented by shared models rather than ad hoc messages.
- Library DataGrid, settings UI, and OCR queue board have backend support for preferences and user-readable task data.

## Scope

### 1. MCP Server Settings And Transport Enforcement

Implement a persistent `McpServerSettings` or equivalent:

- `port`
- `bind_address`
- `cors_enabled`
- `allowed_origins`
- `auth_required`
- `token`
- `tool_overrides`
- `updated_at`

Requirements:

- `0.0.0.0` with no token is a blocking validation failure.
- HTTP auth uses `Authorization: Bearer <token>`.
- Auth failure returns 401.
- Token must not be logged.
- MCP token is local app/server configuration, not ProviderCredential.
- MCP token does not enter snapshots or branch imports.
- `/health` may return minimal unauthenticated status.
- JSON-RPC endpoint must be authenticated unless bound to `127.0.0.1` and user explicitly disables auth.

Per-tool enable/disable:

- Tool availability must be enforced by both tool enumeration and `tools/call`.
- Disabled tools must not appear in the returned tool list.
- Direct calls to disabled tools return a stable disabled/tool_unavailable error.
- Tool overrides must be testable without relying on UI hiding.

MCP long-term security boundary remains:

- MCP 从不触发 OCR 或索引重建.
- MCP does not write metadata, edit bbox, update/delete items, trigger OCR, rebuild indexes, expose local paths, return images, reveal file URLs, or leak provider secrets/configuration.

### 2. MCP CSL Read Tools

Add read-only MCP capabilities:

- `list_csl_styles`
- `get_csl_style`
- `render_item_bibliography`
- `render_items_bibliography`

Requirements:

- Return style id, style display name, locale, item ids, rendered text/html, warnings.
- Do not expose local file paths, cache paths, provider secrets, OCR provider settings, or local style file paths if not needed.
- Respect per-tool enable/disable.
- `general` item type must block rendering with `general_type_not_renderable`.
- Renderer failures must return warning/error; never report success with an empty bibliography.

### 3. CSL Style Services

Create service boundaries such as:

- `ICslStyleCatalog`
- `ICslStyleStore`
- `ICslItemMapper`
- `ICslRenderer`

Persistence:

- `csl_styles`: style_id, display_name, source_url, source_kind, content_hash, installed_at, updated_at, enabled/deleted state as needed.
- `csl_settings`: default style, locale, updated_at.

Requirements:

- Connect to Zotero Chinese community style entry `https://zotero-chinese.github.io/styles/`.
- Preserve ability to connect to official Zotero/CSL style repositories later.
- Cache style index locally.
- Support refresh, search, install, update, remove/disable.
- CSL style files should be stored in a manageable app/library location with source URL and content hash.
- Renderer failure returns structured status and warnings.
- Clipboard/UI behavior is frontend, but backend must provide enough error information to keep old clipboard unchanged.

### 4. CSL Item Mapper And Type Profiles

Implement `CslItemTypeProfile` and profile service:

- `item_type`
- `display_name`
- `description`
- `primary_fields`
- `recommended_fields`
- `advanced_fields`
- `creator_roles`
- `date_roles`
- `identifier_schemes`
- `field_labels`
- `hidden_by_default_fields`

Initial supported profiles:

- `general`
- `book`
- `article-journal`
- `chapter`
- `thesis`
- `report`
- `webpage`
- `manuscript`
- `paper-conference`
- `patent`
- `standard`

Mapper requirements:

- Map Item fields, structured creators, dates, identifiers, tags, and `extra_csl` into CSL JSON.
- `custom_fields_json` may be reused initially, but mapper must treat low-frequency CSL variables as explicit `extra_csl`.
- Do not guess type-specific fields inside renderer.
- `general` is a Patchouli UI/data-entry type, not a CSL type. Mapper must reject/block rendering with `general_type_not_renderable`.
- Preserve unknown/hidden fields when item type changes.

Item service requirements:

- New item creation should support staged identifiers saved with the item.
- Creator input supports family/given/literal.
- Date input supports literal and date-parts while preserving current literal behavior.

### 5. Initial PDF Import And Type Classification

Change initial PDF import semantics:

- `PdfDiscoveryService` discovers file facts only: path, page count, size, mtime, hash.
- It must not declare CSL item type.
- `PdfImportWorkflow` creates skeleton item + primary `DocumentInstance`.
- Unknown bibliographic type is stored as `general`, not CSL `document`.
- Saving/importing `general` is allowed, but CSL rendering must be blocked.

Type inference:

- Add `ItemTypeInference` or equivalent:
  - `item_id`
  - `suggested_type`
  - `confidence`
  - `source`
  - `evidence_summary`
  - `created_at`
  - `accepted_at`
- Sources may include DOI/ISBN/ISSN lookup, imported CSL JSON/BibTeX type, PDF/XMP metadata, filename/directory heuristics, OCR/text first-page extraction.
- Low-confidence inference produces suggestions only.
- Only high-confidence source or user confirmation converts `general` to a specific type.

### 6. OCR Provider Architecture

MinerU remains the preferred production OCR/layout provider. Its content-list-style output is the compatibility baseline for OCR text storage, editing, layout mapping, tables, bbox, SearchUnits, and evidence.

Backend requirements:

- Mock OCR remains available for tests or explicit developer paths only.
- Retired local CLI OCR paths and local placeholder are not production OCR providers.
- Multimodal LLM OCR providers may be added as production providers.
- All providers must normalize output into a provider-neutral MinerU-compatible DTO before import/adoption.

Introduce or formalize DTOs such as:

- `OcrLayoutDocument`
- `OcrLayoutPage`
- `OcrLayoutBlock`
- `OcrTableCell`

DTO must represent at least:

- page
- block type
- text/latex
- bbox
- confidence
- table/table_row/table_cell
- row/column/span/header metadata

Import/adoption requirements:

- No provider may directly write `layout_nodes`.
- All providers must pass through the same import/adoption service.
- Provider-specific raw responses may be retained for diagnostics/provenance, but are not the direct editing/search/MCP data model.
- Provider credentials follow ProviderCredential boundary: never in MCP, never in logs, never in immutable historical shards.
- Patchouli does not handle account registration, quota purchase, billing, balance checks, or cost estimation.

### 7. OCR Editor Service Support

Backend must support frontend OCR editor requirements:

- Region OCR input: page + normalized bbox + OCR Preset Version.
- Region OCR output enters staging/candidate, not current layout.
- Candidate comparison and adoption by region/page.
- Explicit selected-node replacement.
- No automatic deletion of overlapping surrounding nodes.
- Ordinary bbox overlap becomes structured conflict CF-06.
- Updating current layout marks search index stale/partial as appropriate.

### 8. BlockingOperation Service

Implement a shared blocking operation model:

- `operation_id`
- `operation_type`
- `scope_type`
- `scope_id`
- `status`
- `progress_current`
- `progress_total`
- `progress_label`
- `can_cancel`
- `failure_code`
- `failure_message`
- `next_actions`
- log/detail entries for UI display

Initial operation types:

- `initial_root_scan`
- `file_search_root_scan`
- `snapshot_import_validation`
- `mcp_start_validation`
- `csl_style_install`

Requirements:

- AddSearchRoot creates a scan run and does not mark root fully ready until scan completes.
- Initial folder-root scan blocks app readiness.
- Snapshot import validation must not mutate active runtime DB on failure.
- MCP start validation blocks unsafe `0.0.0.0` without token.
- CSL style install failure blocks the style from becoming default, but does not block library use.

### 9. ConflictDescriptor Service Boundary

Implement unified conflict model:

- `conflict_code`: CF-01 to CF-06
- `domain`: snapshot_sync, file_resolution, layout_edit
- `severity`: blocking, warning, info
- `object_type`
- `object_id`
- `summary`
- `local_snapshot`
- `incoming_snapshot`
- `recommended_actions`
- `selected_action`
- `resolution_status`: unresolved, resolved, ignored, superseded

Initial conflicts:

- CF-01 same item_id with Title/Type difference (`same_id_different_content`)
- CF-02 primary document conflict (`primary_document_conflict`)
- CF-03 credential not imported (`credential_not_imported`)
- CF-04 file relocation multiple candidates
- CF-05 source file changed / bbox basis stale
- CF-06 layout bbox ordinary overlap

Requirements:

- Existing `BranchImportConflict` may be adapted into `ConflictDescriptor`.
- UI must not parse error message substrings.
- File resolution and layout edit conflicts must use the same descriptor shape.
- `credential_not_imported` is non-blocking warning/conflict item; import can continue, but related preset enters `credential_missing`.

### 10. Library Preferences And User-Readable Queue Data

Support frontend DataGrid and OCR queue requirements:

Library preferences:

- Persist DataGrid column order.
- Persist column visibility.
- Persist column width if practical.
- Scope preference appropriately, likely by library/user/app profile.

Library list data:

- Provide OCR/index status.
- Provide page count.
- Provide linked file name.
- Support sort operations or expose enough data for ViewModel sorting.

OCR queue:

- Queue rows must expose user-readable item title, not only task/document/preset IDs.
- Backend/ViewModel service should support row-level pause/resume/cancel by stable task identity.
- Global pause/resume remains supported.

## File-Level Implementation Plan

This section is the file-level task breakdown. Paths marked "new" are proposed files; paths marked "modify" already exist.

Migration filenames below assume the current latest migration is `014_add_table_cell_metadata.sql`. When implementing, use the next available migration number in the actual branch instead of blindly reusing the example number.

### 1. MCP Settings, Auth, And Tool Overrides

Core contracts:

- Modify `src/Patchouli.Mcp/McpDtos.cs`
  - Add MCP settings DTOs if the contract layer owns them, or add response DTOs for tool availability/errors.
  - Add stable error code constants or DTO fields for `disabled` / `tool_unavailable`.
- Modify `src/Patchouli.Mcp/IMcpReadApi.cs`
  - Do not add write methods.
  - Add CSL read-only API methods only after CSL service contracts exist.
- Add `src/Patchouli.Core/Mcp/McpServerSettings.cs`
  - `port`, `bind_address`, `cors_enabled`, `allowed_origins`, `auth_required`, `token`, `tool_overrides`, `updated_at`.
- Add `src/Patchouli.Core/Mcp/McpToolOverride.cs`
  - Represent tool name, enabled/disabled state, and optional disabled reason.
- Add `src/Patchouli.Core/Mcp/IMcpServerSettingsService.cs`
  - Load/save/validate settings.
  - Validate unsafe `0.0.0.0` without token.

Infrastructure:

- Add `src/Patchouli.Infrastructure/Mcp/McpServerSettingsService.cs`
  - Persist settings.
  - Return structured validation failures.
  - Ensure token is not logged.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/015_create_mcp_server_settings.sql`
  - Create MCP settings table and optional tool override table.
  - Keep MCP token local/runtime configuration; do not include it in snapshot shards.
- Modify `src/Patchouli.Infrastructure/Snapshots/SnapshotServices.cs`
  - Exclude MCP server settings and token/tool overrides from immutable snapshot export.
  - Add explicit tests/guards if snapshot table allowlists are changed.

Transport:

- Modify `src/Patchouli.McpServer/McpTransport.cs`
  - Filter disabled tools from tools/list output.
  - Reject disabled tools in tools/call with stable `disabled` / `tool_unavailable` error.
  - Wire CSL tools once added.
- Modify `src/Patchouli.McpServer/McpHttpServer.cs`
  - Enforce bearer token on JSON-RPC endpoint.
  - Keep `/health` minimal and unauthenticated if desired.
  - Return 401 on auth failure.
- Modify `src/Patchouli.McpServer/Program.cs`
  - Load `McpServerSettings`.
  - Apply bind address, port, CORS, auth, and tool overrides.
  - Fail fast or report validation failure for unsafe `0.0.0.0` without token.
- Modify `src/Patchouli.Infrastructure/Workflows/McpVerificationService.cs`
  - Add validation for settings, auth, unsafe bind, and enabled tool state as needed.

Tests:

- Add or extend `tests/Patchouli.Tests/McpServerSettingsTests.cs`
  - Save/load settings.
  - Validate unsafe bind.
  - Token not included in logs/snapshot payloads.
- Extend `tests/Patchouli.Tests/McpServerTransportTests.cs`
  - Disabled tool absent from list.
  - Disabled tool direct call returns `disabled` / `tool_unavailable`.
  - Auth failure returns 401.
- Extend `tests/Patchouli.Tests/AlphaSecurityBoundaryTests.cs`
  - MCP token/tool settings do not leak into snapshots/logs.

### 2. CSL Style Store, Catalog, Mapper, Renderer

Core contracts:

- Add `src/Patchouli.Core/Csl/CslStyle.cs`
  - Style id, display name, source URL, source kind, content hash, installed/updated timestamps, state.
- Add `src/Patchouli.Core/Csl/CslSettings.cs`
  - Default style id, locale, updated timestamp.
- Add `src/Patchouli.Core/Csl/CslRenderRequest.cs`
  - Item ids, style id, locale, output format options.
- Add `src/Patchouli.Core/Csl/CslRenderResult.cs`
  - Rendered text/html, warnings, errors, style metadata, item ids.
- Add `src/Patchouli.Core/Csl/ICslStyleCatalog.cs`
  - Refresh/search remote style index.
- Add `src/Patchouli.Core/Csl/ICslStyleStore.cs`
  - Install/update/remove/disable/list local styles and settings.
- Add `src/Patchouli.Core/Csl/ICslItemMapper.cs`
  - Convert Item metadata to CSL JSON or structured map result.
- Add `src/Patchouli.Core/Csl/ICslRenderer.cs`
  - Render bibliography and return warnings/errors.

Infrastructure:

- Add `src/Patchouli.Infrastructure/Csl/CslStyleCatalog.cs`
  - Fetch/cache Zotero Chinese community style index.
  - Keep official Zotero/CSL repository support pluggable.
- Add `src/Patchouli.Infrastructure/Csl/CslStyleStore.cs`
  - Persist style metadata and style files.
  - Verify content hash.
- Add `src/Patchouli.Infrastructure/Csl/CslItemMapper.cs`
  - Map Item fields, creators, dates, identifiers, tags, and `extra_csl`.
  - Reject/block `general` with `general_type_not_renderable`.
- Add `src/Patchouli.Infrastructure/Csl/CslRenderer.cs`
  - Wrap chosen CSL processor.
  - Never report success with empty output.
  - Return structured warnings/errors.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/016_create_csl_styles.sql`
  - Create `csl_styles` and `csl_settings`.
- Modify `src/Patchouli.Infrastructure/Patchouli.Infrastructure.csproj`
  - Include any style resources or package references if needed.
- Modify `Directory.Packages.props`
  - Add CSL rendering/catalog dependencies if selected.

MCP integration:

- Modify `src/Patchouli.Mcp/McpDtos.cs`
  - Add DTOs for style list/style details/render response.
- Modify `src/Patchouli.Mcp/IMcpReadApi.cs`
  - Add `ListCslStylesAsync`, `GetCslStyleAsync`, `RenderItemBibliographyAsync`, `RenderItemsBibliographyAsync` or equivalent read-only methods.
- Modify `src/Patchouli.Infrastructure/Mcp/McpReadApi.cs`
  - Implement CSL methods by delegating to CSL services.
  - Preserve MCP no-path/no-secret boundary.
- Modify `src/Patchouli.McpServer/McpTransport.cs`
  - Expose CSL tools in tools/list subject to tool overrides.
  - Route tools/call arguments to new MCP read API methods.

Tests:

- Add `tests/Patchouli.Tests/CslStyleStoreTests.cs`
  - Install/update/remove/default locale/default style.
- Add `tests/Patchouli.Tests/CslStyleCatalogTests.cs`
  - Refresh/search/cache behavior with fake HTTP/index.
- Add `tests/Patchouli.Tests/CslItemMapperTests.cs`
  - Core CSL fields.
  - Creators family/given/literal.
  - Dates literal/date-parts.
  - Identifiers.
  - `extra_csl`.
  - `general_type_not_renderable`.
- Add `tests/Patchouli.Tests/CslRendererTests.cs`
  - Success, warnings, error, empty-output failure.
- Extend `tests/Patchouli.Tests/McpReadApiTests.cs`
  - CSL responses do not expose local paths/secrets.
  - `general` returns blocked warning/error.
- Extend `tests/Patchouli.Tests/McpServerTransportTests.cs`
  - CSL tools are listed/callable only when enabled.

### 3. CSL Type Profiles And Item Editing Support

Core contracts:

- Add `src/Patchouli.Core/Bibliography/CslItemTypeProfile.cs`
  - Profile fields from PRD.
- Add `src/Patchouli.Core/Bibliography/ICslItemTypeProfileService.cs`
  - List profiles, get by type, validate type.
- Add `src/Patchouli.Core/Bibliography/ItemTypeInference.cs`
  - Suggested type, confidence, source, evidence summary, created/accepted timestamps.
- Modify `src/Patchouli.Core/Bibliography/IItemService.cs`
  - Add create/update overload or request object support for staged identifiers.
  - Avoid expanding long parameter lists further if possible; prefer request records.
- Modify `src/Patchouli.Core/Bibliography/UpdateItemRequest.cs`
  - Preserve hidden/extra fields through type changes.
  - Ensure structured creators/dates stay first-class.
- Modify `src/Patchouli.Core/Bibliography/ItemCreator.cs`
  - Confirm family/given/literal inputs cover UI needs.
- Modify `src/Patchouli.Core/Bibliography/ItemDate.cs`
  - Add or confirm date-parts support.

Infrastructure:

- Add `src/Patchouli.Infrastructure/Bibliography/CslItemTypeProfileService.cs`
  - Built-in profiles in code or embedded JSON.
- Add `src/Patchouli.Infrastructure/Bibliography/ItemTypeInferenceService.cs`
  - Store suggestions and accept/reject transitions.
- Modify `src/Patchouli.Infrastructure/Bibliography/ItemService.cs`
  - Support staged identifier creation in same transaction as item creation.
  - Preserve `custom_fields_json` as `extra_csl` backing store until a clearer column is introduced.
  - Preserve hidden fields when `item_type` changes.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/017_create_item_type_inferences.sql`
  - Store type suggestions and acceptance metadata.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/018_add_item_type_status.sql`
  - Only if `item_type = general` is insufficient for UI/backend state.

Tests:

- Extend `tests/Patchouli.Tests/BibliographicCoreTests.cs`
  - New item with staged identifiers saves item and identifiers in one transaction.
  - Type change preserves hidden/extra fields.
  - Structured creators and dates round-trip.
- Add `tests/Patchouli.Tests/CslItemTypeProfileTests.cs`
  - Required built-in profiles exist.
  - `general` is present but marked non-renderable for CSL.
- Add `tests/Patchouli.Tests/ItemTypeInferenceTests.cs`
  - Suggestion creation.
  - Low-confidence does not confirm type.
  - User acceptance converts `general` to concrete type.

### 4. Initial PDF Import And Discovery

Core:

- Modify `src/Patchouli.Core/Import/PdfDiscoveryModels.cs`
  - Ensure `PdfCandidate` contains only file facts, not CSL item type.
  - If needed, add import result fields indicating created item type/status.

Infrastructure:

- Modify `src/Patchouli.Infrastructure/Workflows/PdfDiscoveryService.cs`
  - Keep discovery limited to file candidates and facts.
- Modify `src/Patchouli.Infrastructure/Workflows/PdfImportWorkflow.cs`
  - Replace hard-coded `CreateItemAsync("document", ...)` with `general`.
  - Create skeleton item + primary `DocumentInstance`.
  - Create type inference suggestion only when evidence exists.
- Modify `src/Patchouli.Infrastructure/Workflows/PdfMetadataReader.cs`
  - Keep page count behavior.
  - Add metadata extraction only if it feeds `ItemTypeInference` as a suggestion, not as silent confirmation.
- Modify `src/Patchouli.Infrastructure/Workflows/FirstRunWorkflow.cs`
  - Initial scan/import should reflect `general`/needs-classification outcome if it creates items.

Tests:

- Extend `tests/Patchouli.Tests/PdfImportWorkflowTests.cs`
  - Imported unknown PDF creates item with `item_type = general`.
  - Does not create `document` by default.
  - Creates primary `DocumentInstance` and pages as before.
- Extend `tests/Patchouli.Tests/PdfDiscoveryServiceTests.cs`
  - Discovery does not infer/emit CSL type.
- Extend `tests/Patchouli.Tests/FirstRunViewModelTests.cs` only if first-run import behavior is surfaced through ViewModel.

### 5. OCR Provider-Neutral Layout DTO And Import Boundary

Core/OCR contracts:

- Add `src/Patchouli.Ocr/OcrLayoutDocument.cs`
  - Document-level OCR layout DTO.
- Add `src/Patchouli.Ocr/OcrLayoutPage.cs`
  - Page metadata and blocks.
- Add `src/Patchouli.Ocr/OcrLayoutBlock.cs`
  - Block type, text/latex, bbox, confidence, reading order, table refs.
- Add `src/Patchouli.Ocr/OcrTableCell.cs`
  - Row/column/span/header metadata.
- Add `src/Patchouli.Ocr/IOcrLayoutImporter.cs`
  - Provider-neutral import/adoption boundary.
- Modify `src/Patchouli.Ocr/OcrAdapterContracts.cs`
  - Provider adapters return or can convert to `OcrLayoutDocument`.
- Modify `src/Patchouli.Ocr/IOcrEngine.cs`
  - Avoid provider-specific direct layout writes.

MinerU:

- Modify `src/Patchouli.Infrastructure/Ocr/MinerU/MinerUContentListParser.cs`
  - Parse MinerU output into `OcrLayoutDocument` or a MinerU DTO that immediately maps to it.
- Modify `src/Patchouli.Infrastructure/Ocr/MinerU/MinerULayoutNodeMapper.cs`
  - Accept provider-neutral DTO or become internal MinerU-to-OcrLayoutDocument mapper.
- Modify `src/Patchouli.Infrastructure/Ocr/MinerU/MinerUResultImporter.cs`
  - Stop being the only layout writer if possible.
  - Delegate final layout persistence to provider-neutral importer.
- Modify `src/Patchouli.Infrastructure/Ocr/MinerU/MinerUResultDownloader.cs`
  - Preserve download/chunk behavior; ensure output enters common import path.

Infrastructure:

- Add `src/Patchouli.Infrastructure/Ocr/OcrLayoutImporter.cs`
  - Convert `OcrLayoutDocument` into `LayoutRevision` / `LayoutNode`.
  - Handle table cell metadata.
  - Mark search units/index stale as required.
- Modify `src/Patchouli.Infrastructure/Ocr/OcrRunCoordinator.cs`
  - Route all provider outputs through `IOcrLayoutImporter`.
- Modify `src/Patchouli.Infrastructure/Ocr/OcrQueueTaskExecutor.cs`
  - Ensure queued OCR runs use the common importer.
- Modify `src/Patchouli.Infrastructure/Layout/LayoutTreeService.cs`
  - Keep layout mutation rules and bbox overlap validation reusable by OCR adoption.
- Modify `src/Patchouli.Ocr/MockOcrEngine.cs`
  - Keep for tests/dev paths; ensure production readiness does not expose it.
- Remove any retired local CLI OCR adapter implementation instead of keeping it on the production path.

Tests:

- Add `tests/Patchouli.Tests/OcrLayoutDocumentTests.cs`
  - DTO validation and table metadata shape.
- Add `tests/Patchouli.Tests/OcrLayoutImporterTests.cs`
  - Provider-neutral import creates layout revisions/nodes/table cells.
  - Search/index stale behavior.
- Extend `tests/Patchouli.Tests/MinerUContentListParserTests.cs`
  - MinerU output maps to provider-neutral DTO.
- Extend `tests/Patchouli.Tests/MinerULayoutNodeMapperTests.cs`
  - Mapper boundary updated for DTO.
- Extend `tests/Patchouli.Tests/MinerUResultImporterTests.cs`
  - Importer delegates to common importer.
- Extend `tests/Patchouli.Tests/OcrAdapterReadinessTests.cs`
  - Production UI/readiness excludes Mock/retired local CLI OCR/local placeholder.
- Add a guard test in `tests/Patchouli.Tests/ProjectBoundaryTests.cs`
  - Provider implementations must not directly insert into `layout_nodes` except `OcrLayoutImporter`.

### 6. OCR Editor Backend Support

Core/OCR:

- Modify `src/Patchouli.Ocr/IOcrRunCoordinator.cs`
  - Confirm `RunPresetOnRegionAsync` supports page + normalized bbox + preset version.
  - Add adoption APIs if current ones cannot target selected nodes/regions.
- Modify `src/Patchouli.Ocr/OcrCandidateAdoption.cs`
  - Support selected-node replacement and region/page adoption semantics.
- Modify `src/Patchouli.Ocr/OcrRunState.cs`
  - Ensure staging/candidate/current states cover region OCR.

Infrastructure:

- Modify `src/Patchouli.Infrastructure/Ocr/OcrRunCoordinator.cs`
  - Region OCR creates staging/candidate output only.
  - Adoption does not automatically delete overlapping nodes.
- Modify `src/Patchouli.Infrastructure/Layout/LayoutTreeService.cs`
  - Return structured conflict for ordinary bbox overlap, suitable for CF-06.
- Modify `src/Patchouli.Infrastructure/Search/SearchUnitBuilder.cs`
  - Rebuild/dirty behavior after adopted layout changes.
- Modify `src/Patchouli.Infrastructure/Search/SearchIndexRebuilder.cs`
  - Ensure adopted OCR/layout changes mark FTS stale/partial/current correctly.

Tests:

- Extend `tests/Patchouli.Tests/OcrLifecycleTests.cs`
  - Region OCR stays candidate/staging before adoption.
  - Adoption updates current only through explicit action.
- Extend `tests/Patchouli.Tests/PageLayoutTests.cs`
  - Ordinary bbox overlap maps to CF-06-compatible conflict.
- Extend `tests/Patchouli.Tests/SearchTests.cs`
  - Adoption marks search units/index stale or rebuilds as intended.

### 7. BlockingOperation Model And Services

Core:

- Add `src/Patchouli.Core/Operations/BlockingOperation.cs`
  - Fields from task scope.
- Add `src/Patchouli.Core/Operations/BlockingOperationLogEntry.cs`
  - Timestamp, level, message, optional detail/scope.
- Add `src/Patchouli.Core/Operations/IBlockingOperationService.cs`
  - Start/update/complete/fail/cancel/list operations.

Infrastructure:

- Add `src/Patchouli.Infrastructure/Operations/BlockingOperationService.cs`
  - Persist and update operations.
  - Store log/detail entries.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/019_create_blocking_operations.sql`
  - Operation table and log table.
- Modify `src/Patchouli.Infrastructure/Files/FileResolutionService.cs`
  - AddSearchRoot / relocation scan emits blocking operation progress.
- Modify `src/Patchouli.Infrastructure/Workflows/FirstRunWorkflow.cs`
  - Initial folder scan emits blocking operation and gates readiness.
- Modify `src/Patchouli.Infrastructure/Snapshots/SnapshotServices.cs`
  - Snapshot import validation emits blocking operation and never mutates active DB on failure.
- Modify `src/Patchouli.Infrastructure/Workflows/McpVerificationService.cs`
  - MCP start validation emits blocking operation failure for unsafe config.
- Modify `src/Patchouli.Infrastructure/Csl/CslStyleStore.cs`
  - CSL install emits blocking operation; failed install cannot become default.

Tests:

- Add `tests/Patchouli.Tests/BlockingOperationServiceTests.cs`
  - Lifecycle transitions.
  - Progress/log updates.
  - Cancellation.
- Extend `tests/Patchouli.Tests/FileResolutionServiceTests.cs`
  - AddSearchRoot creates scan operation and root readiness waits.
- Extend `tests/Patchouli.Tests/FirstRunViewModelTests.cs` or workflow tests
  - Initial scan blocks readiness.
- Extend `tests/Patchouli.Tests/SnapshotTests.cs`
  - Validation failure leaves runtime DB unchanged and records operation failure.
- Extend `tests/Patchouli.Tests/McpVerificationServiceTests.cs`
  - Unsafe MCP config creates blocking validation failure.
- Extend `tests/Patchouli.Tests/CslStyleStoreTests.cs`
  - Failed style install blocks default selection.

### 8. ConflictDescriptor Boundary

Core:

- Add `src/Patchouli.Core/Conflicts/ConflictDescriptor.cs`
  - Fields from task scope.
- Add `src/Patchouli.Core/Conflicts/ConflictCode.cs`
  - CF-01 to CF-06 stable codes.
- Add `src/Patchouli.Core/Conflicts/ConflictAction.cs`
  - Recommended/selected action representation.
- Add `src/Patchouli.Core/Conflicts/IConflictService.cs`
  - Optional service if conflicts are persisted/queried globally.

Infrastructure:

- Add `src/Patchouli.Infrastructure/Conflicts/ConflictDescriptorMapper.cs`
  - Convert existing conflict sources into `ConflictDescriptor`.
- Modify `src/Patchouli.Infrastructure/Snapshots/SnapshotBranchInspection.cs`
  - Map `same_id_different_content`, `primary_document_conflict`, `credential_not_imported`.
- Modify `src/Patchouli.Infrastructure/Snapshots/SnapshotServices.cs`
  - Return descriptors or descriptor-compatible DTOs in branch import plans.
- Modify `src/Patchouli.Infrastructure/Files/FileResolutionService.cs`
  - Map multiple candidate conflict to CF-04.
  - Map changed source / bbox basis stale to CF-05.
- Modify `src/Patchouli.Infrastructure/Layout/LayoutTreeService.cs`
  - Map ordinary bbox overlap to CF-06.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/020_create_conflicts.sql`
  - Only if conflicts need persisted status beyond existing branch/file/layout flows.

Tests:

- Add `tests/Patchouli.Tests/ConflictDescriptorTests.cs`
  - Descriptor shape, stable code, recommended actions.
- Extend `tests/Patchouli.Tests/SnapshotBranchInspectionTests.cs`
  - CF-01, CF-02, CF-03 descriptor mapping.
- Extend `tests/Patchouli.Tests/FileResolutionServiceTests.cs`
  - CF-04 and CF-05 descriptor mapping.
- Extend `tests/Patchouli.Tests/PageLayoutTests.cs`
  - CF-06 descriptor mapping.

### 9. Library Preferences, DataGrid Data, And Queue Rows

Core:

- Add `src/Patchouli.Core/Library/LibraryPreferences.cs`
  - DataGrid columns: order, visibility, width.
- Add `src/Patchouli.Core/Library/ILibraryPreferencesService.cs`
  - Load/save preferences by library/app scope.
- Add `src/Patchouli.Core/Library/LibraryItemRow.cs`
  - Row DTO for library grid: item metadata plus OCR/index status, page count, linked file name.
- Add `src/Patchouli.Core/Library/ILibraryItemQueryService.cs`
  - Query library rows with sort/filter/page options if ViewModel sorting is not enough.
- Add or extend `src/Patchouli.Ocr/OcrQueueContracts.cs`
  - Queue row DTO includes item title and row-level action capability.

Infrastructure:

- Add `src/Patchouli.Infrastructure/LibraryIdentity/LibraryPreferencesService.cs`
  - Persist preferences.
- Add `src/Patchouli.Infrastructure/LibraryIdentity/LibraryItemQueryService.cs`
  - Query items joined to primary document, pages, search index status, linked file name.
- Add next migration, e.g. `src/Patchouli.Infrastructure/migrations/021_create_library_preferences.sql`
  - Store JSON preferences or normalized column settings.
- Modify `src/Patchouli.Ocr/OcrQueueScheduler.cs`
  - Expose stable task identity and action state if not already present.
- Modify `src/Patchouli.Infrastructure/Ocr/OcrQueueTaskExecutor.cs`
  - Ensure queue row data can resolve item title through document/item joins.
- Modify `src/Patchouli.Infrastructure/Documents/DocumentInstanceService.cs`
  - Provide helper query if needed for item title/document resolution.

Tests:

- Add `tests/Patchouli.Tests/LibraryPreferencesServiceTests.cs`
  - Column order/visibility/width round-trip.
  - Library/app scoping.
- Add `tests/Patchouli.Tests/LibraryItemQueryServiceTests.cs`
  - Rows include OCR/index status, page count, linked file name.
- Extend `tests/Patchouli.Tests/OcrQueueSchedulerTests.cs`
  - Queue rows expose item title and row-level pause/resume/cancel state.

### 10. Project Wiring

Core project:

- Modify `src/Patchouli.Core/Patchouli.Core.csproj`
  - Include new folders automatically if SDK-style default includes are active; otherwise add compile includes.

Infrastructure project:

- Modify `src/Patchouli.Infrastructure/Patchouli.Infrastructure.csproj`
  - Ensure new migrations are embedded/copied consistently with existing migrations.
  - Add package references for CSL/catalog HTTP/rendering dependencies if needed.

MCP projects:

- Modify `src/Patchouli.Mcp/Patchouli.Mcp.csproj`
  - Include new DTOs/contracts.
- Modify `src/Patchouli.McpServer/Patchouli.McpServer.csproj`
  - Add references only if new settings/CSL services require them.

OCR project:

- Modify `src/Patchouli.Ocr/Patchouli.Ocr.csproj`
  - Include provider-neutral OCR layout DTOs.

Tests project:

- Modify `tests/Patchouli.Tests/Patchouli.Tests.csproj`
  - Include fixtures or package references required by CSL tests.
  - Keep tests using local fixtures/fake HTTP where possible.

## Out Of Scope For Backend Task

- Building final Avalonia UI controls.
- Visual styling and layout.
- Vector/semantic search.
- Program-hosted original file sync.
- Accounts, quota purchase, billing, balance checks, or cloud cost management.
- MCP write operations.

## Acceptance Mapping

- V2-AC1: MCP settings persistence, validation, tool overrides.
- V2-AC2: MCP auth and unsafe bind blocking.
- V2-AC3: CSL style catalog/store/renderer.
- V2-AC4: MCP CSL tools and security boundary.
- V2-AC5: `CslItemTypeProfile`, structured creator/date/identifier support.
- V2-AC6: PDF import `general`, type inference, blocked CSL rendering.
- V2-AC7: OCR editor service support, local OCR staging/adoption.
- V2-AC8: Production OCR provider boundary.
- V2-AC9: MinerU preferred provider, multimodal LLM normalization.
- V2-AC10: BlockingOperation service.
- V2-AC11: ConflictDescriptor service and CF-01 to CF-06.
- V2-AC13: Blocking operations for AddSearchRoot, first scan, snapshot validation, MCP start.
- V2-AC14: Provider-neutral OCR import/adoption service.
- V2-AC15: CSL mapper tests.
- V2-AC16: Queue row title and row-level action support.
- V2-AC17: Library preferences and list data support.

## Suggested Test Focus

- MCP settings persistence and validation tests.
- MCP auth tests for 401 and no token logging.
- MCP tool override tests:
  - Disabled tool absent from list.
  - Disabled tool direct call returns disabled/tool_unavailable.
- CSL style catalog/store tests.
- CSL renderer tests for failure and warnings.
- CSL item mapper tests for core fields, creators, dates, identifiers, `extra_csl`.
- `general` mapper tests returning `general_type_not_renderable`.
- PdfImportWorkflow tests proving unknown PDFs create `general`, not `document`.
- Type inference tests for suggestion vs confirmation.
- OCR DTO normalization tests for MinerU and at least one non-MinerU provider shape.
- Guard test that no provider directly writes `layout_nodes`.
- BlockingOperation tests for initial scan, AddSearchRoot scan, MCP start validation, snapshot validation, CSL install.
- ConflictDescriptor mapping tests for snapshot branch, file resolution, source changed, and layout overlap.
- DataGrid preference persistence tests.
- OCR queue row data tests showing item title and supporting row-level operations.
