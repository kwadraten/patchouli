update ocr_page_results
set working_tree_revision_id = null
where working_tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

update ocr_runs
set source_tree_revision_id = null
where source_tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

update ocr_runs
set output_tree_revision_id = null
where output_tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

update document_tree_revisions
set parent_tree_revision_id = null
where parent_tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

update document_tree_revisions
set reverted_from_tree_revision_id = null
where reverted_from_tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

update page_translations
set source_tree_revision_id = ''
where source_tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

delete from document_commit_pages
where tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

delete from fts_cache_state
where exists (
    select 1 from search_units
    where tree_revision_id in (
        select tree_revision_id from document_tree_revisions
        where status in ('discarded', 'staging', 'draft')));

delete from search_units_fts
where rowid in (
    select fts_row_id from fts_row_map
    where unit_id in (
        select unit_id from search_units
        where tree_revision_id in (
            select tree_revision_id from document_tree_revisions
            where status in ('discarded', 'staging', 'draft'))));

delete from fts_row_map
where unit_id in (
    select unit_id from search_units
    where tree_revision_id in (
        select tree_revision_id from document_tree_revisions
        where status in ('discarded', 'staging', 'draft')));

update search_units
set supersedes_unit_id = null
where supersedes_unit_id in (
    select unit_id from search_units
    where tree_revision_id in (
        select tree_revision_id from document_tree_revisions
        where status in ('discarded', 'staging', 'draft')));

update search_units
set superseded_by_unit_id = null
where superseded_by_unit_id in (
    select unit_id from search_units
    where tree_revision_id in (
        select tree_revision_id from document_tree_revisions
        where status in ('discarded', 'staging', 'draft')));

delete from search_units
where tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

delete from continuation_migration_diagnostics
where tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

delete from document_boxes
where tree_revision_id in (
    select tree_revision_id from document_tree_revisions
    where status in ('discarded', 'staging', 'draft'));

delete from document_tree_revisions
where status in ('discarded', 'staging', 'draft');
