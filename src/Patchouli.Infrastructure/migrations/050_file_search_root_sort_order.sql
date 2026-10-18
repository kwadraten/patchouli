-- Issue #11: stable user-defined ordering for file search roots. The first root in
-- this order is the move target of the move-to-first-search-root file handling
-- behavior; the order must never rely on accidental DB ordering.
alter table file_search_root_definitions add column sort_order integer not null default 0;

-- Backfill: preserve the historical insertion order (oldest first) for existing rows
-- that never had an explicit order.
update file_search_root_definitions as target
set sort_order = (
    select count(*)
    from file_search_root_definitions as older
    where older.library_id = target.library_id
      and (older.created_at < target.created_at
           or (older.created_at = target.created_at and older.root_id < target.root_id))
);

create index if not exists idx_file_search_root_definitions_library_sort
    on file_search_root_definitions(library_id, sort_order, created_at, root_id);
