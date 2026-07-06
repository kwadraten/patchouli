create table if not exists item_creators (
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
    check (role in ('author', 'editor', 'translator', 'container-author')),
    check (sequence_index >= 0),
    check (
        length(trim(coalesce(family, '') || coalesce(given, '') || coalesce(literal, ''))) > 0
    )
);

create table if not exists item_dates (
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
    check (role in ('issued', 'accessed', 'original-date')),
    check (circa in (0, 1)),
    check (length(trim(date_parts_json)) > 0)
);

create index if not exists idx_item_creators_item_role_sequence
    on item_creators(item_id, role, sequence_index);

create index if not exists idx_item_dates_item_role
    on item_dates(item_id, role);
