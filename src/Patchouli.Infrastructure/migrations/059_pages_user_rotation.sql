-- Per-page user rotation: a clockwise quarter turn the PDF workbench persists on the
-- page itself (displayed orientation for preview, images/ fetch and interactive OCR input).
-- The existing rotation column keeps its source-intrinsic /Rotate metadata role; page
-- geometry (boxes) stays in the unrotated document basis either way.
alter table pages add column user_rotation integer not null default 0
    check (user_rotation in (0, 90, 180, 270));
