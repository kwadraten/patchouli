# OCR normalization and MinerU v4 evidence

User direction, 2026-10-21: Patchouli is based on the MinerU data model. Results from
other OCR engines normalize to that same model before adoption; each engine must not
define its own competing content semantics. The existing Box Tree still owns stable
identity, geometry and revisions. This is a normalization boundary, not permission to
store provider-specific fields in committed boxes.

## Verified upstream baseline

The research pins MinerU commit
[`b7f3dd0c5f6e6d66a298239ffd02b328b873918e`](https://github.com/opendatalab/MinerU/tree/b7f3dd0c5f6e6d66a298239ffd02b328b873918e).
Its [accepted Middle JSON contract](https://github.com/opendatalab/MinerU/blob/b7f3dd0c5f6e6d66a298239ffd02b328b873918e/docs/next/middle-json/current-medium.md)
uses the DocVortex models, with serialized `schema="docvortex.middle"` and
`schema_version="2.0"`. `table` contains one `table_body`; that body's content is a
string containing safe HTML or a spatial text projection. Table captions and footnotes
use typed inline spans separately from table-body HTML. These distinctions must survive
normalization rather than being inferred from hypothetical generic HTML.

The contract explicitly names `strong/em/u/s/sup/sub` for table text styles. This says
which representations may occur; it does not establish that every OCR mode recognizes
all of them. In particular, it does not justify arbitrary CSS, class names, hyperlinks
or code-formatted cells as purported scanned-OCR fixtures.

| Upstream path | Verified behavior | Testing implication |
| --- | --- | --- |
| [MinerU Medium table OCR](https://github.com/opendatalab/MinerU/blob/b7f3dd0c5f6e6d66a298239ffd02b328b873918e/mineru/backend/analysis/pdf/tables.py) | Recognized text is HTML-escaped before table assembly. Separately recognized formulas can enter table cells as `eq` nodes. | Cover grid/spans, escaped text and the actual formula representation; do not assume font-style recovery from image OCR. |
| [MinerU Flash table OCR](https://github.com/opendatalab/MinerU/blob/b7f3dd0c5f6e6d66a298239ffd02b328b873918e/mineru/backend/analysis/pdf/tables.py) | Uses an OCR spatial-text projection. | Do not require styled HTML from this path. |
| [mineru-vl-utils 2.0.5 OTSL conversion](https://github.com/opendatalab/mineru-vl-utils/blob/2bd170c309dd14bfc8a1fb82d43dfb4667fa20d3/mineru_vl_utils/post_process/otsl2html.py) | Generates table rows/cells and spans while escaping cell text. A result already expressed as a table HTML fragment is passed through. | Test the generated structural contract. HTML pass-through alone is not evidence that recognition restores arbitrary styles. |
| [DocVortex 0.5.16 native PDF table materialization](https://github.com/myhloli/DocVortex/blob/fb59f89dcb4f76dd1adbb3c84bd8dfd51a982c16/src/docvortex/analyzers/native/pdf/table_text_styles.py) | Uses native PDF character evidence for superscript/subscript and conditionally bold/italic markup. | Keep native PDF extraction distinct from scanned-image OCR. Use attributed upstream examples rather than inventing samples and calling them OCR output. |

The current checked MinerU source depends on DocVortex `>=0.5.16,<1` and
mineru-vl-utils `>=2.0.5,<3`; these are research pins, not a new dependency upgrade in
Patchouli. The [DocVortex table-body model](https://github.com/myhloli/DocVortex/blob/fb59f89dcb4f76dd1adbb3c84bd8dfd51a982c16/src/docvortex/schema.py)
stores its string content separately from typed inline spans.

## Application boundary

`MinerUResultParser` already prefers `middle_json.json` and checks the actual
`docvortex.middle` 2.0 discriminator through `MinerUMiddleJsonParser`. Older
content-list artifacts are compatibility inputs. Their internal adapter records
are not a second provider-defined document model.

Text-only OCR engines produce only text blocks, geometry, order and confidence;
normalization must not infer tables or formulas that those engines did not identify.
Full structured OCR extensions need the shared document candidate fields, including
subtype and code language, rather than the smaller local text-edit candidate.
Region text prefill is a separate text-edit operation and must not silently flatten
a structured MinerU result into text.

Spatial table text does not establish a cell grid. Until a lossless supported
representation is available, preserve the input with an explicit blocking diagnostic;
do not call it invalid upstream HTML, generate a guessed grid, or allow a placeholder
to stand in for lost table content at adoption.

## Evidence rules for this implementation

Fixtures must identify whether they model the upstream schema, a known producer path,
a captured real result, or the editor's own serialization. A fabricated fixture is not
a live OCR result. Add style-preservation requirements only for verified upstream
semantics or actual supported input; remove unsupported assumptions instead of adding
protective machinery around them. Editor-generated default attributes are adapter
behavior, not evidence about MinerU output.

The pinned MinerU Flash implementation calls DocVortex `project_table_text(...)` and
stores its string result as the table block's content. Patchouli accepts safe table HTML
as an authoritative table payload. A spatial-text projection is kept in the in-memory
candidate with an `unsupported_table_spatial_text` blocking diagnostic; it is not
converted into a guessed grid or accepted automatically. Legacy content-list tables
whose cells cannot be represented losslessly as GFM likewise retain a blocking
diagnostic and placeholder rather than being adopted as if complete.

Middle JSON `superscript` and `subscript` spans use the existing Markdig `^` and `~`
syntax. The current safe Markdown pipeline has no underline representation and disables
raw HTML; an `underline` text span therefore fails explicitly with
`unsupported_middle_semantics` rather than losing its style. The MinerU region OCR
shortcut accepts only ordinary text boxes. If structured content is returned, it asks
the user to use full-page or structured OCR review instead of flattening a table,
equation, code block, or other semantic box into a text leaf.
