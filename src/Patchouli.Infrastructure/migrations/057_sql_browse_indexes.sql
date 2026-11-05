-- Partial indexes supporting active item browse, search, and keyset pagination.

-- MCP item browse orders active items by updated_at descending, then item_id.
create index if not exists idx_items_active_browse
    on items(deleted_at, merged_into_item_id, updated_at desc, item_id)
    where deleted_at is null and merged_into_item_id is null;

-- Keyset pagination for active library items orders by created_at descending, then item_id descending.
create index if not exists idx_items_library_active_created
    on items(library_id, created_at desc, item_id desc)
    where deleted_at is null and merged_into_item_id is null;
