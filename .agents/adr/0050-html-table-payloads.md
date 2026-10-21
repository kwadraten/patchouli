# 0050. Authoritative HTML table payloads

Status: accepted and implemented, 2026-10-21. Issue #26.

The Document Box Tree remains authoritative for identity, page ownership, geometry,
order, containment and revision history. A table's editable content can use restricted
HTML independently of the Markdown representation used by other content types.
New HTML table payloads explicitly identify their format and version. HTML is their
single editable representation; Markdown and search text are projections.

All OCR engines normalize to MinerU v4's shared data model. Supported input semantics
and regression fixtures follow the verified upstream contract and producer paths in
[OCR normalization](../ocr-normalization.md), rather than assumed generic HTML features.

The stored markers are `contentFormat: "html"` and `contentFormatVersion: 1`;
`html` contains the fragment and `markdown` is empty. Missing markers mean legacy
Markdown/version 0. XML uses `format="html-table-v1"` with escaped HTML text.

Legacy table payloads remain readable without rewriting committed history. Compatible
reads can recover usable HTML accompanying a legacy placeholder. Actual edits write the
new format through working revisions and the existing commit boundary. Missing or
unrecoverable content is never invented.

Validation defines allowed table and inline elements and attributes. Scripts, event
handlers and uncontrolled external resources are rejected. Unsupported editing features
must be preserved or must block a lossy save. Rendering does not grant permission to
load arbitrary resources. Native table editing retains undo/redo and existing active
draft, optimistic concurrency and atomic batch rules.

XML distinguishes HTML payloads explicitly. Legacy Markdown projections retain the
ADR 0044 compatibility warning and cannot silently replace structure they omit. HTML
translation candidates must preserve source table structure, merged-cell attributes and
protected inline content before the host commits them. Search and export derive their
representations from the authoritative payload. Reading, preview and export do not
mutate historical revisions.

The supported HTML subset includes table sections, rows, header/data cells, captions,
column definitions, nested tables within cells, common text formatting, paragraphs,
lists, safe links and restricted MathML. Cell spans are positive bounded integers.
MinerU's verified `<eq>LaTeX</eq>` cell content is retained as protected formula
content; translation must not change or remove it. RichEditor currently drops the
`eq` wrapper, so the editor adapter restores it only when the entire decoded cell
text remains unchanged and the serialized cell contains no replacement markup.
The editor also flattens row sections and header cells. The adapter restores the
source row grouping only when the row count agrees, then checks the full grid,
cell attributes and protected content before restoring header-cell names.
Attributes are whitelisted; CSS allows only the adapter's presentation properties and
safe values, without resource URLs or executable expressions. Validation rejects unknown
elements/attributes, comments, processing instructions and outside-table content.
The code-level whitelist lives in `TableContentAdapter`; extending it requires tests.
Editor support is narrower when its parser cannot roundtrip a supported structure:
that case remains readable but cannot be saved through a destructive editor conversion.

This amends ADR 0045's complex-table placeholder constraint. It does not change the
Markdown decision for body text, lists or algorithms, LaTeX for formulas, or literal
code content. TEI, DOCX, new PDF exports, OTSL storage, a general document AST and a
dynamic content-format plugin registry remain outside this change.

Implementation evidence must distinguish unit/contract tests, headless UI interaction,
and real desktop acceptance. Compilation alone does not establish the complete editing
workflow.
Current verification evidence is recorded in
[Extension validation](../extension-validation.md).
