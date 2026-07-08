create table if not exists blocking_operations (
    operation_id text primary key,
    operation_type text not null,
    scope_type text not null,
    scope_id text null,
    status text not null,
    progress_current integer null,
    progress_total integer null,
    progress_label text null,
    can_cancel integer not null default 0,
    failure_code text null,
    failure_message text null,
    next_actions_json text not null default '[]',
    created_at text not null,
    updated_at text not null
);

create index if not exists idx_blocking_operations_scope
    on blocking_operations(scope_type, scope_id, status, created_at);

create table if not exists blocking_operation_log_entries (
    entry_id text primary key,
    operation_id text not null,
    level text not null,
    message text not null,
    detail text null,
    scope_type text null,
    scope_id text null,
    created_at text not null,
    foreign key(operation_id) references blocking_operations(operation_id) on delete cascade
);

create index if not exists idx_blocking_operation_logs_operation
    on blocking_operation_log_entries(operation_id, created_at);
