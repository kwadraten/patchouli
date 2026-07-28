use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::json;

use super::{parse_bool_flag, positional_args, tsv_escape, DomainBuiltins};

pub struct StatBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[async_trait]
impl Builtin for StatBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if parse_bool_flag(ctx.args, "--help") || parse_bool_flag(ctx.args, "-h") {
            return Ok(ExecResult::ok("usage: stat [--meta] path|uri...\n"));
        }
        let meta = parse_bool_flag(ctx.args, "--meta");
        let targets = positional_args(ctx.args);
        if targets.is_empty() {
            return Ok(ExecResult::err("stat: missing operand\n", 2));
        }

        let mut stdout = String::new();
        let mut stderr = String::new();
        let mut failed = false;
        if meta {
            stdout.push_str("type\turi\ttitle\tstatus\n");
        }

        for target in targets {
            match self
                .domain
                .rpc
                .call("vfs.stat", json!({ "path": target }))
                .await
            {
                Ok(value) => {
                    if meta {
                        stdout.push_str(&format!(
                            "{}\t{}\t{}\t{}\n",
                            tsv_escape(value.get("type").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(value.get("uri").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(value.get("title").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(
                                value
                                    .get("status")
                                    .and_then(|v| v.as_str())
                                    .unwrap_or("available")
                            ),
                        ));
                    } else if let Some(text) = value.get("text").and_then(|v| v.as_str()) {
                        stdout.push_str(text);
                        if !text.ends_with('\n') {
                            stdout.push('\n');
                        }
                    } else {
                        stdout.push_str(&format!(
                            "Type: {}\nURI: {}\nTitle: {}\nStatus: {}\n",
                            value.get("type").and_then(|v| v.as_str()).unwrap_or(""),
                            value.get("uri").and_then(|v| v.as_str()).unwrap_or(""),
                            value.get("title").and_then(|v| v.as_str()).unwrap_or(""),
                            value
                                .get("status")
                                .and_then(|v| v.as_str())
                                .unwrap_or("available"),
                        ));
                    }
                }
                Err(err) => {
                    failed = true;
                    stderr.push_str(&format!("stat: {target}: {err}\n"));
                }
            }
        }

        Ok(ExecResult {
            stdout,
            stderr,
            exit_code: if failed { 1 } else { 0 },
            ..Default::default()
        })
    }
}
