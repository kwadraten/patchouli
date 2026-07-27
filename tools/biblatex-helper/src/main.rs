use std::collections::BTreeMap;
use std::io::{self, Read, Write};
use std::process::ExitCode;

use biblatex::{
    Bibliography, Chunk, ChunksExt, Date, DateValue, Entry, EntryType, PermissiveType, Person,
    RetrievalError, Spanned,
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
    let bibliography = match Bibliography::parse(text) {
        Ok(value) => value,
        Err(error) => {
            return Response::error("parse_failed", error.to_string());
        }
    };

    let entries = bibliography
        .iter()
        .map(entry_to_dto)
        .collect::<Vec<_>>();

    Response {
        ok: true,
        error: None,
        entries: Some(entries),
        text: None,
    }
}

fn write_bibliography(entries: &[WriteEntryDto]) -> Response {
    let mut parts = Vec::with_capacity(entries.len());
    for entry in entries {
        match build_entry(entry) {
            Ok(built) => parts.push(built.to_biblatex_string()),
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
        entry_type: entry.entry_type.to_string().to_ascii_lowercase(),
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
            missing: report.missing.iter().map(|value| (*value).to_string()).collect(),
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

    if let Ok(editors) = entry.editors() {
        let mut people = Vec::new();
        for (group, _) in editors {
            people.extend(group.into_iter().map(person_to_dto));
        }
        if !people.is_empty() {
            map.insert("editor".to_string(), people);
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

    if let Some(people) = dto.persons.get("author") {
        entry.set_author(people.iter().map(person_from_dto).collect());
    }
    if let Some(people) = dto.persons.get("editor") {
        entry.set_as("editor", &people.iter().map(person_from_dto).collect::<Vec<_>>());
    }
    if let Some(people) = dto.persons.get("translator") {
        entry.set_translator(people.iter().map(person_from_dto).collect());
    }
    if let Some(people) = dto.persons.get("bookauthor") {
        entry.set_book_author(people.iter().map(person_from_dto).collect());
    }

    if !dto.keywords.is_empty() {
        let joined = dto.keywords.join(", ");
        entry.set_keywords(chunks_from_text(&joined));
    }

    Ok(entry)
}

fn person_from_dto(person: &PersonDto) -> Person {
    if let Some(literal) = person.literal.as_ref().filter(|value| !value.trim().is_empty()) {
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
