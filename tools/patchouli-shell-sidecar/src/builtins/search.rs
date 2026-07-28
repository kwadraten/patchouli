use std::sync::Arc;

use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};
use serde_json::json;

use super::{parse_bool_flag, parse_opt_value, positional_args, tsv_escape, DomainBuiltins};

pub struct GrepBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

pub struct SearchBuiltin {
    pub domain: Arc<DomainBuiltins>,
}

#[async_trait]
impl Builtin for GrepBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        run_search(&self.domain, &ctx, false).await
    }
}

#[async_trait]
impl Builtin for SearchBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        run_search(&self.domain, &ctx, true).await
    }
}

async fn run_search(
    domain: &DomainBuiltins,
    ctx: &BuiltinContext<'_>,
    enhanced: bool,
) -> Result<ExecResult> {
    if parse_bool_flag(ctx.args, "--help") || parse_bool_flag(ctx.args, "-h") {
        let msg = if enhanced {
            "usage: search [--meta] [--context N] [--limit N] QUERY\n"
        } else {
            "usage: grep|rg [--meta] [-A N|-B N|-C N] [--limit N] REGEX [path|uri]\n"
        };
        return Ok(ExecResult::ok(msg));
    }

    let meta = parse_bool_flag(ctx.args, "--meta");
    let limit = parse_opt_value(ctx.args, "--limit")
        .and_then(|s| s.parse::<u32>().ok())
        .unwrap_or(100)
        .clamp(1, 1000);
    let context_n = parse_opt_value(ctx.args, "--context")
        .or_else(|| parse_opt_value(ctx.args, "-C"))
        .and_then(|s| s.parse::<u32>().ok())
        .unwrap_or(0);
    let before = parse_opt_value(ctx.args, "-B")
        .and_then(|s| s.parse::<u32>().ok())
        .unwrap_or(context_n);
    let after = parse_opt_value(ctx.args, "-A")
        .and_then(|s| s.parse::<u32>().ok())
        .unwrap_or(context_n);

    let positionals = positional_args(ctx.args);
    if positionals.is_empty() {
        return Ok(ExecResult::err("missing search pattern/query\n", 2));
    }
    let query = positionals[0].clone();
    let scope = positionals
        .get(1)
        .cloned()
        .unwrap_or_else(|| ctx.cwd.to_string_lossy().replace('\\', "/"));

    let method = if enhanced {
        "search.enhanced"
    } else {
        "search.exact"
    };
    let payload = json!({
        "query": query,
        "scope": scope,
        "limit": limit,
        "before": before,
        "after": after,
        "context": context_n,
    });

    match domain.rpc.call(method, payload).await {
        Ok(value) => {
            let mut stdout = String::new();
            let mut stderr = String::new();
            if meta {
                stdout.push_str("type\turi\ttitle\tstatus\tline\tcolumn\tpreview\n");
            }
            let matches = value
                .get("matches")
                .and_then(|v| v.as_array())
                .cloned()
                .unwrap_or_default();
            for m in &matches {
                let uri = m.get("uri").and_then(|v| v.as_str()).unwrap_or("");
                let line = m.get("line").and_then(|v| v.as_u64()).unwrap_or(0);
                let column = m.get("column").and_then(|v| v.as_u64()).unwrap_or(0);
                let preview = m.get("preview").and_then(|v| v.as_str()).unwrap_or("");
                if meta {
                    stdout.push_str(&format!(
                        "{}\t{}\t{}\t{}\t{}\t{}\t{}\n",
                        tsv_escape(m.get("type").and_then(|v| v.as_str()).unwrap_or("match")),
                        tsv_escape(uri),
                        tsv_escape(m.get("title").and_then(|v| v.as_str()).unwrap_or("")),
                        tsv_escape(
                            m.get("status")
                                .and_then(|v| v.as_str())
                                .unwrap_or("available")
                        ),
                        line,
                        column,
                        tsv_escape(preview),
                    ));
                } else {
                    stdout.push_str(&format!("{uri}:{line}:{column}:{preview}\n"));
                }
            }
            if value
                .get("truncated")
                .and_then(|v| v.as_bool())
                .unwrap_or(false)
            {
                stderr.push_str(&format!(
                    "search: result limit {limit} reached; narrow the query\n"
                ));
            }
            Ok(ExecResult {
                stdout,
                stderr,
                exit_code: 0,
                ..Default::default()
            })
        }
        Err(err) => Ok(ExecResult::err(format!("{err}\n"), 1)),
    }
}
