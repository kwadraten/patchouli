-- Target language of the stored translation (issue #20).
-- The skip-or-retranslate decision compares the requested target language with this column, so
-- switching languages forces a fresh translation instead of reusing rows written for another
-- language. The column is nullable because rows written before this migration have an unknown
-- target language; they count as stale as soon as a specific language is requested. Realignment
-- keeps the value: it only re-anchors rows to a newer source revision.
alter table page_translations add column target_language text;
