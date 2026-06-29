create table if not exists evidence_ref_records (
    evidence_record_id text primary key not null,
    evidence_ref_id text not null unique,
    library_id text not null,
    document_instance_id text not null,
    page_id text not null,
    unit_id text not null,
    text_revision_id text not null,
    bbox_revision_id text not null,
    layout_revision_id text not null,
    snapshot_id text null,
    pinned_text text not null,
    source_title text not null,
    page_label text null,
    page_index integer not null,
    status text not null,
    created_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    foreign key (document_instance_id) references document_instances(document_instance_id),
    foreign key (page_id) references pages(page_id),
    foreign key (unit_id) references search_units(unit_id),
    foreign key (layout_revision_id) references layout_revisions(layout_revision_id)
);

create table if not exists evidence_successors (
    predecessor_record_id text not null,
    successor_record_id text not null,
    reason text not null,
    created_at text not null,
    primary key (predecessor_record_id, successor_record_id),
    foreign key (predecessor_record_id) references evidence_ref_records(evidence_record_id),
    foreign key (successor_record_id) references evidence_ref_records(evidence_record_id)
);

create index if not exists idx_evidence_records_library_id on evidence_ref_records(library_id);
create index if not exists idx_evidence_records_unit_id on evidence_ref_records(unit_id);
create index if not exists idx_evidence_records_page_id on evidence_ref_records(page_id);
create index if not exists idx_evidence_records_status on evidence_ref_records(status);
create index if not exists idx_evidence_successors_predecessor on evidence_successors(predecessor_record_id);
create index if not exists idx_evidence_successors_successor on evidence_successors(successor_record_id);
