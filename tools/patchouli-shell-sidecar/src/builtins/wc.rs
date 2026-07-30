use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::{json, Value};

use super::DomainBuiltins;
use crate::limits::MAX_STRING_BYTES;

const MAX_READ_BATCH: usize = 64;
const MAX_FILE_OPERANDS: usize = 256;
const HELP: &str = "Usage: wc [OPTION]... [FILE]...\nPrint newline, word, and byte counts for each FILE.\n\n  -l, --lines\t\t\tprint the newline counts\n  -w, --words\t\t\tprint the word counts\n  -c, --bytes\t\t\tprint the byte counts\n  -m, --chars\t\t\tprint the character counts\n  -L, --max-line-length\t\tprint the maximum line length\n  --help\t\t\tdisplay this help and exit\n  --version\t\t\toutput version information and exit\n";

pub struct WcBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[derive(Clone, Copy, Default)]
struct WcFlags {
    lines: bool,
    words: bool,
    bytes: bool,
    chars: bool,
    max_line_length: bool,
}

impl WcFlags {
    fn active_count(self) -> usize {
        [
            self.lines,
            self.words,
            self.bytes,
            self.chars,
            self.max_line_length,
        ]
        .into_iter()
        .filter(|enabled| *enabled)
        .count()
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
struct TextCounts {
    lines: usize,
    words: usize,
    bytes: usize,
    chars: usize,
    max_line_length: usize,
}

impl TextCounts {
    fn add(&mut self, other: Self) {
        self.lines += other.lines;
        self.words += other.words;
        self.bytes += other.bytes;
        self.chars += other.chars;
        self.max_line_length = self.max_line_length.max(other.max_line_length);
    }
}

#[async_trait]
impl Builtin for WcBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if ctx.args.iter().any(|arg| arg == "--help") {
            return Ok(ExecResult::ok(HELP));
        }
        if ctx.args.iter().any(|arg| arg == "--version") {
            return Ok(ExecResult::ok("wc (bashkit) 0.1\n"));
        }

        let (flags, operands) = match parse_args(ctx.args) {
            Ok(parsed) => parsed,
            Err(option) => {
                let message = option.strip_prefix("--").map_or_else(
                    || format!("wc: invalid option -- '{option}'\n"),
                    |long| format!("wc: unrecognized option '--{long}'\n"),
                );
                return Ok(ExecResult::err(message, 1));
            }
        };
        if operands.len() > MAX_FILE_OPERANDS {
            return Ok(ExecResult::err(
                format!("wc: at most {MAX_FILE_OPERANDS} file operands are supported\n"),
                2,
            ));
        }

        if operands.is_empty() {
            let Some(stdin) = ctx.stdin else {
                return Ok(ExecResult::ok(""));
            };
            let counts = count_text(stdin);
            return Ok(ExecResult::ok(format!(
                "{}\n",
                format_counts(counts, flags, None, flags.active_count() > 1)
            )));
        }

        let resolved: Vec<Option<String>> = operands
            .iter()
            .map(|operand| (operand != "-").then(|| resolve_operand(ctx.cwd, operand.as_str())))
            .collect();
        let distinct = distinct_paths(&resolved);

        let mut reads = HashMap::new();
        for chunk in distinct.chunks(MAX_READ_BATCH) {
            let payload = json!({ "paths": chunk });
            match self.domain.rpc.call("vfs.read_batch", payload).await {
                Ok(response) => {
                    collect_batch_results(chunk, &response, &mut reads);
                    let retained_bytes: usize = reads
                        .values()
                        .filter_map(|read| read.as_ref().ok())
                        .map(String::len)
                        .sum();
                    if retained_bytes > MAX_STRING_BYTES {
                        return Ok(ExecResult::err(
                            format!("wc: input exceeds {MAX_STRING_BYTES} byte command budget\n"),
                            124,
                        ));
                    }
                }
                Err(error) => {
                    for path in chunk {
                        reads.insert(path.clone(), Err(error.to_string()));
                    }
                }
            }
        }

        Ok(render_files(flags, &operands, &resolved, ctx.stdin, &reads))
    }
}

fn parse_args(args: &[String]) -> std::result::Result<(WcFlags, Vec<String>), String> {
    let mut flags = WcFlags::default();
    let mut operands = Vec::new();
    let mut options = true;
    for arg in args {
        if options && arg == "--" {
            options = false;
            continue;
        }
        if !options || arg == "-" || !arg.starts_with('-') {
            operands.push(arg.clone());
            continue;
        }
        match arg.as_str() {
            "--lines" => flags.lines = true,
            "--words" => flags.words = true,
            "--bytes" => flags.bytes = true,
            "--chars" => flags.chars = true,
            "--max-line-length" => flags.max_line_length = true,
            value if value.starts_with("--") => return Err(value.to_string()),
            value => {
                for option in value[1..].chars() {
                    match option {
                        'l' => flags.lines = true,
                        'w' => flags.words = true,
                        'c' => flags.bytes = true,
                        'm' => flags.chars = true,
                        'L' => flags.max_line_length = true,
                        other => return Err(other.to_string()),
                    }
                }
            }
        }
    }
    if flags.active_count() == 0 {
        flags.lines = true;
        flags.words = true;
        flags.bytes = true;
    }
    Ok((flags, operands))
}

fn resolve_operand(cwd: &std::path::Path, operand: &str) -> String {
    if operand.starts_with('/') || operand.contains("://") {
        return operand.replace('\\', "/");
    }
    cwd.join(operand).to_string_lossy().replace('\\', "/")
}

fn distinct_paths(resolved: &[Option<String>]) -> Vec<String> {
    let mut distinct = Vec::new();
    let mut seen = HashSet::new();
    for path in resolved.iter().flatten() {
        if seen.insert(path.clone()) {
            distinct.push(path.clone());
        }
    }
    distinct
}

fn collect_batch_results(
    requested: &[String],
    response: &Value,
    reads: &mut HashMap<String, std::result::Result<String, String>>,
) {
    let results = response.get("results").and_then(Value::as_array);
    for path in requested {
        let result = results.and_then(|items| {
            items
                .iter()
                .find(|item| item.get("path").and_then(Value::as_str) == Some(path))
        });
        let read = match result {
            Some(item) if item.get("ok").and_then(Value::as_bool) == Some(true) => item
                .pointer("/value/content")
                .and_then(Value::as_str)
                .map(str::to_owned)
                .ok_or_else(|| "vfs.read_batch returned no content".to_string()),
            Some(item) => Err(item
                .pointer("/error/message")
                .and_then(Value::as_str)
                .unwrap_or("file could not be read")
                .to_string()),
            None => Err("vfs.read_batch returned no result".to_string()),
        };
        reads.insert(path.clone(), read);
    }
}

fn render_files(
    flags: WcFlags,
    operands: &[String],
    resolved: &[Option<String>],
    stdin: Option<&str>,
    reads: &HashMap<String, std::result::Result<String, String>>,
) -> ExecResult {
    let mut stdout = String::new();
    let mut stderr = String::new();
    let mut total = TextCounts::default();
    for (operand, path) in operands.iter().zip(resolved) {
        let content = match path {
            None => Ok(stdin.unwrap_or("")),
            Some(path) => reads
                .get(path)
                .map(|read| read.as_deref().map_err(String::as_str))
                .unwrap_or_else(|| Err("vfs.read_batch returned no result")),
        };
        match content {
            Ok(content) => {
                let counts = count_text(content);
                total.add(counts);
                stdout.push_str(&format_counts(counts, flags, Some(operand), true));
                stdout.push('\n');
            }
            Err(error) => stderr.push_str(&format!("wc: {operand}: {error}\n")),
        }
    }
    if operands.len() > 1 {
        stdout.push_str(&format_counts(total, flags, Some("total"), true));
        stdout.push('\n');
    }
    let exit_code = i32::from(!stderr.is_empty());
    ExecResult {
        stdout,
        stderr,
        exit_code,
        ..Default::default()
    }
}

fn count_text(text: &str) -> TextCounts {
    TextCounts {
        lines: text.chars().filter(|character| *character == '\n').count(),
        words: text.split_whitespace().count(),
        bytes: text.len(),
        chars: text.chars().count(),
        max_line_length: text
            .lines()
            .map(|line| line.chars().count())
            .max()
            .unwrap_or(0),
    }
}

fn format_counts(
    counts: TextCounts,
    flags: WcFlags,
    filename: Option<&str>,
    padded: bool,
) -> String {
    let mut values = Vec::new();
    if flags.lines {
        values.push(counts.lines);
    }
    if flags.words {
        values.push(counts.words);
    }
    if flags.bytes {
        values.push(counts.bytes);
    }
    if flags.chars {
        values.push(counts.chars);
    }
    if flags.max_line_length {
        values.push(counts.max_line_length);
    }
    let output = values
        .iter()
        .map(|value| {
            if padded {
                format!("{value:>7}")
            } else {
                value.to_string()
            }
        })
        .collect::<Vec<_>>()
        .join(" ");
    filename.map_or(output.clone(), |name| format!("{output} {name}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn flags(options: &[&str]) -> WcFlags {
        parse_args(
            &options
                .iter()
                .map(|value| value.to_string())
                .collect::<Vec<_>>(),
        )
        .unwrap()
        .0
    }

    #[test]
    fn counts_unicode_bytes_chars_and_line_length() {
        assert_eq!(
            count_text("héllo 世界\nlonger\n"),
            TextCounts {
                lines: 2,
                words: 3,
                bytes: 21,
                chars: 16,
                max_line_length: 8,
            }
        );
    }

    #[test]
    fn renders_multiple_files_and_total() {
        let operands = vec!["a".to_string(), "b".to_string()];
        let resolved = vec![Some("/a".to_string()), Some("/b".to_string())];
        let reads = HashMap::from([
            ("/a".to_string(), Ok("one two\n".to_string())),
            ("/b".to_string(), Ok("three\n".to_string())),
        ]);
        let result = render_files(flags(&[]), &operands, &resolved, None, &reads);
        assert_eq!(result.exit_code, 0);
        assert_eq!(
            result.stdout,
            "      1       2       8 a\n      1       1       6 b\n      2       3      14 total\n"
        );
    }

    #[test]
    fn preserves_mixed_errors_and_duplicate_operands() {
        let operands = vec!["a".to_string(), "missing".to_string(), "a".to_string()];
        let resolved = vec![
            Some("/a".to_string()),
            Some("/missing".to_string()),
            Some("/a".to_string()),
        ];
        let reads = HashMap::from([
            ("/a".to_string(), Ok("x\n".to_string())),
            ("/missing".to_string(), Err("not found".to_string())),
        ]);
        let result = render_files(flags(&["-l"]), &operands, &resolved, None, &reads);
        assert_eq!(result.exit_code, 1);
        assert_eq!(result.stderr, "wc: missing: not found\n");
        assert_eq!(result.stdout, "      1 a\n      1 a\n      2 total\n");
    }

    #[test]
    fn parses_read_batch_response_contract() {
        let requested = vec!["/ok".to_string(), "/bad".to_string()];
        let response = json!({ "results": [
            { "path": "/ok", "ok": true, "value": { "content": "text" }, "error": null },
            { "path": "/bad", "ok": false, "value": null, "error": { "message": "denied" } }
        ] });
        let mut reads = HashMap::new();
        collect_batch_results(&requested, &response, &mut reads);
        assert_eq!(reads["/ok"].as_deref(), Ok("text"));
        assert_eq!(
            reads["/bad"].as_deref().map_err(String::as_str),
            Err("denied")
        );
    }

    #[test]
    fn distinct_reads_are_chunked_at_contract_limit() {
        let mut resolved = (0..129)
            .map(|index| Some(format!("/{index}")))
            .collect::<Vec<_>>();
        resolved.extend([Some("/0".to_string()), None, Some("/64".to_string())]);
        let paths = distinct_paths(&resolved);
        assert_eq!(paths.len(), 129);
        assert_eq!(
            paths
                .chunks(MAX_READ_BATCH)
                .map(<[_]>::len)
                .collect::<Vec<_>>(),
            [64, 64, 1]
        );
    }
}
