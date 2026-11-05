-- Index known file locations by path and file asset ID to accelerate file registration
-- and import impact queries without scanning document instances or file locations.
create index if not exists idx_known_file_locations_path_file_asset_id
    on known_file_locations (path, file_asset_id);
