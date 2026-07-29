-- Expand the creator-role and date-role CHECK constraints. SQLite cannot alter
-- CHECK constraints, so both tables are rebuilt: create new tables with the
-- expanded role sets, copy all rows, drop the old tables, rename, and restore
-- the indexes.

create table item_creators_new (
    creator_id text primary key not null,
    item_id text not null,
    role text not null,
    family text null,
    given text null,
    literal text null,
    suffix text null,
    particles text null,
    sequence_index integer not null,
    created_at text not null,
    foreign key (item_id) references items(item_id) on delete cascade,
    check (role in (
        'author', 'editor', 'translator', 'container-author',
        'host', 'producer', 'director', 'composer', 'performer',
        'interviewer', 'recipient', 'script-writer', 'original-author',
        'organizer', 'reviewed-author')),
    check (sequence_index >= 0),
    check (
        length(trim(coalesce(family, '') || coalesce(given, '') || coalesce(literal, ''))) > 0
    )
);

insert into item_creators_new (
    creator_id, item_id, role, family, given, literal, suffix, particles, sequence_index, created_at
)
select creator_id, item_id, role, family, given, literal, suffix, particles, sequence_index, created_at
from item_creators;

drop table item_creators;
alter table item_creators_new rename to item_creators;

create index if not exists idx_item_creators_item_role_sequence
    on item_creators(item_id, role, sequence_index);

create table item_dates_new (
    date_id text primary key not null,
    item_id text not null,
    role text not null,
    date_parts_json text not null default '[]',
    circa integer not null default 0,
    season text null,
    literal text null,
    created_at text not null,
    foreign key (item_id) references items(item_id) on delete cascade,
    unique (item_id, role),
    check (role in ('issued', 'accessed', 'original-date', 'event-date', 'submitted')),
    check (circa in (0, 1)),
    check (length(trim(date_parts_json)) > 0)
);

insert into item_dates_new (
    date_id, item_id, role, date_parts_json, circa, season, literal, created_at
)
select date_id, item_id, role, date_parts_json, circa, season, literal, created_at
from item_dates;

drop table item_dates;
alter table item_dates_new rename to item_dates;

create index if not exists idx_item_dates_item_role
    on item_dates(item_id, role);
