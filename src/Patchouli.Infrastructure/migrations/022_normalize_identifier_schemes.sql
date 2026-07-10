delete from item_identifiers
where identifier_id in (
    select duplicate.identifier_id
    from item_identifiers duplicate
    join item_identifiers retained
      on retained.item_id = duplicate.item_id
     and lower(trim(retained.scheme)) = lower(trim(duplicate.scheme))
     and retained.value = duplicate.value
     and (
         retained.created_at < duplicate.created_at
         or (retained.created_at = duplicate.created_at and retained.identifier_id < duplicate.identifier_id)
     )
);

update item_identifiers
set scheme = lower(trim(scheme));

drop index if exists ux_item_identifiers_normalized;

create unique index ux_item_identifiers_normalized
on item_identifiers(item_id, scheme collate nocase, value);

create trigger if not exists trg_item_identifiers_lowercase_insert
after insert on item_identifiers
when new.scheme <> lower(trim(new.scheme))
begin
    update item_identifiers
    set scheme = lower(trim(new.scheme))
    where identifier_id = new.identifier_id;
end;

create trigger if not exists trg_item_identifiers_lowercase_update
after update of scheme on item_identifiers
when new.scheme <> lower(trim(new.scheme))
begin
    update item_identifiers
    set scheme = lower(trim(new.scheme))
    where identifier_id = new.identifier_id;
end;
