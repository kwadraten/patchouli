alter table search_settings add column rewrite_enabled integer not null default 1 check (rewrite_enabled in (0,1));
