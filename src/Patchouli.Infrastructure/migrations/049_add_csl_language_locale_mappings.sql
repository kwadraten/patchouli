alter table csl_settings
    add column language_locale_mappings_json text not null
        default '{"zh":"zh-CN","ja":"zh-CN","ko":"zh-CN"}';
