use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::json;

use super::{parse_bool_flag, positional_args, tsv_escape, DomainBuiltins};

pub struct EvidenceBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[async_trait]
impl Builtin for EvidenceBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if parse_bool_flag(ctx.args, "--help") || parse_bool_flag(ctx.args, "-h") {
            return Ok(ExecResult::ok(
                "usage: evidence [--meta] URI...\n       printf '%s\\n' URI | evidence\n",
            ));
        }
        let meta = parse_bool_flag(ctx.args, "--meta");
        let mut uris = positional_args(ctx.args);
        if uris.is_empty() {
            if let Some(stdin) = ctx.stdin {
                for line in stdin.lines() {
                    let t = line.trim();
                    if !t.is_empty() {
                        uris.push(t.to_string());
                    }
                }
            }
        }
        if uris.is_empty() {
            return Ok(ExecResult::err("evidence: missing URI\n", 2));
        }

        let mut stdout = String::new();
        let mut stderr = String::new();
        let mut failed = false;
        if meta {
            stdout.push_str("type\turi\ttitle\tstatus\tdocument_uri\tpage\tversion\trange\ttext\n");
        }

        for uri in uris {
            match self
                .domain
                .rpc
                .call("evidence.resolve", json!({ "uri": uri }))
                .await
            {
                Ok(value) => {
                    if meta {
                        stdout.push_str(&format!(
                            "{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}\t{}\n",
                            tsv_escape(
                                value
                                    .get("type")
                                    .and_then(|v| v.as_str())
                                    .unwrap_or("evidence")
                            ),
                            tsv_escape(value.get("uri").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(value.get("title").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(
                                value.get("status").and_then(|v| v.as_str()).unwrap_or("ok")
                            ),
                            tsv_escape(
                                value
                                    .get("document_uri")
                                    .and_then(|v| v.as_str())
                                    .unwrap_or("")
                            ),
                            tsv_escape(
                                value
                                    .get("page")
                                    .map(|v| v.to_string())
                                    .as_deref()
                                    .unwrap_or("")
                            ),
                            tsv_escape(value.get("version").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(value.get("range").and_then(|v| v.as_str()).unwrap_or("")),
                            tsv_escape(value.get("text").and_then(|v| v.as_str()).unwrap_or("")),
                        ));
                    } else if let Some(text) = value.get("display").and_then(|v| v.as_str()) {
                        stdout.push_str(text);
                        if !text.ends_with('\n') {
                            stdout.push('\n');
                        }
                    } else {
                        stdout.push_str(&format!(
                            "URI: {}\nStatus: {}\nDocument: {}\nPage: {}\nVersion: {}\nRange: {}\nText:\n{}\n",
                            value.get("uri").and_then(|v| v.as_str()).unwrap_or(""),
                            value.get("status").and_then(|v| v.as_str()).unwrap_or("ok"),
                            value.get("document_uri").and_then(|v| v.as_str()).unwrap_or(""),
                            value.get("page").map(|v| v.to_string()).unwrap_or_default(),
                            value.get("version").and_then(|v| v.as_str()).unwrap_or(""),
                            value.get("range").and_then(|v| v.as_str()).unwrap_or(""),
                            value.get("text").and_then(|v| v.as_str()).unwrap_or(""),
                        ));
                    }
                }
                Err(err) => {
                    failed = true;
                    stderr.push_str(&format!("evidence: {uri}: {err}\n"));
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
