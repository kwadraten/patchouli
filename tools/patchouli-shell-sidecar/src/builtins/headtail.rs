use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::{json, Value};

use super::DomainBuiltins;

const MAX_LINES: u32 = 1000;
const MAX_FILE_OPERANDS: usize = 64;

pub struct HeadTailBuiltin {
    pub domain: Arc<DomainBuiltins>,
    pub mode: &'static str,
}

#[derive(Debug, PartialEq, Eq)]
struct HeadTailArgs {
    count: u32,
    files: Vec<String>,
}

#[async_trait]
impl Builtin for HeadTailBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if ctx.args.iter().any(|arg| arg == "--help") {
            return Ok(ExecResult::ok(format!(
                "usage: {} [-n N|--lines N] [FILE...]\n",
                self.mode
            )));
        }
        let parsed = match parse_args(ctx.args) {
            Ok(parsed) => parsed,
            Err(error) => return Ok(ExecResult::err(format!("{}: {error}\n", self.mode), 1)),
        };
        if parsed.files.len() > MAX_FILE_OPERANDS {
            return Ok(ExecResult::err(
                format!(
                    "{}: at most {MAX_FILE_OPERANDS} file operands are supported\n",
                    self.mode
                ),
                2,
            ));
        }
        if parsed.files.is_empty() {
            return Ok(ExecResult::ok(slice_stdin(
                ctx.stdin.unwrap_or(""),
                parsed.count as usize,
                self.mode,
            )));
        }

        let multiple = parsed.files.len() > 1;
        let mut stdout = String::new();
        let mut stderr = String::new();
        for (index, file) in parsed.files.iter().enumerate() {
            let result = if file == "-" {
                Ok(slice_stdin(
                    ctx.stdin.unwrap_or(""),
                    parsed.count as usize,
                    self.mode,
                ))
            } else {
                let path = resolve_path(ctx.cwd, file);
                match self
                    .domain
                    .rpc
                    .call(
                        "vfs.read_lines",
                        json!({ "path": path, "mode": self.mode, "count": parsed.count }),
                    )
                    .await
                {
                    Ok(response) => response
                        .get("content")
                        .and_then(Value::as_str)
                        .map(str::to_owned)
                        .ok_or_else(|| "invalid vfs.read_lines response".to_string()),
                    Err(error) => Err(error.to_string()),
                }
            };

            match result {
                Ok(content) => {
                    if multiple {
                        if index > 0 && !stdout.is_empty() {
                            stdout.push('\n');
                        }
                        let label = if file == "-" { "standard input" } else { file };
                        stdout.push_str(&format!("==> {label} <==\n"));
                    }
                    stdout.push_str(&content);
                }
                Err(error) => stderr.push_str(&format!("{}: {file}: {error}\n", self.mode)),
            }
        }
        Ok(ExecResult {
            stdout,
            exit_code: i32::from(!stderr.is_empty()),
            stderr,
            ..Default::default()
        })
    }
}

fn parse_args(args: &[String]) -> std::result::Result<HeadTailArgs, String> {
    let mut count = 10;
    let mut files = Vec::new();
    let mut options = true;
    let mut index = 0;
    while index < args.len() {
        let arg = &args[index];
        if options && arg == "--" {
            options = false;
            index += 1;
            continue;
        }
        if options && (arg == "-n" || arg == "--lines") {
            let value = args
                .get(index + 1)
                .ok_or_else(|| format!("missing argument to '{arg}'"))?;
            count = parse_count(value)?;
            index += 2;
            continue;
        }
        if options && arg.starts_with('-') && arg != "-" {
            return Err(format!("unsupported option '{arg}'"));
        }
        files.push(arg.clone());
        index += 1;
    }
    Ok(HeadTailArgs { count, files })
}

fn parse_count(value: &str) -> std::result::Result<u32, String> {
    value
        .parse::<u32>()
        .ok()
        .filter(|count| *count <= MAX_LINES)
        .ok_or_else(|| format!("line count must be an integer from 0 to {MAX_LINES}"))
}

fn resolve_path(cwd: &std::path::Path, path: &str) -> String {
    if path.starts_with('/') || path.contains("://") {
        path.replace('\\', "/")
    } else {
        cwd.join(path).to_string_lossy().replace('\\', "/")
    }
}

fn slice_stdin(content: &str, count: usize, mode: &str) -> String {
    if count == 0 {
        return String::new();
    }
    let lines = content.split_inclusive('\n').collect::<Vec<_>>();
    let selected = if mode == "head" {
        &lines[..lines.len().min(count)]
    } else {
        &lines[lines.len().saturating_sub(count)..]
    };
    selected.concat()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| value.to_string()).collect()
    }

    #[test]
    fn parses_default_count_multiple_files_and_stdin() {
        assert_eq!(
            parse_args(&strings(&["a", "-", "b"])).unwrap(),
            HeadTailArgs {
                count: 10,
                files: strings(&["a", "-", "b"]),
            }
        );
        assert_eq!(
            parse_args(&strings(&["--lines", "25", "a"])).unwrap().count,
            25
        );
    }

    #[test]
    fn rejects_unsupported_options_and_excessive_counts() {
        assert!(parse_args(&strings(&["-q", "a"])).is_err());
        assert!(parse_args(&strings(&["-n", "1001", "a"])).is_err());
        assert!(parse_args(&strings(&["-n"])).is_err());
    }

    #[test]
    fn slices_stdin_without_losing_line_endings() {
        assert_eq!(slice_stdin("a\nb\nc", 2, "head"), "a\nb\n");
        assert_eq!(slice_stdin("a\nb\nc", 2, "tail"), "b\nc");
        assert_eq!(slice_stdin("a\nb\n", 1, "tail"), "b\n");
    }
}
