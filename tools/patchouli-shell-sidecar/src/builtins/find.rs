use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::{json, Value};

use super::DomainBuiltins;

const WALK_LIMIT: u32 = 10_000;
const MAX_DEPTH: u32 = 20;

pub struct FindBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[derive(Debug, PartialEq, Eq)]
struct FindArgs {
    path: String,
    max_depth: u32,
    kind: Option<&'static str>,
}

#[async_trait]
impl Builtin for FindBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if ctx.args.iter().any(|arg| arg == "--help") {
            return Ok(ExecResult::ok(
                "usage: find [path] [-maxdepth N] [-type f|d]\n",
            ));
        }

        let parsed = match parse_args(ctx.args) {
            Ok(parsed) => parsed,
            Err(error) => return Ok(ExecResult::err(format!("find: {error}\n"), 1)),
        };
        let request_path = resolve_path(ctx.cwd, &parsed.path);
        let mut payload = json!({
            "path": request_path,
            "max_depth": parsed.max_depth,
            "limit": WALK_LIMIT,
        });
        if let Some(kind) = parsed.kind {
            payload["type"] = json!(kind);
        }

        match self.domain.rpc.call("vfs.walk", payload).await {
            Ok(response) => render_response(&response, &parsed.path),
            Err(error) => Ok(ExecResult::err(
                format!("find: {}: {error}\n", parsed.path),
                1,
            )),
        }
    }
}

fn parse_args(args: &[String]) -> std::result::Result<FindArgs, String> {
    let mut path = None;
    let mut max_depth = MAX_DEPTH;
    let mut kind = None;
    let mut index = 0;
    let mut options = true;
    while index < args.len() {
        let arg = &args[index];
        if options && arg == "--" {
            options = false;
            index += 1;
            continue;
        }
        if options && (arg == "-maxdepth" || arg == "-type") {
            let value = args
                .get(index + 1)
                .ok_or_else(|| format!("missing argument to '{arg}'"))?;
            if arg == "-maxdepth" {
                max_depth = parse_depth(value, "-maxdepth")?;
            } else {
                kind = Some(match value.as_str() {
                    "f" => "file",
                    "d" => "directory",
                    _ => return Err(format!("unsupported type '{value}'; expected f or d")),
                });
            }
            index += 2;
            continue;
        }
        if options && arg.starts_with('-') {
            return Err(format!("unsupported option or predicate '{arg}'"));
        }
        if path.replace(arg.clone()).is_some() {
            return Err("only one path is supported".to_string());
        }
        index += 1;
    }

    Ok(FindArgs {
        path: path.unwrap_or_else(|| ".".to_string()),
        max_depth,
        kind,
    })
}

fn parse_depth(value: &str, option: &str) -> std::result::Result<u32, String> {
    value
        .parse::<u32>()
        .ok()
        .filter(|depth| *depth <= MAX_DEPTH)
        .ok_or_else(|| format!("{option} must be an integer from 0 to {MAX_DEPTH}"))
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
    let mut stdout = String::new();
    for entry in entries {
        let depth = entry.get("depth").and_then(Value::as_u64).unwrap_or(0) as usize;
        let path = entry.get("path").and_then(Value::as_str).unwrap_or("");
        stdout.push_str(&lexical_path(lexical_root, path, depth));
        stdout.push('\n');
    }
    if response.get("truncated").and_then(Value::as_bool) == Some(true) {
        return Ok(ExecResult {
            stdout,
            stderr: format!("find: results truncated after {WALK_LIMIT} entries\n"),
            exit_code: 1,
            ..Default::default()
        });
    }
    Ok(ExecResult::ok(stdout))
}

fn lexical_path(root: &str, absolute_path: &str, depth: usize) -> String {
    if depth == 0 {
        return root.to_string();
    }
    let suffix = absolute_path
        .trim_end_matches('/')
        .rsplitn(depth + 1, '/')
        .take(depth)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect::<Vec<_>>()
        .join("/");
    if root == "/" {
        format!("/{suffix}")
    } else if root.ends_with('/') {
        format!("{root}{suffix}")
    } else {
        format!("{root}/{suffix}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| value.to_string()).collect()
    }

    #[test]
    fn parses_supported_find_subset() {
        assert_eq!(
            parse_args(&strings(&["./texts", "-type", "f", "-maxdepth", "2"])).unwrap(),
            FindArgs {
                path: "./texts".to_string(),
                max_depth: 2,
                kind: Some("file"),
            }
        );
        assert_eq!(parse_args(&[]).unwrap().path, ".");
    }

    #[test]
    fn rejects_predicates_multiple_paths_and_excessive_depth() {
        assert!(parse_args(&strings(&[".", "-name", "*.md"])).is_err());
        assert!(parse_args(&strings(&["a", "b"])).is_err());
        assert!(parse_args(&strings(&["-maxdepth", "21"])).is_err());
    }

    #[test]
    fn preserves_lexical_root_in_output_paths() {
        assert_eq!(lexical_path(".", "/items/id.bib", 2), "./items/id.bib");
        assert_eq!(
            lexical_path("./texts", "/texts/id/page-1.md", 2),
            "./texts/id/page-1.md"
        );
        assert_eq!(lexical_path("/", "/texts", 1), "/texts");
    }

    #[test]
    fn truncation_is_a_visible_failure() {
        let response = json!({
            "entries": [{ "path": "/items", "depth": 1 }],
            "truncated": true
        });
        let result = render_response(&response, ".").unwrap();
        assert_eq!(result.exit_code, 1);
        assert_eq!(result.stdout, "./items\n");
        assert!(result.stderr.contains("truncated"));
    }
}
