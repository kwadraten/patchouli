use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::json;

use super::{parse_bool_flag, parse_opt_value, positional_args, tsv_escape, DomainBuiltins};

pub struct LsBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[async_trait]
impl Builtin for LsBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if parse_bool_flag(ctx.args, "--help") || parse_bool_flag(ctx.args, "-h") {
            return Ok(ExecResult::ok(
                "usage: ls [--meta] [--limit N] [--after URI] [path|uri]\n",
            ));
        }
        let meta = parse_bool_flag(ctx.args, "--meta");
        let limit = parse_opt_value(ctx.args, "--limit")
            .and_then(|s| s.parse::<u32>().ok())
            .unwrap_or(100)
            .clamp(1, 1000);
        let after = parse_opt_value(ctx.args, "--after").map(|s| s.to_string());
        let path = positional_args(ctx.args)
            .into_iter()
            .next()
            .unwrap_or_else(|| ctx.cwd.to_string_lossy().replace('\\', "/"));

        let mut payload = json!({
            "path": path,
            "limit": limit,
        });
        if let Some(a) = after {
            payload["after"] = json!(a);
        }

        match self.domain.rpc.call("vfs.list", payload).await {
            Ok(value) => {
                let mut stdout = String::new();
                let mut stderr = String::new();
                if meta {
                    stdout.push_str("type\turi\ttitle\tstatus\n");
                }
                if let Some(entries) = value.get("entries").and_then(|v| v.as_array()) {
                    for e in entries {
                        if meta {
                            stdout.push_str(&format!(
                                "{}\t{}\t{}\t{}\n",
                                tsv_escape(e.get("type").and_then(|v| v.as_str()).unwrap_or("")),
                                tsv_escape(e.get("uri").and_then(|v| v.as_str()).unwrap_or("")),
                                tsv_escape(e.get("title").and_then(|v| v.as_str()).unwrap_or("")),
                                tsv_escape(
                                    e.get("status")
                                        .and_then(|v| v.as_str())
                                        .unwrap_or("available")
                                ),
                            ));
                        } else {
                            let name = e.get("name").and_then(|v| v.as_str()).unwrap_or("");
                            stdout.push_str(name);
                            stdout.push('\n');
                        }
                    }
                }
                if let Some(cont) = value.get("continuation_command").and_then(|v| v.as_str()) {
                    stderr.push_str(cont);
                    if !stderr.ends_with('\n') {
                        stderr.push('\n');
                    }
                }
                Ok(ExecResult {
                    stdout,
                    stderr,
                    exit_code: 0,
                    ..Default::default()
                })
            }
            Err(err) => Ok(ExecResult {
                stdout: String::new(),
                stderr: format!("ls: {err}\n"),
                exit_code: 1,
                ..Default::default()
            }),
        }
    }
}
