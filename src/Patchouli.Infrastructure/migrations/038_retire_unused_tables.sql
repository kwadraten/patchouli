-- Retire unused library column preferences and the root table migrated by 027.
drop table if exists library_preferences;
drop table if exists file_search_roots;
