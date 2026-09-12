-- One-level user playlists per Library with many-to-many Item membership.
-- Collections never own Items; dissolving a collection removes only membership rows.
create table if not exists collections (
    collection_id text primary key not null,
    library_id text not null,
    name text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    unique (library_id, name),
    check (length(trim(name)) > 0)
);

create index if not exists idx_collections_library_id on collections(library_id);

create table if not exists item_collections (
    collection_id text not null,
    item_id text not null,
    added_at text not null,
    primary key (collection_id, item_id),
    foreign key (collection_id) references collections(collection_id) on delete cascade,
    foreign key (item_id) references items(item_id) on delete cascade
);

create index if not exists idx_item_collections_item_id on item_collections(item_id);

-- Item-collection membership is now the single authority. Clear the legacy JSON mirror so no
-- read path can observe stale membership; the column is retained for snapshot compatibility.
update items
set collections_json = '[]'
where collections_json is not null
  and collections_json <> '[]';
