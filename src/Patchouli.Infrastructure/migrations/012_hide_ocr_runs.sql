alter table ocr_runs add column hidden integer not null default 0;

create index if not exists idx_ocr_runs_hidden on ocr_runs(hidden);
