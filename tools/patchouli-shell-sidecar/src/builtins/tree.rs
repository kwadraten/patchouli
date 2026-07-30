use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::{json, Value};

use super::DomainBuiltins;

const WALK_LIMIT: u32 = 10_000;
const MAX_DEPTH: u32 = 20;

pub struct TreeBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[derive(Debug, PartialEq, Eq)]
struct TreeArgs {
    path: String,
    max_depth: u32,
}

#[async_trait]
impl Builtin for TreeBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if ctx.args.iter().any(|arg| arg == "--help") {
            return Ok(ExecResult::ok("usage: tree [-L N] [path]\n"));
        }
        let parsed = match parse_args(ctx.args) {
            Ok(parsed) => parsed,
            Err(error) => return Ok(ExecResult::err(format!("tree: {error}\n"), 1)),
        };
        let request_path = resolve_path(ctx.cwd, &parsed.path);
        match self
            .domain
            .rpc
            .call(
                "vfs.walk",
                json!({
                    "path": request_path,
                    "max_depth": parsed.max_depth,
                    "limit": WALK_LIMIT,
                }),
            )
            .await
        {
            Ok(response) => render_response(&response, &parsed.path),
            Err(error) => Ok(ExecResult::err(
                format!("tree: {}: {error}\n", parsed.path),
                1,
            )),
        }
    }
}

fn parse_args(args: &[String]) -> std::result::Result<TreeArgs, String> {
    let mut path = None;
    let mut max_depth = MAX_DEPTH;
    let mut index = 0;
    let mut options = true;
    while index < args.len() {
        let arg = &args[index];
        if options && arg == "--" {
            options = false;
            index += 1;
            continue;
        }
        if options && arg == "-L" {
            let value = args
                .get(index + 1)
                .ok_or_else(|| "missing argument to '-L'".to_string())?;
            max_depth = value
                .parse::<u32>()
                .ok()
                .filter(|depth| *depth <= MAX_DEPTH)
                .ok_or_else(|| format!("-L must be an integer from 0 to {MAX_DEPTH}"))?;
            index += 2;
            continue;
        }
        if options && arg.starts_with('-') {
            return Err(format!("unsupported option '{arg}'"));
        }
        if path.replace(arg.clone()).is_some() {
            return Err("only one path is supported".to_string());
        }
        index += 1;
    }
    Ok(TreeArgs {
        path: path.unwrap_or_else(|| ".".to_string()),
        max_depth,
    })
}

fn resolve_path(cwd: &std::path::Path, path: &str) -> String {
    if path.starts_with('/') || path.contains("://") {
        path.replace('\\', "/")
    } else {
        cwd.join(path).to_string_lossy().replace('\\', "/")
    }
}

fn render_response(response: &Value, lexical_root: &str) -> Result<ExecResult> {
    let entries = response
        .get("entries")
        .and_then(Value::as_array)
        .ok_or_else(|| bashkit::Error::from(std::io::Error::other("invalid vfs.walk response")))?;
    let mut stdout = format!("{lexical_root}\n");
    let mut directories = 0;
    let mut files = 0;
    for entry in entries.iter().skip(1) {
        let depth = entry.get("depth").and_then(Value::as_u64).unwrap_or(0) as usize;
        let name = entry.get("name").and_then(Value::as_str).unwrap_or("");
        stdout.push_str(&"    ".repeat(depth.saturating_sub(1)));
        stdout.push_str("|-- ");
        stdout.push_str(name);
        stdout.push('\n');
        if entry.get("kind").and_then(Value::as_str) == Some("directory") {
            directories += 1;
        } else {
            files += 1;
        }
    }
    stdout.push('\n');
    stdout.push_str(&format!("{directories} directories, {files} files\n"));

    if response.get("truncated").and_then(Value::as_bool) == Some(true) {
        return Ok(ExecResult {
            stdout,
            stderr: format!("tree: results truncated after {WALK_LIMIT} entries\n"),
            exit_code: 1,
            ..Default::default()
        });
    }
    Ok(ExecResult::ok(stdout))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| value.to_string()).collect()
    }

    #[test]
    fn parses_depth_and_rejects_other_flags() {
        assert_eq!(
            parse_args(&strings(&["-L", "2", "./texts"])).unwrap(),
            TreeArgs {
                path: "./texts".to_string(),
                max_depth: 2,
            }
        );
        assert!(parse_args(&strings(&["-a"])).is_err());
        assert!(parse_args(&strings(&["a", "b"])).is_err());
    }

    #[test]
    fn formats_flat_entries_as_an_indented_tree() {
        let response = json!({
            "entries": [
                { "name": "texts", "kind": "directory", "depth": 0 },
                { "name": "doc", "kind": "directory", "depth": 1 },
                { "name": "page-1.md", "kind": "file", "depth": 2 }
            ],
            "truncated": false
        });
        let result = render_response(&response, "./texts").unwrap();
        assert_eq!(result.exit_code, 0);
        assert_eq!(
            result.stdout,
            "./texts\n|-- doc\n    |-- page-1.md\n\n1 directories, 1 files\n"
        );
    }
}
