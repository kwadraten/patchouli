-- Canonicalize page coordinate basis. Rows written before this migration carry the legacy
-- 'normalized' spelling; the OCR text-layer pipeline only consumes 'normalized_page' and the
-- import path now stamps that constant, so upgrade the stored value in place.
update pages set coordinate_basis = 'normalized_page' where coordinate_basis = 'normalized';
