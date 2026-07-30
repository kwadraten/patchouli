mod cite;
mod evidence;
mod find;
mod headtail;
mod help;
mod ls;
mod search;
mod stat_cmd;
mod tree;
mod wc;

use std::sync::Arc;

use bashkit::Builtin;

use crate::rpc::SharedRpc;

#[derive(Clone)]
pub struct DomainBuiltins {
    pub rpc: SharedRpc,
}

impl DomainBuiltins {
    pub fn new(rpc: SharedRpc) -> Self {
        Self { rpc }
    }
}

pub fn register_all(domain: DomainBuiltins) -> Vec<(String, Box<dyn Builtin>)> {
    let d = Arc::new(domain);
    vec![
        (
            "ls".to_string(),
            Box::new(ls::LsBuiltin { domain: d.clone() }),
        ),
        (
            "stat".to_string(),
            Box::new(stat_cmd::StatBuiltin { domain: d.clone() }),
        ),
        (
            "find".to_string(),
            Box::new(find::FindBuiltin { domain: d.clone() }),
        ),
        (
            "tree".to_string(),
            Box::new(tree::TreeBuiltin { domain: d.clone() }),
        ),
        (
            "head".to_string(),
            Box::new(headtail::HeadTailBuiltin {
                domain: d.clone(),
                mode: "head",
            }),
        ),
        (
            "tail".to_string(),
            Box::new(headtail::HeadTailBuiltin {
                domain: d.clone(),
                mode: "tail",
            }),
        ),
        (
            "grep".to_string(),
            Box::new(search::GrepBuiltin { domain: d.clone() }),
        ),
        (
            "rg".to_string(),
            Box::new(search::GrepBuiltin { domain: d.clone() }),
        ),
        (
            "search".to_string(),
            Box::new(search::SearchBuiltin { domain: d.clone() }),
        ),
        (
            "evidence".to_string(),
            Box::new(evidence::EvidenceBuiltin { domain: d.clone() }),
        ),
        (
            "cite".to_string(),
            Box::new(cite::CiteBuiltin { domain: d.clone() }),
        ),
        (
            "wc".to_string(),
            Box::new(wc::WcBuiltin { domain: d.clone() }),
        ),
        ("help".to_string(), Box::new(help::HelpBuiltin)),
    ]
}

pub fn tsv_escape(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('\t', "\\t")
        .replace('\n', "\\n")
        .replace('\r', "\\r")
}

pub fn parse_bool_flag(args: &[String], name: &str) -> bool {
    args.iter().any(|a| a == name)
}

pub fn parse_opt_value<'a>(args: &'a [String], name: &str) -> Option<&'a str> {
    let mut i = 0;
    while i < args.len() {
        if args[i] == name {
            return args.get(i + 1).map(|s| s.as_str());
        }
        if let Some(rest) = args[i].strip_prefix(&format!("{name}=")) {
            return Some(rest);
        }
        i += 1;
    }
    None
}

pub fn positional_args(args: &[String]) -> Vec<String> {
    let mut out = Vec::new();
    let mut i = 0;
    while i < args.len() {
        let a = &args[i];
        if a == "--" {
            out.extend(args[i + 1..].iter().cloned());
            break;
        }
        if a.starts_with('-') {
            if matches!(
                a.as_str(),
                "--limit" | "--after" | "--style" | "--context" | "-A" | "-B" | "-C" | "-e" | "-f"
            ) {
                i += 2;
                continue;
            }
            i += 1;
            continue;
        }
        out.push(a.clone());
        i += 1;
    }
    out
}
