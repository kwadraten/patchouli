-- Explicitly declared tags survive without any Item membership. Existing tags remain
-- discoverable through item_tag_memberships; this table is canonical snapshot data.
create table library_tag_definitions (
    library_id text not null references library_metadata(library_id),
    tag text not null check (length(trim(tag)) > 0),
    primary key (library_id, tag)
);
insert or ignore into library_tag_definitions (library_id, tag)
select distinct library_id, tag from item_tag_memberships where length(trim(tag)) > 0;
