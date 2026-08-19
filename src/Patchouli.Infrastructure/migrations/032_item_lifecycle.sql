alter table items add column merged_into_item_id text null;

create index if not exists idx_items_merged_into on items(merged_into_item_id);

create table if not exists item_purge_records (
    item_id text primary key,
    purged_at text not null,
    purge_reason text null,
    payload_summary_json text null
) without rowid;

create index if not exists idx_item_purge_records_purged_at on item_purge_records(purged_at);
