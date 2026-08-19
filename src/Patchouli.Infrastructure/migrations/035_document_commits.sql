create table if not exists document_commits (
    commit_id text primary key not null,
    document_instance_id text not null,
    parent_commit_id text null,
    source text not null,
    message text null,
    created_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (parent_commit_id) references document_commits(commit_id),
    check (length(trim(source)) > 0)
);

create table if not exists document_commit_pages (
    commit_id text not null,
    page_id text not null,
    tree_revision_id text not null,
    primary key (commit_id, page_id),
    foreign key (commit_id) references document_commits(commit_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (tree_revision_id) references document_tree_revisions(tree_revision_id)
);

create index idx_document_commits_document_instance_id on document_commits(document_instance_id);
create index idx_document_commit_pages_tree_revision_id on document_commit_pages(tree_revision_id);