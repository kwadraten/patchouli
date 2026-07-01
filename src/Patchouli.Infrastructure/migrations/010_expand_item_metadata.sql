alter table items add column citation_key text null;
alter table items add column title_short text null;
alter table items add column container_title_short text null;
alter table items add column collection_title text null;
alter table items add column edition text null;
alter table items add column genre text null;
alter table items add column number text null;
alter table items add column chapter_number text null;
alter table items add column version text null;
alter table items add column status text null;
alter table items add column note text null;
alter table items add column deleted_at text null;

update items
set citation_key = lower(replace(item_id, '-', ''))
where citation_key is null or length(trim(citation_key)) = 0;

create unique index if not exists idx_items_citation_key on items(citation_key);
create index if not exists idx_items_deleted_at on items(deleted_at);

create trigger if not exists trg_items_assign_citation_key_after_insert
after insert on items
for each row
when new.citation_key is null or length(trim(new.citation_key)) = 0
begin
    update items
    set citation_key = lower(replace(new.item_id, '-', ''))
    where rowid = new.rowid;
end;
