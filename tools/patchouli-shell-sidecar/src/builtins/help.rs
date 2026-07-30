use bashkit::{async_trait, Builtin, BuiltinContext, ExecResult, Result};

pub struct HelpBuiltin;

const HELP_TEXT: &str = r#"Patchouli read-only virtual shell

Commands:
  pwd                 Print working directory
  cd                  Change directory
  ls                  List VFS entries (--meta, --limit, --after)
  stat                Domain metadata for paths/URIs
  cat                 Read complete text pages and files
  head tail           Read up to 1000 leading or trailing lines
  wc                  Count lines, words, bytes, characters, or line length
  grep rg             Regex search over texts with automatic evref URIs
  search              Enhanced library search
  find tree           Single-RPC path traversal helpers
  file                Report file types
  evidence            Validate evref and extract pinned fragment
  cite                Format CSL bibliography for item paths/URIs
  help                This help

Start with: pwd; ls; cat /AGENTS.md
"#;

#[async_trait]
impl Builtin for HelpBuiltin {
    async fn execute(&self, ctx: BuiltinContext<'_>) -> Result<ExecResult> {
        if ctx.args.first().map(|s| s.as_str()) == Some("--help")
            || ctx.args.first().map(|s| s.as_str()) == Some("-h")
        {
            return Ok(ExecResult::ok(HELP_TEXT));
        }
        if let Some(cmd) = ctx.args.first() {
            return Ok(ExecResult::ok(command_help(cmd)));
        }
        Ok(ExecResult::ok(HELP_TEXT))
    }
}

fn command_help(cmd: &str) -> String {
    match cmd {
        "ls" => "usage: ls [--meta] [--limit N] [--after URI] [path|uri]\n".to_string(),
        "stat" => "usage: stat [--meta] path|uri...\n".to_string(),
        "find" => "usage: find [path] [-maxdepth N] [-type f|d]\n".to_string(),
        "tree" => "usage: tree [-L N] [path]\n".to_string(),
        "head" | "tail" => format!("usage: {cmd} [-n N|--lines N] [FILE...]\n"),
        "grep" | "rg" => {
            "usage: grep|rg [--meta] [-A N|-B N|-C N] [--limit N] REGEX [path|uri]\n".to_string()
        }
        "search" => "usage: search [--meta] [--context N] [--limit N] QUERY\n".to_string(),
        "evidence" => {
            "usage: evidence [--meta] URI...\n       printf '%s\\n' URI | evidence\n".to_string()
        }
        "cite" => {
            "usage: cite [--meta] [--style /csl-styles/id.csl] /items/id.bib...\n".to_string()
        }
        "wc" => "usage: wc [-lwcmL] [FILE...]\n".to_string(),
        "help" => HELP_TEXT.to_string(),
        other => format!("no detailed help for '{other}'\nrun: help\n"),
    }
}
