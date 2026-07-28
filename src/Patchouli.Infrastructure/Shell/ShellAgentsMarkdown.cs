namespace Patchouli.Infrastructure.Shell;

public static class ShellAgentsMarkdown
{
    public const string Content =
        """
        # Patchouli Virtual Library Shell

        Patchouli is a personal literature manager. This environment is a **read-only** virtual shell over the currently open Library.

        ## Boundaries

        - No host filesystem access
        - No network
        - No external processes
        - No writes, temp files, or redirections that create files
        - No OCR, scanning, or index rebuild triggers

        ## VFS root

        ```text
        /
        ├── AGENTS.md
        ├── library.yml
        ├── items/
        ├── texts/
        └── csl-styles/
        ```

        Paths and `patchouli://` URIs are equivalent:

        - `patchouli://items/{item-id}.bib`
        - `patchouli://texts/{document-instance-id}/`
        - `patchouli://texts/{document-instance-id}/page-{page-index}.md`
        - `patchouli://texts/{document-instance-id}/page-{page-index}.md?evref={evref}`
        - `patchouli://csl-styles/{style-id}.csl`

        `/texts/{document-instance-id}/` appears only after OCR text exists for that document
        (current committed tree with non-empty box payload). Until then the folder is absent.
        Item `.bib` entries include `file = {patchouli://texts/.../}` only when such text exists.

        ## Evidence (`evref`)

        Search hits return URIs that include an opaque `evref` query parameter.
        That pins a specific evidence fragment. Removing the query reads the current page.
        Invalid `evref` values fail; they never silently fall back to current content.

        - `cat '…?evref=…'` returns the full pinned page
        - `evidence '…?evref=…'` returns the exact evidence fragment

        ## Search

        - `grep` / `rg`: .NET regex over search-unit text (no Search Profile rewrite); each match gets an `evref`
        - `search`: enhanced library search using the current Search Profile

        ## Meta TSV

        Domain-aware commands support `--meta` with fixed public columns:

        ```text
        type	uri	title	status
        ```

        Escape rules: `\` → `\\`, TAB → `\t`, LF → `\n`, CR → `\r`.

        ## Cite

        ```bash
        cite /items/{item-id}.bib
        cite --style /csl-styles/{id}.csl /items/a.bib /items/b.bib
        ```

        Accepts only Item paths/URIs. Formats a single plain-text CSL bibliography.

        ## Exit codes

        - `0` success
        - `1` domain error
        - `2` usage error
        - `124` timeout / output limit
        - `125` session reset / library change
        - `130` cancelled

        ## Recommended workflow

        ```bash
        pwd; ls; cat /AGENTS.md
        ls --meta /items
        rg --meta "keyword" /texts
        evidence 'patchouli://texts/.../page-0.md?evref=...'
        cite /items/{item-id}.bib
        ```

        When a report cites literature, obtain both the formatted bibliography with `cite` and the full
        evidence URI with `rg`/`search` plus `evidence`. Present them together as a Markdown link whose
        link text is the formatted bibliography and whose target is the complete evidence URI:

        ```markdown
        [Formatted bibliography](patchouli://texts/.../page-0.md?evref=...)
        ```

        Do not leave the bibliography and evidence URI as unrelated text, and do not replace the full
        `patchouli://` evidence URI with a bare `evref`.
        """;
}
