create table if not exists pages (
    page_id text primary key not null,
    document_instance_id text not null,
    page_index integer not null,
    page_label text null,
    width real null,
    height real null,
    rotation integer not null default 0,
    coordinate_basis text not null,
    basis_width real null,
    basis_height real null,
    renderer_basis_version text not null,
    source_file_hash text null,
    created_at text not null,
    updated_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    unique (document_instance_id, page_index),
    check (page_index >= 0),
    check (rotation in (0, 90, 180, 270))
);

create table if not exists layout_revisions (
    layout_revision_id text primary key not null,
    document_instance_id text not null,
    parent_revision_id text null,
    source text not null,
    is_current integer not null default 0,
    created_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (parent_revision_id) references layout_revisions(layout_revision_id),
    check (is_current in (0, 1))
);

create table if not exists layout_nodes (
    node_id text primary key not null,
    document_instance_id text not null,
    page_id text not null,
    parent_node_id text null,
    node_type text not null,
    bbox_x real null,
    bbox_y real null,
    bbox_width real null,
    bbox_height real null,
    own_text text null,
    text_policy text not null,
    reading_order integer not null,
    source text not null,
    revision_id text not null,
    confidence real null,
    ignored integer not null default 0,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (parent_node_id) references layout_nodes(node_id) on delete cascade,
    foreign key (revision_id) references layout_revisions(layout_revision_id) on delete cascade,
    check (ignored in (0, 1)),
    check (
        (bbox_x is null and bbox_y is null and bbox_width is null and bbox_height is null)
        or (
            bbox_x is not null and bbox_y is not null and bbox_width is not null and bbox_height is not null
            and bbox_x >= 0 and bbox_x <= 1
            and bbox_y >= 0 and bbox_y <= 1
            and bbox_width > 0 and bbox_width <= 1
            and bbox_height > 0 and bbox_height <= 1
            and bbox_x + bbox_width <= 1
            and bbox_y + bbox_height <= 1
        )
    )
);

create index if not exists idx_pages_document_instance_id on pages(document_instance_id);
create index if not exists idx_layout_revisions_document_instance_id on layout_revisions(document_instance_id);
create index if not exists idx_layout_nodes_page_id_revision_id on layout_nodes(page_id, revision_id);
create index if not exists idx_layout_nodes_parent_node_id on layout_nodes(parent_node_id);
create index if not exists idx_layout_nodes_document_instance_id_revision_id on layout_nodes(document_instance_id, revision_id);
