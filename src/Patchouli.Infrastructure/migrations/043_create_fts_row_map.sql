-- Preserve the existing local FTS cache while assigning every row an explicit mapping.
-- Duplicate or malformed FTS rows are disposable derived state; keep one usable row per
-- SearchUnit before enforcing the stable unit mapping.
delete from search_units_fts
where unit_id is null
   or length(trim(unit_id)) = 0
   or document_instance_id is null
   or length(trim(document_instance_id)) = 0
   or page_id is null
   or length(trim(page_id)) = 0;

delete from search_units_fts
where rowid not in (
    select min(rowid)
    from search_units_fts
    group by unit_id
);

create table if not exists fts_row_map (
    fts_row_id integer primary key autoincrement,
    document_instance_id text not null,
    unit_id text not null
);

create unique index if not exists idx_fts_row_map_unit_id
    on fts_row_map(unit_id);
create index if not exists idx_fts_row_map_document_instance_id
    on fts_row_map(document_instance_id, fts_row_id);

insert into fts_row_map (fts_row_id, document_instance_id, unit_id)
select rowid, document_instance_id, unit_id
from search_units_fts;

-- A missing row means a local cache was intentionally omitted during snapshot restore.
-- The first scoped operation can then perform one controlled rebuild and persist readiness.
create table if not exists fts_cache_state (
    state_id integer primary key check (state_id = 1),
    cache_version integer not null
);

insert into fts_cache_state (state_id, cache_version)
values (1, 1);
