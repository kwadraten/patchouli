create table if not exists item_type_inferences (
    inference_id text primary key not null,
    item_id text not null,
    suggested_type text not null,
    confidence real not null,
    source text not null,
    evidence_summary text null,
    created_at text not null,
    accepted_at text null,
    foreign key (item_id) references items(item_id) on delete cascade
);

create index if not exists idx_item_type_inferences_item_created
    on item_type_inferences(item_id, created_at desc);
