-- Local operations must not traverse unrelated document payload or reference history.
create index if not exists idx_document_boxes_document on document_boxes(document_instance_id);
create index if not exists idx_document_tree_revisions_page_status_current
    on document_tree_revisions(page_id, status, is_current);
create index if not exists idx_document_tree_revisions_parent
    on document_tree_revisions(parent_tree_revision_id);
create index if not exists idx_document_tree_revisions_reverted_from
    on document_tree_revisions(reverted_from_tree_revision_id);
create index if not exists idx_document_commits_parent on document_commits(parent_commit_id);
create index if not exists idx_document_commit_pages_page on document_commit_pages(page_id);
create index if not exists idx_ocr_candidate_adoptions_document
    on ocr_candidate_adoptions(document_instance_id);
create index if not exists idx_ocr_candidate_adoptions_run_created
    on ocr_candidate_adoptions(ocr_run_id, created_at);
create index if not exists idx_ocr_page_results_working_revision
    on ocr_page_results(working_tree_revision_id);
create index if not exists idx_ocr_runs_retry on ocr_runs(retry_of_run_id);
create index if not exists idx_ocr_runs_source_revision on ocr_runs(source_tree_revision_id);
create index if not exists idx_ocr_runs_output_revision on ocr_runs(output_tree_revision_id);
create index if not exists idx_ocr_runs_preset on ocr_runs(preset_id);
create index if not exists idx_ocr_runs_preset_version on ocr_runs(preset_version_id);
create index if not exists idx_search_units_supersedes on search_units(supersedes_unit_id);
create index if not exists idx_search_units_superseded_by on search_units(superseded_by_unit_id);
create index if not exists idx_search_units_page_revision_status_order
    on search_units(page_id, tree_revision_id, status, ordinal, unit_id);
create index if not exists idx_search_rewrite_rules_profile on search_rewrite_rules(profile_id);

-- A trash listing is ordered by creation time, not by the time it was discarded.
create index if not exists idx_items_trash_created
    on items(created_at desc, item_id desc)
    where deleted_at is not null and merged_into_item_id is null;
create index if not exists idx_items_library_trash_created
    on items(library_id, created_at desc, item_id desc)
    where deleted_at is not null and merged_into_item_id is null;

create index idx_ocr_page_results_state on ocr_page_results(state);
create index idx_document_instances_created on document_instances(created_at, document_instance_id);
create index idx_file_assets_created on file_assets(created_at, file_asset_id);
