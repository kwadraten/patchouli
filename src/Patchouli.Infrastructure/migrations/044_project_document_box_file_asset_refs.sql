create table file_asset_payload_refs (
    tree_revision_id text not null,
    box_id text not null,
    file_asset_id text not null,
    primary key (tree_revision_id, box_id)
) without rowid;

create index idx_file_asset_payload_refs_asset
    on file_asset_payload_refs (file_asset_id, tree_revision_id);

insert or ignore into file_asset_payload_refs (tree_revision_id, box_id, file_asset_id)
select
    b.tree_revision_id,
    b.box_id,
    case
        when json_valid(b.payload_json) then lower(coalesce(
            json_extract(b.payload_json, '$.assetId'),
            json_extract(b.payload_json, '$.AssetId')))
    end
from document_boxes b
join document_tree_revisions r on r.tree_revision_id = b.tree_revision_id
where r.status in ('working', 'committed')
  and case
      when json_valid(b.payload_json) then coalesce(
          json_type(b.payload_json, '$.assetId'),
          json_type(b.payload_json, '$.AssetId'))
  end = 'text'
  and case
      when json_valid(b.payload_json) then coalesce(
          json_extract(b.payload_json, '$.assetId'),
          json_extract(b.payload_json, '$.AssetId'))
  end <> '';

create trigger document_boxes_file_asset_refs_after_insert
after insert on document_boxes
begin
    insert or ignore into file_asset_payload_refs (tree_revision_id, box_id, file_asset_id)
    select
        new.tree_revision_id,
        new.box_id,
        case
            when json_valid(new.payload_json) then lower(coalesce(
                json_extract(new.payload_json, '$.assetId'),
                json_extract(new.payload_json, '$.AssetId')))
        end
    from document_tree_revisions r
    where r.tree_revision_id = new.tree_revision_id
      and r.status in ('working', 'committed')
      and case
          when json_valid(new.payload_json) then coalesce(
              json_type(new.payload_json, '$.assetId'),
              json_type(new.payload_json, '$.AssetId'))
      end = 'text'
      and case
          when json_valid(new.payload_json) then coalesce(
              json_extract(new.payload_json, '$.assetId'),
              json_extract(new.payload_json, '$.AssetId'))
      end <> '';
end;

create trigger document_boxes_file_asset_refs_after_update
after update of tree_revision_id, box_id, payload_json on document_boxes
begin
    delete from file_asset_payload_refs
    where tree_revision_id = old.tree_revision_id
      and box_id = old.box_id;

    insert or ignore into file_asset_payload_refs (tree_revision_id, box_id, file_asset_id)
    select
        new.tree_revision_id,
        new.box_id,
        case
            when json_valid(new.payload_json) then lower(coalesce(
                json_extract(new.payload_json, '$.assetId'),
                json_extract(new.payload_json, '$.AssetId')))
        end
    from document_tree_revisions r
    where r.tree_revision_id = new.tree_revision_id
      and r.status in ('working', 'committed')
      and case
          when json_valid(new.payload_json) then coalesce(
              json_type(new.payload_json, '$.assetId'),
              json_type(new.payload_json, '$.AssetId'))
      end = 'text'
      and case
          when json_valid(new.payload_json) then coalesce(
              json_extract(new.payload_json, '$.assetId'),
              json_extract(new.payload_json, '$.AssetId'))
      end <> '';
end;

create trigger document_boxes_file_asset_refs_after_delete
after delete on document_boxes
begin
    delete from file_asset_payload_refs
    where tree_revision_id = old.tree_revision_id
      and box_id = old.box_id;
end;

create trigger document_tree_revisions_file_asset_refs_after_status_update
after update of status on document_tree_revisions
begin
    delete from file_asset_payload_refs
    where tree_revision_id = new.tree_revision_id;

    insert or ignore into file_asset_payload_refs (tree_revision_id, box_id, file_asset_id)
    select
        b.tree_revision_id,
        b.box_id,
        case
            when json_valid(b.payload_json) then lower(coalesce(
                json_extract(b.payload_json, '$.assetId'),
                json_extract(b.payload_json, '$.AssetId')))
        end
    from document_boxes b
    where b.tree_revision_id = new.tree_revision_id
      and new.status in ('working', 'committed')
      and case
          when json_valid(b.payload_json) then coalesce(
              json_type(b.payload_json, '$.assetId'),
              json_type(b.payload_json, '$.AssetId'))
      end = 'text'
      and case
          when json_valid(b.payload_json) then coalesce(
              json_extract(b.payload_json, '$.assetId'),
              json_extract(b.payload_json, '$.AssetId'))
      end <> '';
end;

create trigger document_tree_revisions_file_asset_refs_after_delete
after delete on document_tree_revisions
begin
    delete from file_asset_payload_refs
    where tree_revision_id = old.tree_revision_id;
end;
