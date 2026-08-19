pragma foreign_keys = off;

create table ocr_page_results_new (
    result_id text primary key not null,
    ocr_run_id text not null,
    page_id text not null,
    state text not null,
    working_tree_revision_id text null,
    error_code text null,
    error_message text null,
    created_at text not null,
    updated_at text not null,
    foreign key (ocr_run_id) references ocr_runs(ocr_run_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (working_tree_revision_id) references document_tree_revisions(tree_revision_id),
    unique (ocr_run_id, page_id)
);

insert into ocr_page_results_new (
    result_id, ocr_run_id, page_id, state, working_tree_revision_id,
    error_code, error_message, created_at, updated_at)
select
    result_id, ocr_run_id, page_id, state, staging_tree_revision_id,
    error_code, error_message, created_at, updated_at
from ocr_page_results;

drop table ocr_page_results;

alter table ocr_page_results_new rename to ocr_page_results;

create index idx_ocr_page_results_run_id on ocr_page_results(ocr_run_id);
create index idx_ocr_page_results_page_id on ocr_page_results(page_id);

pragma foreign_keys = on;