create table if not exists ocr_presets (
    preset_id text primary key not null,
    library_id text not null,
    name text not null,
    description text null,
    current_version_id text null,
    archived integer not null default 0,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    check (archived in (0, 1)),
    check (length(trim(name)) > 0)
);

create table if not exists ocr_preset_versions (
    preset_version_id text primary key not null,
    preset_id text not null,
    engine_id text not null,
    model_id text not null,
    model_path text null,
    parameters_json text not null,
    apply_on_success integer not null,
    created_at text not null,
    foreign key (preset_id) references ocr_presets(preset_id) on delete cascade,
    check (apply_on_success in (0, 1))
);

create table if not exists ocr_runs (
    ocr_run_id text primary key not null,
    document_instance_id text not null,
    preset_id text not null,
    preset_version_id text not null,
    engine_id text not null,
    model_id text not null,
    parameters_snapshot_json text not null,
    source_revision_id text null,
    output_revision_id text null,
    retry_of_run_id text null,
    state text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (preset_id) references ocr_presets(preset_id),
    foreign key (preset_version_id) references ocr_preset_versions(preset_version_id),
    foreign key (source_revision_id) references layout_revisions(layout_revision_id),
    foreign key (output_revision_id) references layout_revisions(layout_revision_id),
    foreign key (retry_of_run_id) references ocr_runs(ocr_run_id)
);

create table if not exists ocr_page_results (
    result_id text primary key not null,
    ocr_run_id text not null,
    page_id text not null,
    state text not null,
    staging_layout_revision_id text null,
    error_code text null,
    error_message text null,
    created_at text not null,
    updated_at text not null,
    foreign key (ocr_run_id) references ocr_runs(ocr_run_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (staging_layout_revision_id) references layout_revisions(layout_revision_id),
    unique (ocr_run_id, page_id)
);

create table if not exists ocr_candidate_adoptions (
    adoption_id text primary key not null,
    ocr_run_id text not null,
    document_instance_id text not null,
    adopted_revision_id text not null,
    adopted_pages_json text not null,
    created_at text not null,
    foreign key (ocr_run_id) references ocr_runs(ocr_run_id),
    foreign key (document_instance_id) references document_instances(document_instance_id),
    foreign key (adopted_revision_id) references layout_revisions(layout_revision_id)
);

create index if not exists idx_ocr_presets_library_id on ocr_presets(library_id);
create index if not exists idx_ocr_preset_versions_preset_id on ocr_preset_versions(preset_id);
create index if not exists idx_ocr_runs_document_instance_id on ocr_runs(document_instance_id);
create index if not exists idx_ocr_page_results_run_id on ocr_page_results(ocr_run_id);
create index if not exists idx_ocr_page_results_page_id on ocr_page_results(page_id);
