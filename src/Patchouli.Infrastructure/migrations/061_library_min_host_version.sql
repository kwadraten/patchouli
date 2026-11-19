-- The runner adds the column if absent, allowing preserved values from newer hosts.
update library_metadata
set min_host_version = '0.4.1'
where min_host_version is null or trim(min_host_version) = '';
