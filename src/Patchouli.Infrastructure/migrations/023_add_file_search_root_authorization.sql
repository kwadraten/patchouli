alter table file_search_roots add column authorization_kind text null;
alter table file_search_roots add column authorization_payload blob null;
alter table file_search_roots add column authorization_payload_version integer null;
alter table file_search_roots add column authorization_updated_at text null;

update file_search_roots
set authorization_kind = 'none',
    authorization_updated_at = updated_at
where authorization_kind is null;
