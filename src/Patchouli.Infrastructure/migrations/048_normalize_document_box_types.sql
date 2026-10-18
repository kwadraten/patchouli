-- Derived source hashes and search projections must be recomputed with the normalized model.
update search_units set status='stale' where status='current' and page_id in (
    select page_id from document_boxes where box_type in
        ('equation_interline','formula','page_header','page_footer','page_aside_text','algorithm')
        or (box_type='code' and sub_type='algorithm'));
update page_translations set source_tree_revision_id='';

-- The runner checks for and removes the historical base_type column transactionally.
-- Fresh schemas never create it. All revisions retain their IDs, geometry and order.
update document_boxes
set payload_json = json_object('latex', coalesce(
        json_extract(payload_json, '$.latex'),
        trim(json_extract(payload_json, '$.markdown'), '$' || char(10) || char(13) || ' '), ''), 'hasImage', json('true')),
    box_type = 'equation', sub_type = null
where box_type in ('equation_interline', 'formula') and (payload_json is null or json_valid(payload_json));

update document_boxes set box_type = case box_type
    when 'page_header' then 'header' when 'page_footer' then 'footer'
    when 'page_aside_text' then 'aside_text' else box_type end
where box_type in ('page_header','page_footer','page_aside_text');

update document_boxes
set payload_json = json_object('markdown', coalesce(json_extract(payload_json, '$.markdown'),
        json_extract(payload_json, '$.code'), '')),
    box_type = 'code', sub_type = 'algorithm'
where (box_type = 'algorithm' or (box_type = 'code' and sub_type = 'algorithm'))
  and json_valid(payload_json);

update document_boxes set sub_type = 'code' where box_type = 'code' and sub_type is null;

update document_boxes set payload_json = json_set(payload_json, '$.hasImage', json('true'))
where box_type='equation' and json_valid(payload_json) and coalesce(json_extract(payload_json, '$.hasImage'),0)=0;
