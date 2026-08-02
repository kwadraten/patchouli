create index if not exists idx_items_active_created
    on items(deleted_at, created_at desc, item_id desc);

create index if not exists idx_document_instances_item_primary
    on document_instances(item_id, is_primary);

create index if not exists idx_ocr_runs_document_hidden_created
    on ocr_runs(document_instance_id, hidden, created_at desc, ocr_run_id desc);

create index if not exists idx_ocr_page_results_run_created
    on ocr_page_results(ocr_run_id, created_at desc, result_id desc);

create index if not exists idx_search_units_document_status
    on search_units(document_instance_id, status);
