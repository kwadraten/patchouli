-- Full-text translations are derived data over the OCR box tree. They are stored per box and
-- compiled into a whole-page markdown on demand, mirroring how page markdown is compiled.
-- Translations always track the latest committed tree revision: when the tree changes, rows
-- whose source content changed (or whose box disappeared) are dropped during realignment.
create table if not exists page_translations (
    page_id text primary key not null,
    -- Revision the stored rows were aligned against. Deliberately not a foreign key: old
    -- revisions may be purged, and a stale value simply triggers lazy realignment.
    source_tree_revision_id text not null,
    -- Incremented on every put/realign; part of the compiler cache key.
    version integer not null default 1,
    updated_at text not null,
    foreign key (page_id) references pages(page_id) on delete cascade,
    check (version >= 1)
);

create table if not exists translation_boxes (
    page_id text not null,
    box_id text not null,
    -- Content-box order within the page, used to rebuild the compiled markdown deterministically.
    ordinal integer not null,
    -- Translated payload for one box. Titles store plain text, equations store LaTeX and code
    -- boxes store raw code; every other payload stores its markdown fragment verbatim.
    translated_md text not null,
    -- Hash of the source payload at write time; a mismatch marks the translation stale.
    source_hash text not null,
    primary key (page_id, box_id),
    foreign key (page_id) references page_translations(page_id) on delete cascade,
    check (ordinal >= 0)
);

create index if not exists idx_translation_boxes_page_ordinal on translation_boxes(page_id, ordinal);
