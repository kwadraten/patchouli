use std::collections::{BTreeMap, HashSet};
use std::io::{self, Read, Write};
use std::process::ExitCode;

use biblatex::{
    Bibliography, Chunk, ChunksExt, Date, DateValue, Entry, EntryType, PermissiveType, Person,
    RawBibliography, RetrievalError, Spanned,
};
use serde::{Deserialize, Serialize};

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(code) => code,
    }
}

fn run() -> Result<(), ExitCode> {
    let mut stdin = io::stdin();
    let mut stdout = io::stdout();
    let mut stderr = io::stderr();

    let mut input = String::new();
    if let Err(error) = stdin.read_to_string(&mut input) {
        let _ = writeln!(stderr, "failed to read stdin: {error}");
        return Err(ExitCode::from(2));
    }

    let request: Request = match serde_json::from_str(&input) {
        Ok(value) => value,
        Err(error) => {
            write_json(
                &mut stdout,
                &Response::error("invalid_request", format!("invalid JSON request: {error}")),
            )?;
            return Err(ExitCode::from(1));
        }
    };

    let response = match request {
        Request::Parse { text } => parse_bibliography(&text),
        Request::Write { entries } => write_bibliography(&entries),
    };

    write_json(&mut stdout, &response)?;
    if response.ok {
        Ok(())
    } else {
        Err(ExitCode::from(1))
    }
}

fn write_json(stdout: &mut impl Write, value: &Response) -> Result<(), ExitCode> {
    match serde_json::to_writer(stdout, value) {
        Ok(()) => Ok(()),
        Err(error) => {
            let _ = writeln!(io::stderr(), "failed to write stdout: {error}");
            Err(ExitCode::from(2))
        }
    }
}

fn parse_bibliography(text: &str) -> Response {
    // Standard Bib(La)TeX already parses upstream, so a document that succeeds
    // on the first, unmodified attempt is never rewritten. The compatibility
    // pass exists to accept loose database exports: entries without a citation
    // key and the `(` entry delimiter that `biblatex` 0.12.0 does not accept.
    //
    // Missing keys are syntactically ambiguous with the rare citation key that
    // contains `=`. The raw grammar is the authority there, and the fallback
    // keeps its interpretation whenever it can parse the candidate as a valid
    // keyed entry. A failed retry still reports the original parse error as
    // well as the retry error so callers can see both.
    match Bibliography::parse(text) {
        Ok(bibliography) => success_response(&bibliography),
        Err(original) => {
            let original_message = original.to_string();
            let Some(normalized) = normalize_bibtex_compatible(text) else {
                return Response::error("parse_failed", original_message);
            };

            match Bibliography::parse(&normalized.text) {
                Ok(bibliography) => success_response(&bibliography),
                Err(retry) => Response::error(
                    "parse_failed",
                    format!(
                        "{original_message}; BibTeX compatibility retry ({} generated key(s), {} converted delimiter(s)) failed: {retry}",
                        normalized.injected_keys, normalized.converted_entries
                    ),
                ),
            }
        }
    }
}

fn success_response(bibliography: &Bibliography) -> Response {
    let entries = bibliography.iter().map(entry_to_dto).collect::<Vec<_>>();

    Response {
        ok: true,
        error: None,
        entries: Some(entries),
        text: None,
    }
}

/// Result of the BibTeX compatibility rewrite.
struct CompatNormalization {
    text: String,
    injected_keys: usize,
    converted_entries: usize,
}

/// Rewrites a bibliography that uses the wider BibTeX surface into the subset
/// `biblatex` 0.12.0 accepts:
///
/// * a normal entry whose body starts with `field = ...` is missing its
///   citation key, so a unique temporary key is injected — unless the entry
///   already parses as a legal keyed entry under the raw grammar, which covers
///   the rare citation key containing `=`;
/// * an entry opened with `(` is rewritten to the brace delimiter;
/// * a standard bibtex `@string(...)` / `@preamble(...)` directive is rewritten
///   to braces while `@comment(...)` is left untouched, because its body is
///   arbitrary and may contain braces a rewrite would misread.
///
/// The scan is structural: nested field values, quoted strings, escapes and
/// comments are skipped. An entry whose braces cannot be proven balanced after
/// the rewrite (for example a stray `}` inside a parenthesized entry) abandons
/// the whole pass with `None`, preserving the original parse error instead of
/// silently truncating the entry. Returns `None` when nothing needed to change,
/// so the caller can preserve the original parse error.
fn normalize_bibtex_compatible(text: &str) -> Option<CompatNormalization> {
    let mut result = String::with_capacity(text.len());
    let mut injected_keys = 0usize;
    let mut converted_entries = 0usize;
    let mut next_key_ordinal = 1usize;
    let reserved_key_ordinals = collect_reserved_key_ordinals(text);
    let mut i = 0usize;

    while i < text.len() {
        let current = text[i..].chars().next().expect("cursor is in bounds");
        if current == '%' {
            let start = i;
            while i < text.len() {
                let c = text[i..].chars().next().expect("cursor is in bounds");
                i += c.len_utf8();
                if c == '\n' {
                    break;
                }
            }
            result.push_str(&text[start..i]);
            continue;
        }

        if current != '@' {
            result.push(current);
            i += current.len_utf8();
            continue;
        }

        let type_start = i + 1;
        let mut type_end = type_start;
        if let Some(first) = text[type_start..]
            .chars()
            .next()
            .filter(|c| is_id_start(*c))
        {
            type_end += first.len_utf8();
            while let Some(next) = text[type_end..].chars().next() {
                if !is_id_continue(next) {
                    break;
                }
                type_end += next.len_utf8();
            }
        }

        if type_end == type_start {
            result.push('@');
            i += 1;
            continue;
        }

        let entry_type = &text[type_start..type_end];
        let mut open_index = type_end;
        while let Some(next) = text[open_index..].chars().next() {
            if !next.is_whitespace() {
                break;
            }
            open_index += next.len_utf8();
        }

        let open = match text[open_index..].chars().next() {
            Some('{') => '{',
            Some('(') => '(',
            _ => {
                // Not an entry opening; keep scanning after the '@'.
                result.push('@');
                i += 1;
                continue;
            }
        };
        let close = if open == '{' { '}' } else { ')' };

        let end = match find_entry_end(text, open_index, open, close) {
            EntryEnd::Terminated(end) => end,
            EntryEnd::Unterminated => {
                // Unterminated entry: copy the remainder unchanged.
                result.push_str(&text[i..]);
                break;
            }
            EntryEnd::Unbalanced => {
                // Rewriting this entry to braces could truncate it and drop
                // fields, so leave the whole source untouched and keep the
                // original parse error.
                return None;
            }
        };

        let directive = entry_type.to_ascii_lowercase();
        let is_directive = matches!(directive.as_str(), "string" | "preamble" | "comment");
        let body_start = open_index + open.len_utf8();
        let body = &text[body_start..end];
        // `@comment(...)` is deliberately not rewritten: its body is arbitrary
        // and a brace rewrite could terminate on content braces.
        let convert_delimiter = open == '(' && directive != "comment";
        let missing_key = !is_directive
            && entry_body_starts_with_field(text, body_start)
            && !parses_as_keyed_raw_entry(&text[i..open_index], body);

        if !missing_key && !convert_delimiter {
            result.push_str(&text[i..=end]);
            i = end + close.len_utf8();
            continue;
        }

        // Copy the header up to the opening delimiter, then always emit a brace
        // opening and the injected key when one is missing.
        result.push_str(&text[i..open_index]);
        result.push('{');
        if missing_key {
            let key = generate_compat_key(&reserved_key_ordinals, &mut next_key_ordinal);
            result.push_str(&key);
            result.push(',');
            injected_keys += 1;
        }
        result.push_str(body);
        result.push('}');
        if convert_delimiter {
            converted_entries += 1;
        }
        i = end + close.len_utf8();
    }

    if injected_keys == 0 && converted_entries == 0 {
        None
    } else {
        Some(CompatNormalization {
            text: result,
            injected_keys,
            converted_entries,
        })
    }
}

/// Where an entry opened at `open_index` ends.
enum EntryEnd {
    /// The closing delimiter is at this byte index.
    Terminated(usize),
    /// The input ended before the closing delimiter.
    Unterminated,
    /// A `}` was found at brace depth zero inside a parenthesized entry, so
    /// rewriting it to braces could close the entry early.
    Unbalanced,
}

/// Parses one candidate entry with the upstream raw grammar. That grammar is
/// the authority for the keyless / `=`-in-key ambiguity: when the body already
/// forms a valid keyed entry it must not receive a temporary key.
fn parses_as_keyed_raw_entry(header: &str, body: &str) -> bool {
    let candidate = format!("{header}{{{body}}}");
    matches!(
        RawBibliography::parse(&candidate),
        Ok(parsed) if parsed.entries.len() == 1 && !parsed.entries[0].v.key.v.is_empty()
    )
}

/// Finds the index of the delimiter that closes the entry opened at
/// `open_index`. Field values are skipped so braces and parentheses inside them
/// cannot close the entry early. A brace that would unbalance the rewritten
/// brace entry is reported as [`EntryEnd::Unbalanced`].
fn find_entry_end(text: &str, open_index: usize, open: char, close: char) -> EntryEnd {
    let mut brace_depth = 0usize;
    let mut i = open_index + open.len_utf8();

    while i < text.len() {
        let Some(c) = text[i..].chars().next() else {
            break;
        };
        match c {
            '\\' => {
                i += c.len_utf8();
                if let Some(escaped) = text[i..].chars().next() {
                    i += escaped.len_utf8();
                }
            }
            '%' if brace_depth == 0 => {
                while i < text.len() {
                    let Some(cc) = text[i..].chars().next() else {
                        break;
                    };
                    i += cc.len_utf8();
                    if cc == '\n' {
                        break;
                    }
                }
            }
            '"' if brace_depth == 0 => {
                i += c.len_utf8();
                while i < text.len() {
                    let Some(cc) = text[i..].chars().next() else {
                        break;
                    };
                    if cc == '\\' {
                        i += cc.len_utf8();
                        if let Some(escaped) = text[i..].chars().next() {
                            i += escaped.len_utf8();
                        }
                        continue;
                    }
                    i += cc.len_utf8();
                    if cc == '"' {
                        break;
                    }
                }
            }
            '{' => {
                brace_depth += 1;
                i += c.len_utf8();
            }
            '}' => {
                if brace_depth == 0 {
                    if close == '}' {
                        return EntryEnd::Terminated(i);
                    }
                    // A stray closing brace inside a parenthesized entry would
                    // terminate the brace rewrite early.
                    return EntryEnd::Unbalanced;
                }
                brace_depth -= 1;
                i += c.len_utf8();
            }
            ')' if open == '(' && brace_depth == 0 => return EntryEnd::Terminated(i),
            _ => i += c.len_utf8(),
        }
    }

    EntryEnd::Unterminated
}

/// True when the first key-value pair directly follows the opening delimiter,
/// i.e. the entry is missing its citation key.
fn entry_body_starts_with_field(text: &str, start: usize) -> bool {
    let mut i = skip_whitespace_and_comments(text, start);
    let Some(first) = text[i..].chars().next().filter(|c| is_id_start(*c)) else {
        return false;
    };
    i += first.len_utf8();
    while let Some(next) = text[i..].chars().next() {
        if !is_id_continue(next) {
            break;
        }
        i += next.len_utf8();
    }

    i = skip_whitespace_and_comments(text, i);
    text[i..].chars().next() == Some('=')
}

fn skip_whitespace_and_comments(text: &str, start: usize) -> usize {
    let mut i = start;
    while let Some(c) = text[i..].chars().next() {
        if c.is_whitespace() {
            i += c.len_utf8();
            continue;
        }
        if c == '%' {
            while let Some(cc) = text[i..].chars().next() {
                i += cc.len_utf8();
                if cc == '\n' {
                    break;
                }
            }
            continue;
        }
        break;
    }
    i
}

/// Collects the ordinals of `patchouli-import-<n>` keys already present in the
/// source in a single pass, so key generation never rescans the whole document.
fn collect_reserved_key_ordinals(text: &str) -> HashSet<usize> {
    const PREFIX: &str = "patchouli-import-";
    let mut reserved = HashSet::new();
    let mut search = 0usize;
    while let Some(offset) = text[search..].find(PREFIX) {
        let digits_start = search + offset + PREFIX.len();
        let rest = &text[digits_start..];
        let digits_end = rest
            .find(|c: char| !c.is_ascii_digit())
            .unwrap_or(rest.len());
        if digits_end > 0 {
            if let Ok(ordinal) = rest[..digits_end].parse::<usize>() {
                reserved.insert(ordinal);
            }
        }
        search = digits_start;
    }
    reserved
}

/// Generates a temporary import key that is unique among the source keys and
/// among the keys injected for this document. Patchouli assigns the real
/// citation key when it creates the item.
fn generate_compat_key(reserved: &HashSet<usize>, ordinal: &mut usize) -> String {
    loop {
        let candidate = *ordinal;
        *ordinal += 1;
        if !reserved.contains(&candidate) {
            return format!("patchouli-import-{candidate}");
        }
    }
}

/// Mirrors the upstream identifier rules used for entry types and field names.
fn is_id_continue(c: char) -> bool {
    !matches!(
        c,
        '@' | '{' | '}' | '"' | '#' | '\'' | '(' | ')' | ',' | '=' | '%' | '\\' | '~'
    ) && !c.is_control()
        && !c.is_whitespace()
}

fn is_id_start(c: char) -> bool {
    !matches!(c, ':' | '<' | '-' | '>') && is_id_continue(c)
}

fn write_bibliography(entries: &[WriteEntryDto]) -> Response {
    let mut parts = Vec::with_capacity(entries.len());
    for entry in entries {
        match build_entry(entry) {
            Ok(built) => {
                let mut text = built.to_biblatex_string();
                // The upstream writer downgrades extension types to misc.
                if let EntryType::Unknown(name) = &built.entry_type {
                    if let Some(header_end) = text.find('{') {
                        text.replace_range(1..header_end, name);
                    }
                }
                parts.push(text);
            }
            Err(message) => return Response::error("write_failed", message),
        }
    }

    Response {
        ok: true,
        error: None,
        entries: None,
        text: Some(parts.join("\n")),
    }
}

fn entry_to_dto(entry: &Entry) -> EntryDto {
    let report = entry.verify();
    let mut fields = BTreeMap::new();
    for (key, chunks) in &entry.fields {
        fields.insert(key.clone(), chunks.format_verbatim());
    }

    EntryDto {
        key: entry.key.clone(),
        entry_type: match &entry.entry_type {
            EntryType::Unknown(name) => name.to_ascii_lowercase(),
            known => known.to_string().to_ascii_lowercase(),
        },
        is_xdata: matches!(entry.entry_type, EntryType::XData),
        fields,
        persons: collect_persons(entry),
        dates: collect_dates(entry),
        keywords: entry
            .keywords()
            .ok()
            .map(|chunks| split_keywords(&chunks.format_verbatim()))
            .unwrap_or_default(),
        file: match entry.file() {
            Ok(path) if !path.trim().is_empty() => Some(path),
            _ => None,
        },
        verify_ok: report.is_ok(),
        verify: VerifyDto {
            missing: report
                .missing
                .iter()
                .map(|value| (*value).to_string())
                .collect(),
            superfluous: report
                .superfluous
                .iter()
                .map(|value| (*value).to_string())
                .collect(),
            malformed: report
                .malformed
                .iter()
                .map(|(field, error)| MalformedDto {
                    field: field.clone(),
                    message: error.to_string(),
                })
                .collect(),
        },
    }
}

fn collect_persons(entry: &Entry) -> BTreeMap<String, Vec<PersonDto>> {
    let mut map = BTreeMap::new();
    push_persons(&mut map, "author", entry.author());
    push_persons(&mut map, "translator", entry.translator());
    push_persons(&mut map, "bookauthor", entry.book_author());

    // Keep each source group separate; editors() already combines editor/a/b/c.
    push_persons(&mut map, "editor", entry.get_as::<Vec<Person>>("editor"));

    const ADDITIONAL_ROLES: &[&str] = &[
        "director",
        "producer",
        "composer",
        "performer",
        "interviewer",
        "recipient",
        "script-writer",
        "scriptwriter",
        "writer",
        "original-author",
        "originalauthor",
        "origauthor",
        "organizer",
        "reviewed-author",
        "reviewedauthor",
        "holder",
        "annotator",
        "commentator",
        "editora",
        "editorb",
        "editorc",
    ];

    for &role in ADDITIONAL_ROLES {
        if !map.contains_key(role) && entry.fields.contains_key(role) {
            push_persons(&mut map, role, entry.get_as::<Vec<Person>>(role));
        }
    }

    map
}

fn push_persons(
    map: &mut BTreeMap<String, Vec<PersonDto>>,
    role: &str,
    result: Result<Vec<Person>, RetrievalError>,
) {
    if let Ok(people) = result {
        let values = people.into_iter().map(person_to_dto).collect::<Vec<_>>();
        if !values.is_empty() {
            map.insert(role.to_string(), values);
        }
    }
}

fn person_to_dto(person: Person) -> PersonDto {
    let family = empty_to_none(person.name.clone());
    let given = empty_to_none(person.given_name.clone());
    let prefix = empty_to_none(person.prefix.clone());
    let suffix = empty_to_none(person.suffix.clone());
    let literal = if family.is_none() && given.is_none() && prefix.is_none() && suffix.is_none() {
        empty_to_none(person.to_string())
    } else {
        None
    };

    PersonDto {
        family,
        given,
        prefix,
        suffix,
        literal,
    }
}

fn collect_dates(entry: &Entry) -> BTreeMap<String, DateDto> {
    let mut map = BTreeMap::new();
    push_date(&mut map, "date", entry.date());
    push_date(&mut map, "urldate", entry.url_date());
    push_date(&mut map, "origdate", entry.orig_date());
    if entry.fields.contains_key("eventdate") {
        push_date(
            &mut map,
            "eventdate",
            entry.get_as::<PermissiveType<Date>>("eventdate"),
        );
    }
    map
}

fn push_date(
    map: &mut BTreeMap<String, DateDto>,
    key: &str,
    result: Result<PermissiveType<Date>, RetrievalError>,
) {
    match result {
        Ok(PermissiveType::Typed(date)) => {
            map.insert(key.to_string(), date_to_dto(&date));
        }
        Ok(PermissiveType::Chunks(chunks)) => {
            let literal = chunks.format_verbatim();
            if !literal.trim().is_empty() {
                map.insert(
                    key.to_string(),
                    DateDto {
                        years: Vec::new(),
                        parts: Vec::new(),
                        literal: Some(literal),
                        circa: false,
                    },
                );
            }
        }
        Err(_) => {}
    }
}

fn date_to_dto(date: &Date) -> DateDto {
    let mut years = Vec::new();
    let mut parts = Vec::new();
    match &date.value {
        DateValue::At(time) | DateValue::After(time) | DateValue::Before(time) => {
            push_datetime(&mut years, &mut parts, time);
        }
        DateValue::Between(start, end) => {
            push_datetime(&mut years, &mut parts, start);
            push_datetime(&mut years, &mut parts, end);
        }
    }

    years.sort_unstable();
    years.dedup();

    DateDto {
        years,
        parts,
        literal: None,
        circa: date.uncertain || date.approximate,
    }
}

fn push_datetime(years: &mut Vec<i32>, parts: &mut Vec<Vec<i32>>, time: &biblatex::Datetime) {
    years.push(time.year);
    let mut part = vec![time.year];
    if let Some(month) = time.month {
        part.push(i32::from(month) + 1);
        if let Some(day) = time.day {
            part.push(i32::from(day));
        }
    }
    parts.push(part);
}

fn build_entry(dto: &WriteEntryDto) -> Result<Entry, String> {
    if dto.key.trim().is_empty() {
        return Err("entry key is required".to_string());
    }

    let entry_type = EntryType::new(&dto.entry_type);
    let mut entry = Entry::new(dto.key.clone(), entry_type);

    for (key, value) in &dto.fields {
        if value.trim().is_empty() {
            continue;
        }
        entry.set(key, chunks_from_text(value));
    }

    for (role, people) in &dto.persons {
        let list: Vec<Person> = people.iter().map(person_from_dto).collect();
        if list.is_empty() {
            continue;
        }
        match role.as_str() {
            "author" => entry.set_author(list),
            "translator" => entry.set_translator(list),
            "bookauthor" => entry.set_book_author(list),
            other => entry.set_as(other, &list),
        }
    }

    if !dto.keywords.is_empty() {
        let joined = dto.keywords.join(", ");
        entry.set_keywords(chunks_from_text(&joined));
    }

    Ok(entry)
}

fn person_from_dto(person: &PersonDto) -> Person {
    if let Some(literal) = person
        .literal
        .as_ref()
        .filter(|value| !value.trim().is_empty())
    {
        return Person {
            name: literal.clone(),
            given_name: String::new(),
            prefix: String::new(),
            suffix: String::new(),
            id: None,
            prefix_initials: None,
            given_initials: None,
            use_prefix: None,
        };
    }

    Person {
        name: person.family.clone().unwrap_or_default(),
        given_name: person.given.clone().unwrap_or_default(),
        prefix: person.prefix.clone().unwrap_or_default(),
        suffix: person.suffix.clone().unwrap_or_default(),
        id: None,
        prefix_initials: None,
        given_initials: None,
        use_prefix: None,
    }
}

fn chunks_from_text(value: &str) -> Vec<Spanned<Chunk>> {
    vec![Spanned::detached(Chunk::Normal(value.to_string()))]
}

fn empty_to_none(value: String) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

fn split_keywords(value: &str) -> Vec<String> {
    value
        .split([',', ';'])
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .map(ToOwned::to_owned)
        .collect()
}

#[derive(Debug, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
enum Request {
    Parse { text: String },
    Write { entries: Vec<WriteEntryDto> },
}

#[derive(Debug, Serialize)]
struct Response {
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<ErrorDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    entries: Option<Vec<EntryDto>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    text: Option<String>,
}

impl Response {
    fn error(code: &str, message: impl Into<String>) -> Self {
        Self {
            ok: false,
            error: Some(ErrorDto {
                code: code.to_string(),
                message: message.into(),
            }),
            entries: None,
            text: None,
        }
    }
}

#[derive(Debug, Serialize)]
struct ErrorDto {
    code: String,
    message: String,
}

#[derive(Debug, Serialize)]
struct EntryDto {
    key: String,
    entry_type: String,
    is_xdata: bool,
    fields: BTreeMap<String, String>,
    persons: BTreeMap<String, Vec<PersonDto>>,
    dates: BTreeMap<String, DateDto>,
    keywords: Vec<String>,
    file: Option<String>,
    verify_ok: bool,
    verify: VerifyDto,
}

#[derive(Debug, Serialize, Deserialize)]
struct PersonDto {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    family: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    given: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    prefix: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    suffix: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    literal: Option<String>,
}

#[derive(Debug, Serialize)]
struct DateDto {
    years: Vec<i32>,
    parts: Vec<Vec<i32>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    literal: Option<String>,
    circa: bool,
}

#[derive(Debug, Serialize)]
struct VerifyDto {
    missing: Vec<String>,
    superfluous: Vec<String>,
    malformed: Vec<MalformedDto>,
}

#[derive(Debug, Serialize)]
struct MalformedDto {
    field: String,
    message: String,
}

#[derive(Debug, Deserialize)]
struct WriteEntryDto {
    key: String,
    entry_type: String,
    #[serde(default)]
    fields: BTreeMap<String, String>,
    #[serde(default)]
    persons: BTreeMap<String, Vec<PersonDto>>,
    #[serde(default)]
    keywords: Vec<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn editor_groups_are_collected_once() {
        let response = parse_bibliography(
            "@book{k,title={T},editor={Doe, Jane},editora={Smith, John},editorb={Lee, Ann},editorc={Li, Bo}}",
        );
        let entries = response.entries.unwrap();
        let people = &entries[0].persons;
        for role in ["editor", "editora", "editorb", "editorc"] {
            assert_eq!(people[role].len(), 1);
        }
        assert_eq!(people.values().map(Vec::len).sum::<usize>(), 4);
    }

    #[test]
    fn extension_types_survive_parse_write_parse() {
        for kind in ["movie", "artwork", "unpublish", "custom-type"] {
            let parsed = parse_bibliography(&format!("@{kind}{{k,title={{T}}}}"));
            assert_eq!(parsed.entries.unwrap()[0].entry_type, kind);
            let written = write_bibliography(&[WriteEntryDto {
                key: "k".into(),
                entry_type: kind.into(),
                fields: BTreeMap::from([("title".into(), "T".into())]),
                persons: BTreeMap::new(),
                keywords: Vec::new(),
            }]);
            let text = written.text.unwrap();
            assert_eq!(
                parse_bibliography(&text).entries.unwrap()[0].entry_type,
                kind
            );
        }
    }

    #[test]
    fn canonical_original_author_is_a_person_list() {
        let parsed = parse_bibliography("@software{k,title={T},origauthor={Doe, Jane}}");
        let entries = parsed.entries.unwrap();
        let person = &entries[0].persons["origauthor"][0];
        assert_eq!(person.family.as_deref(), Some("Doe"));
        assert_eq!(person.given.as_deref(), Some("Jane"));
    }

    #[test]
    fn valid_bibtex_is_parsed_without_normalization() {
        let text = "@article{key,title={T},author={Doe, Jane},year={2020}}";
        assert!(normalize_bibtex_compatible(text).is_none());

        let parsed = parse_bibliography(text);
        assert!(parsed.ok);
        let entries = parsed.entries.unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].key, "key");
    }

    #[test]
    fn missing_citation_key_is_injected() {
        let parsed = parse_bibliography(
            "@phdthesis{\n  author = {Doe, Jane},\n  title = {A thesis},\n  year = {2021},\n}",
        );
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let entries = parsed.entries.unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].entry_type, "phdthesis");
        assert_eq!(entries[0].key, "patchouli-import-1");
        assert_eq!(entries[0].fields["title"], "A thesis");
    }

    #[test]
    fn multiple_keyless_entries_receive_unique_keys() {
        let parsed = parse_bibliography(
            "@book{ author={A}, title={One} }\n@article{ author={B}, title={Two} }",
        );
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let entries = parsed.entries.unwrap();
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].key, "patchouli-import-1");
        assert_eq!(entries[1].key, "patchouli-import-2");
    }

    #[test]
    fn generated_keys_avoid_keys_already_present_in_the_source() {
        let text = "@misc{patchouli-import-1,title={Existing}}\n@book{ title={Keyless} }";
        let parsed = parse_bibliography(text);
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let keys = parsed
            .entries
            .unwrap()
            .into_iter()
            .map(|entry| entry.key)
            .collect::<Vec<_>>();
        assert_eq!(keys, vec!["patchouli-import-1", "patchouli-import-2"]);
    }

    #[test]
    fn parenthesis_delimiters_are_accepted() {
        let parsed = parse_bibliography("@article(key, title={T}, note={a) b})");
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let entries = parsed.entries.unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].key, "key");
        assert_eq!(entries[0].fields["note"], "a) b");

        let keyless = parse_bibliography("@article(title={T}, year={2020})");
        let entries = keyless.entries.unwrap();
        assert_eq!(entries[0].key, "patchouli-import-1");
        assert_eq!(entries[0].fields["year"], "2020");
    }

    #[test]
    fn string_preamble_and_comment_directives_are_not_rewritten() {
        let text = "@string{jt=\"Journal of Tests\"}\n@preamble{\"\\\\foo\"}\n@comment{nothing}\n@article{ author={Doe, Jane}, journal=jt }";
        let normalization = normalize_bibtex_compatible(text).expect("keyless entry needs a key");
        assert_eq!(normalization.injected_keys, 1);
        assert_eq!(normalization.converted_entries, 0);
        assert!(normalization
            .text
            .contains("@string{jt=\"Journal of Tests\"}"));
        assert!(normalization.text.contains("@preamble{\"\\\\foo\"}"));
        assert!(normalization.text.contains("@comment{nothing}"));

        let parsed = parse_bibliography(text);
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let entries = parsed.entries.unwrap();
        assert_eq!(entries[0].fields["journal"], "Journal of Tests");
    }

    #[test]
    fn fields_nested_inside_values_are_not_mistaken_for_entries() {
        let text =
            "% @article{ fake, title={not an entry} }\n@book{ author={A}, note={see @article{ x = y }} }";
        let normalization = normalize_bibtex_compatible(text).expect("keyless entry needs a key");
        assert_eq!(normalization.injected_keys, 1);
        assert_eq!(normalization.converted_entries, 0);

        let parsed = parse_bibliography(text);
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let entries = parsed.entries.unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].entry_type, "book");
        // The nested `@article{ x = y }` text must stay untouched inside the field value.
        assert!(!entries[0].fields["note"].contains("patchouli-import"));
    }

    #[test]
    fn unrelated_syntax_errors_still_fail_without_a_compat_retry() {
        assert!(normalize_bibtex_compatible("@article{key title={T}}").is_none());

        let parsed = parse_bibliography("@article{key title={T}}");
        assert!(!parsed.ok);
        let message = parsed.error.unwrap().message;
        assert!(!message.contains("compatibility retry"), "{message}");
    }

    #[test]
    fn failed_compat_retry_reports_the_original_error_and_the_retry() {
        let parsed = parse_bibliography("@article{ title = }");
        assert!(!parsed.ok);
        let message = parsed.error.unwrap().message;
        assert!(message.contains("compatibility retry"), "{message}");
    }

    #[test]
    fn unbalanced_parenthesis_entry_is_not_rewritten() {
        // The stray `}` would close the rewritten brace entry early and drop
        // `title`, so the whole compatibility pass must back off.
        let text = "@article(key, note=123}extra, title={T})";
        assert!(normalize_bibtex_compatible(text).is_none());

        let parsed = parse_bibliography(text);
        assert!(!parsed.ok);
        let message = parsed.error.unwrap().message;
        assert!(!message.contains("compatibility retry"), "{message}");
    }

    #[test]
    fn keyed_entry_with_equals_in_key_survives_a_keyless_sibling() {
        let text = "@article{a=2020, title={T}}\n@book{ author={Doe}, title={Keyless} }";
        let parsed = parse_bibliography(text);
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));

        let entries = parsed.entries.unwrap();
        assert_eq!(entries.len(), 2);
        // `a=2020` is a legal raw key, so the entry must not be renamed.
        assert_eq!(entries[0].key, "a=2020");
        assert_eq!(entries[0].fields["title"], "T");
        assert_eq!(entries[1].key, "patchouli-import-1");
        assert_eq!(entries[1].fields["title"], "Keyless");
    }

    #[test]
    fn string_and_preamble_parenthesis_directives_are_converted() {
        let text =
            "@string(jt=\"Journal of Tests\")\n@preamble(\"\\foo\")\n@article{key, journal=jt}";
        let normalization =
            normalize_bibtex_compatible(text).expect("parenthesis directives need rewriting");
        assert_eq!(normalization.injected_keys, 0);
        assert_eq!(normalization.converted_entries, 2);
        assert!(normalization
            .text
            .contains("@string{jt=\"Journal of Tests\"}"));
        assert!(normalization.text.contains("@preamble{\"\\foo\"}"));

        let parsed = parse_bibliography(text);
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let entries = parsed.entries.unwrap();
        assert_eq!(entries[0].fields["journal"], "Journal of Tests");
    }

    #[test]
    fn comment_parenthesis_directive_is_not_blindly_rewritten() {
        assert!(normalize_bibtex_compatible("@comment(anything)").is_none());
        assert!(!parse_bibliography("@comment(anything)").ok);
    }

    #[test]
    fn generated_keys_skip_reserved_ordinals() {
        let text = "@misc{patchouli-import-1,title={One}}\n@misc{patchouli-import-3,title={Three}}\n@book{ title={Keyless A} }\n@book{ title={Keyless B} }";
        let parsed = parse_bibliography(text);
        assert!(parsed.ok, "{:?}", parsed.error.map(|error| error.message));
        let keys = parsed
            .entries
            .unwrap()
            .into_iter()
            .map(|entry| entry.key)
            .collect::<Vec<_>>();
        assert_eq!(
            keys,
            vec![
                "patchouli-import-1",
                "patchouli-import-3",
                "patchouli-import-2",
                "patchouli-import-4"
            ]
        );
    }
}
