-- Rebuildable tag projection; ordinal preserves legacy duplicate tags.
create table item_tag_memberships (
    item_id text not null references items(item_id) on delete cascade,
    ordinal integer not null,
    tag text not null,
    library_id text not null,
    is_active integer not null,
    primary key (item_id, ordinal)
);
create index idx_item_tags_tag_active_item on item_tag_memberships(tag, is_active, item_id);
create index idx_item_tags_active_tag on item_tag_memberships(is_active, tag);
create index idx_item_tags_library_active_tag on item_tag_memberships(library_id, is_active, tag);
insert into item_tag_memberships
select i.item_id, cast(t.key as integer), t.value, i.library_id,
       i.deleted_at is null and i.merged_into_item_id is null
from items i, json_each(case when json_valid(i.tags_json) then i.tags_json else '[]' end) t
where json_type(case when json_valid(i.tags_json) then i.tags_json else '[]' end) = 'array' and t.type = 'text';
create trigger item_tags_insert after insert on items begin
    insert into item_tag_memberships
    select new.item_id, cast(t.key as integer), t.value, new.library_id,
           new.deleted_at is null and new.merged_into_item_id is null
    from json_each(case when json_valid(new.tags_json) then new.tags_json else '[]' end) t
    where json_type(case when json_valid(new.tags_json) then new.tags_json else '[]' end) = 'array' and t.type = 'text';
end;
create trigger item_tags_update after update of tags_json, library_id, deleted_at, merged_into_item_id on items begin
    delete from item_tag_memberships where item_id = old.item_id;
    insert into item_tag_memberships
    select new.item_id, cast(t.key as integer), t.value, new.library_id,
           new.deleted_at is null and new.merged_into_item_id is null
    from json_each(case when json_valid(new.tags_json) then new.tags_json else '[]' end) t
    where json_type(case when json_valid(new.tags_json) then new.tags_json else '[]' end) = 'array' and t.type = 'text';
end;
create trigger item_tags_delete after delete on items begin
    delete from item_tag_memberships where item_id = old.item_id;
end;
