use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::json;

use super::{parse_bool_flag, positional_args, tsv_escape, DomainBuiltins};

const MAX_RESOLVE_BATCH: usize = 64;
const MAX_URIS: usize = 256;

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
        if uris.len() > MAX_URIS {
            return Ok(ExecResult::err(
                format!("evidence: at most {MAX_URIS} URIs are supported\n"),
                2,
            ));
        }

        let mut stdout = String::new();
        let mut stderr = String::new();
        let mut failed = false;
        if meta {
            stdout.push_str("type\turi\ttitle\tstatus\tdocument_uri\tpage\tversion\trange\ttext\n");
        }

        for chunk in uris.chunks(MAX_RESOLVE_BATCH) {
            match self
                .domain
                .rpc
                .call("evidence.resolve_many", json!({ "uris": chunk }))
                .await
            {
                Ok(response) => append_batch_results(
                    chunk,
                    &response,
                    meta,
                    &mut stdout,
                    &mut stderr,
                    &mut failed,
                ),
                Err(err) => {
                    for uri in chunk {
                        failed = true;
                        stderr.push_str(&format!("evidence: {uri}: {err}\n"));
                    }
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

fn append_batch_results(
    uris: &[String],
    response: &serde_json::Value,
    meta: bool,
    stdout: &mut String,
    stderr: &mut String,
    failed: &mut bool,
) {
    let results = response.get("results").and_then(|value| value.as_array());
    for (index, uri) in uris.iter().enumerate() {
        let Some(result) = results.and_then(|items| items.get(index)) else {
            *failed = true;
            stderr.push_str(&format!("evidence: {uri}: invalid batch response\n"));
            continue;
        };

        if result.get("ok").and_then(|value| value.as_bool()) == Some(true) {
            if let Some(value) = result.get("value") {
                append_value(value, meta, stdout);
            } else {
                *failed = true;
                stderr.push_str(&format!("evidence: {uri}: invalid batch response\n"));
            }
            continue;
        }

        *failed = true;
        let error = result.get("error");
        let code = error
            .and_then(|value| value.get("code"))
            .and_then(|value| value.as_str());
        let message = error
            .and_then(|value| value.get("message"))
            .and_then(|value| value.as_str())
            .unwrap_or("evidence resolution failed");
        match code {
            Some(code) => stderr.push_str(&format!("evidence: {uri}: {code}: {message}\n")),
            None => stderr.push_str(&format!("evidence: {uri}: {message}\n")),
        }
    }
}

fn append_value(value: &serde_json::Value, meta: bool, stdout: &mut String) {
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
            tsv_escape(value.get("status").and_then(|v| v.as_str()).unwrap_or("ok")),
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
            value
                .get("document_uri")
                .and_then(|v| v.as_str())
                .unwrap_or(""),
            value.get("page").map(|v| v.to_string()).unwrap_or_default(),
            value.get("version").and_then(|v| v.as_str()).unwrap_or(""),
            value.get("range").and_then(|v| v.as_str()).unwrap_or(""),
            value.get("text").and_then(|v| v.as_str()).unwrap_or(""),
        ));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn batch_results_preserve_output_order_and_independent_errors() {
        let uris = vec![
            "first".to_string(),
            "second".to_string(),
            "third".to_string(),
        ];
        let response = json!({
            "results": [
                { "uri": "first", "ok": true, "value": { "display": "first text\n" }, "error": null },
                { "uri": "second", "ok": false, "value": null,
                  "error": { "code": "invalid_evref", "message": "invalid reference" } },
                { "uri": "third", "ok": true, "value": { "display": "third text\n" }, "error": null }
            ]
        });
        let mut stdout = String::new();
        let mut stderr = String::new();
        let mut failed = false;

        append_batch_results(
            &uris,
            &response,
            false,
            &mut stdout,
            &mut stderr,
            &mut failed,
        );

        assert_eq!(stdout, "first text\nthird text\n");
        assert_eq!(
            stderr,
            "evidence: second: invalid_evref: invalid reference\n"
        );
        assert!(failed);
    }

    #[test]
    fn resolve_batches_are_capped_at_domain_limit() {
        let uris = (0..129).map(|index| index.to_string()).collect::<Vec<_>>();
        assert_eq!(
            uris.chunks(MAX_RESOLVE_BATCH)
                .map(<[String]>::len)
                .collect::<Vec<_>>(),
            vec![64, 64, 1]
        );
    }
}
