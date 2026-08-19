-- The 036 rebuild of ocr_page_results dropped the composite index added in 031.
create index if not exists idx_ocr_page_results_run_created
    on ocr_page_results(ocr_run_id, created_at desc, result_id desc);
