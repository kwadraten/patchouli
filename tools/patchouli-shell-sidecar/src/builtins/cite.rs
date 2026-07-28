use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::json;

use super::{parse_bool_flag, parse_opt_value, positional_args, tsv_escape, DomainBuiltins};

pub struct CiteBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[async_trait]
impl Builtin for CiteBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if parse_bool_flag(ctx.args, "--help") || parse_bool_flag(ctx.args, "-h") {
            return Ok(ExecResult::ok(
                "usage: cite [--meta] [--style /csl-styles/id.csl] /items/id.bib...\n",
            ));
        }
        let meta = parse_bool_flag(ctx.args, "--meta");
        let style = parse_opt_value(ctx.args, "--style").map(|s| s.to_string());
        let mut items = positional_args(ctx.args);
        if items.is_empty() {
            if let Some(stdin) = ctx.stdin {
                for line in stdin.lines() {
                    let t = line.trim();
                    if !t.is_empty() {
                        items.push(t.to_string());
                    }
                }
            }
        }
        if items.is_empty() {
            return Ok(ExecResult::err("cite: missing item path/URI\n", 2));
        }

        let mut payload = json!({ "items": items });
        if let Some(style) = style {
            payload["style"] = json!(style);
        }

        match self.domain.rpc.call("cite.format", payload).await {
            Ok(value) => {
                let mut stdout = String::new();
                let mut stderr = String::new();
                if let Some(warnings) = value.get("warnings").and_then(|v| v.as_array()) {
                    for w in warnings {
                        if let Some(s) = w.as_str() {
                            stderr.push_str(s);
                            if !s.ends_with('\n') {
                                stderr.push('\n');
                            }
                        }
                    }
                }
                let failed = value
                    .get("failed")
                    .and_then(|v| v.as_bool())
                    .unwrap_or(false);
                if meta {
                    stdout.push_str("type\turi\ttitle\tstatus\tstyle_uri\ttext\n");
                    stdout.push_str(&format!(
                        "bibliography\t\t\t{}\t{}\t{}\n",
                        tsv_escape(value.get("status").and_then(|v| v.as_str()).unwrap_or("ok")),
                        tsv_escape(
                            value
                                .get("style_uri")
                                .and_then(|v| v.as_str())
                                .unwrap_or("")
                        ),
                        tsv_escape(value.get("text").and_then(|v| v.as_str()).unwrap_or("")),
                    ));
                } else if let Some(text) = value.get("text").and_then(|v| v.as_str()) {
                    stdout.push_str(text);
                    if !text.is_empty() && !text.ends_with('\n') {
                        stdout.push('\n');
                    }
                }
                Ok(ExecResult {
                    stdout,
                    stderr,
                    exit_code: if failed { 1 } else { 0 },
                    ..Default::default()
                })
            }
            Err(err) => Ok(ExecResult::err(format!("cite: {err}\n"), 1)),
        }
    }
}
