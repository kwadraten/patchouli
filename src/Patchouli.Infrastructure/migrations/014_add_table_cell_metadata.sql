alter table layout_nodes add column row_index integer null;
alter table layout_nodes add column col_index integer null;
alter table layout_nodes add column row_span integer null;
alter table layout_nodes add column col_span integer null;
alter table layout_nodes add column is_header integer not null default 0;

create index if not exists idx_layout_nodes_table_cell_position
    on layout_nodes(revision_id, page_id, parent_node_id, row_index, col_index)
    where node_type = 'table_cell';
