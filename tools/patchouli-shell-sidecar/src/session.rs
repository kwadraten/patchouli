use std::collections::{HashMap, VecDeque};
use std::pin::pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use bashkit::hooks::{ExecInput, HookAction, ToolEvent};
use bashkit::{Bash, BashBuilder, ExecResult};
use parking_lot::{Mutex, RwLock};
use serde_json::{json, Value};
use tokio::sync::{Mutex as AsyncMutex, Notify};

use crate::builtins::{self, DomainBuiltins};
use crate::limits::{
    append_exit_trailer, append_stream_chunk, execution_limits, truncate_complete_lines,
    MAX_BRACE_EXPANSION_RESULTS, MAX_GLOB_EXPANSION_RESULTS, MAX_TERMINAL_OUTPUT_BYTES,
};
use crate::rpc::SharedRpc;
use crate::vfs::build_readonly_fs;

const FORBIDDEN_COMMANDS: &[&str] = &[
    "rm",
    "mv",
    "cp",
    "mkdir",
    "touch",
    "chmod",
    "chown",
    "ln",
    "rmdir",
    "truncate",
    "mktemp",
    "mkfifo",
    "tee",
    "curl",
    "wget",
    "http",
    "ssh",
    "scp",
    "sftp",
    "python",
    "python3",
    "node",
    "deno",
    "bun",
    "ts",
    "typescript",
    "git",
    "sqlite",
    "sqlite3",
    "sleep",
    "kill",
    "wait",
    "watch",
    "yes",
    "parallel",
    "retry",
    "timeout",
    "exec",
    "bash",
    "sh",
    "env",
    "printenv",
    "dotenv",
    "compgen",
    "fc",
];

/// Control plane that cancel/timeout can touch without the bash exec lock.
struct SessionControl {
    /// Points at the active Bash cancellation token; rebound on session reset.
    cancel_flag: RwLock<Arc<AtomicBool>>,
    cancel_notify: Notify,
    queue: AsyncMutex<VecDeque<QueuedCommand>>,
    running: AtomicBool,
}

struct SessionState {
    control: Arc<SessionControl>,
    bash: AsyncMutex<Bash>,
}

struct QueuedCommand {
    command: String,
    deadline: Instant,
    response: tokio::sync::oneshot::Sender<Value>,
}

pub struct SessionManager {
    rpc: SharedRpc,
    sessions: AsyncMutex<HashMap<String, Arc<SessionState>>>,
}

impl SessionManager {
    pub fn new(rpc: SharedRpc) -> Self {
        Self {
            rpc,
            sessions: AsyncMutex::new(HashMap::new()),
        }
    }

    async fn get_or_create(&self, session_id: &str) -> Arc<SessionState> {
        let mut map = self.sessions.lock().await;
        if let Some(s) = map.get(session_id) {
            return s.clone();
        }
        let state = Arc::new(build_session(self.rpc.clone()));
        map.insert(session_id.to_string(), state.clone());
        state
    }

    pub async fn execute(
        &self,
        session_id: &str,
        command: &str,
        deadline_unix_ms: Option<i64>,
    ) -> Value {
        if session_id.is_empty() {
            return terminal_result("missing session_id", 2, false);
        }

        let deadline = deadline_from_unix_ms(deadline_unix_ms);
        let state = self.get_or_create(session_id).await;
        let (tx, rx) = tokio::sync::oneshot::channel();

        {
            let mut queue = state.control.queue.lock().await;
            if Instant::now() >= deadline {
                return terminal_result("command timed out; shell session reset", 124, true);
            }
            queue.push_back(QueuedCommand {
                command: command.to_string(),
                deadline,
                response: tx,
            });
            if !state.control.running.swap(true, Ordering::SeqCst) {
                let session = state.clone();
                let rpc = self.rpc.clone();
                tokio::spawn(async move {
                    run_session_queue(session, rpc).await;
                });
            }
        }

        rx.await
            .unwrap_or_else(|_| terminal_result("shell session reset before execution", 125, true))
    }

    pub async fn cancel(&self, session_id: &str) {
        let map = self.sessions.lock().await;
        if let Some(state) = map.get(session_id) {
            // Never wait on the bash lock here — only signal + drain queue.
            signal_cancel(&state.control);
            drain_queue(
                &state.control,
                "command cancelled; shell session reset",
                130,
            )
            .await;
            if let Ok(mut bash) = state.bash.try_lock() {
                *bash = build_bash(self.rpc.clone());
                rebind_cancel_flag(&state.control, bash.cancellation_token());
            }
        }
        self.rpc.cancel_all_pending();
    }

    pub async fn close(&self, session_id: &str) {
        let mut map = self.sessions.lock().await;
        if let Some(state) = map.remove(session_id) {
            signal_cancel(&state.control);
            drain_queue(&state.control, "shell session reset before execution", 125).await;
        }
    }

    pub async fn shutdown(&self) {
        let mut map = self.sessions.lock().await;
        for (_, state) in map.drain() {
            signal_cancel(&state.control);
            drain_queue(
                &state.control,
                "library changed; shell session terminated",
                125,
            )
            .await;
        }
        self.rpc.cancel_all_pending();
    }
}

fn signal_cancel(control: &SessionControl) {
    control.cancel_flag.read().store(true, Ordering::SeqCst);
    control.cancel_notify.notify_waiters();
}

fn rebind_cancel_flag(control: &SessionControl, token: Arc<AtomicBool>) {
    token.store(false, Ordering::SeqCst);
    *control.cancel_flag.write() = token;
}

fn build_session(rpc: SharedRpc) -> SessionState {
    let bash = build_bash(rpc);
    let cancel_flag = bash.cancellation_token();
    cancel_flag.store(false, Ordering::SeqCst);
    SessionState {
        control: Arc::new(SessionControl {
            cancel_flag: RwLock::new(cancel_flag),
            cancel_notify: Notify::new(),
            queue: AsyncMutex::new(VecDeque::new()),
            running: AtomicBool::new(false),
        }),
        bash: AsyncMutex::new(bash),
    }
}

async fn drain_queue(control: &SessionControl, message: &str, exit_code: i32) {
    let mut queue = control.queue.lock().await;
    while let Some(item) = queue.pop_front() {
        let _ = item
            .response
            .send(terminal_result(message, exit_code, true));
    }
}

async fn run_session_queue(state: Arc<SessionState>, rpc: SharedRpc) {
    loop {
        let next = {
            let mut queue = state.control.queue.lock().await;
            match queue.pop_front() {
                Some(item) => item,
                None => {
                    state.control.running.store(false, Ordering::SeqCst);
                    return;
                }
            }
        };

        if Instant::now() >= next.deadline
            || state.control.cancel_flag.read().load(Ordering::SeqCst)
        {
            let (msg, code) = if state.control.cancel_flag.read().load(Ordering::SeqCst) {
                ("command cancelled; shell session reset", 130)
            } else {
                ("command timed out; shell session reset", 124)
            };
            let _ = next.response.send(terminal_result(msg, code, true));
            reset_session(&state, &rpc).await;
            continue;
        }

        let remaining = next.deadline.saturating_duration_since(Instant::now());
        let cancel_flag = {
            let flag = state.control.cancel_flag.read().clone();
            flag.store(false, Ordering::SeqCst);
            flag
        };
        let cancel_notify = &state.control.cancel_notify;
        let bash_slot = &state.bash;
        let command = next.command.clone();
        let output_limit_hit = Arc::new(AtomicBool::new(false));

        let mut exec = pin!(async {
            let mut bash = bash_slot.lock().await;
            let merged = Arc::new(Mutex::new(String::new()));
            let merged_cb = merged.clone();
            let limit_hit_cb = output_limit_hit.clone();
            let cancel_output = cancel_flag.clone();
            let result = bash
                .exec_streaming(
                    &command,
                    Box::new(move |stdout_chunk, stderr_chunk| {
                        let mut guard = merged_cb.lock();
                        append_stream_chunk(&mut guard, stdout_chunk, stderr_chunk);
                        if guard.len() >= MAX_TERMINAL_OUTPUT_BYTES {
                            limit_hit_cb.store(true, Ordering::SeqCst);
                            cancel_output.store(true, Ordering::SeqCst);
                        }
                    }),
                )
                .await;
            let ordered = merged.lock().clone();
            let limit_hit = output_limit_hit.load(Ordering::SeqCst);
            (result, ordered, limit_hit)
        });

        let outcome = tokio::select! {
            biased;
            _ = cancel_notify.notified() => {
                cancel_flag.store(true, Ordering::SeqCst);
                None
            }
            _ = tokio::time::sleep(remaining) => {
                cancel_flag.store(true, Ordering::SeqCst);
                None
            }
            res = &mut exec => Some(res),
        };

        let (result, ordered, output_limit_hit) = match outcome {
            Some(pair) => pair,
            None => {
                // Let bashkit observe cancel_flag and drop the bash lock.
                let _ = exec.await;
                let (msg, code) = if Instant::now() >= next.deadline {
                    ("command timed out; shell session reset", 124)
                } else {
                    ("command cancelled; shell session reset", 130)
                };
                let _ = next.response.send(terminal_result(msg, code, true));
                reset_session(&state, &rpc).await;
                continue;
            }
        };

        let payload = if output_limit_hit {
            format_output_limit_result(ordered)
        } else {
            match result {
                Ok(exec_result) => format_exec_result(exec_result, ordered),
                Err(err) => {
                    let msg = format!("{err}");
                    let lower = msg.to_lowercase();
                    let code = if lower.contains("cancel") {
                        130
                    } else if lower.contains("timeout")
                        || lower.contains("limit")
                        || lower.contains("truncated")
                    {
                        124
                    } else {
                        1
                    };
                    if code == 124 || code == 130 {
                        reset_session(&state, &rpc).await;
                    }
                    terminal_result(
                        &sanitize_error_message(&msg),
                        code,
                        code == 124 || code == 130,
                    )
                }
            }
        };
        let _ = next.response.send(payload);
    }
}

async fn reset_session(state: &Arc<SessionState>, rpc: &SharedRpc) {
    drain_queue(&state.control, "shell session reset before execution", 125).await;
    let mut bash = state.bash.lock().await;
    *bash = build_bash(rpc.clone());
    rebind_cancel_flag(&state.control, bash.cancellation_token());
    rpc.cancel_all_pending();
}

fn build_bash(rpc: SharedRpc) -> Bash {
    let fs = build_readonly_fs(rpc.clone());
    let domain = DomainBuiltins::new(rpc);
    let forbidden: Arc<Mutex<Vec<&'static str>>> =
        Arc::new(Mutex::new(FORBIDDEN_COMMANDS.to_vec()));
    let brace_expansion_present = Arc::new(AtomicBool::new(false));

    let mut builder = BashBuilder::default()
        .fs(fs)
        .cwd("/")
        .username("agent")
        .hostname("patchouli")
        .limits(execution_limits())
        .readonly_filesystem(true)
        .before_exec({
            let brace_expansion_present = brace_expansion_present.clone();
            Box::new(move |event: ExecInput| {
                brace_expansion_present
                    .store(script_may_expand_braces(&event.script), Ordering::SeqCst);
                HookAction::Continue(event)
            })
        })
        .before_tool({
            let forbidden = forbidden.clone();
            let brace_expansion_present = brace_expansion_present.clone();
            Box::new(move |event: ToolEvent| {
                let name = event.name.to_lowercase();
                if forbidden.lock().contains(&name.as_str()) {
                    return HookAction::Cancel(format!(
                        "{name}: command not permitted in read-only shell"
                    ));
                }
                if event.args.len() > MAX_GLOB_EXPANSION_RESULTS {
                    return HookAction::Cancel(format!(
                        "{name}: glob expansion exceeds {MAX_GLOB_EXPANSION_RESULTS} results"
                    ));
                }
                if brace_expansion_present.load(Ordering::SeqCst)
                    && event.args.len() > MAX_BRACE_EXPANSION_RESULTS
                {
                    return HookAction::Cancel(format!(
                        "{name}: brace expansion exceeds {MAX_BRACE_EXPANSION_RESULTS} results"
                    ));
                }
                HookAction::Continue(event)
            })
        });

    for (name, builtin) in builtins::register_all(domain) {
        builder = builder.builtin(name, builtin);
    }

    builder.build()
}

fn script_may_expand_braces(script: &str) -> bool {
    let mut start = None;
    for (index, ch) in script.char_indices() {
        match ch {
            '{' => start = Some(index + ch.len_utf8()),
            '}' => {
                if let Some(content_start) = start.take() {
                    let content = &script[content_start..index];
                    if content.contains(',') || content.contains("..") {
                        return true;
                    }
                }
            }
            _ => {}
        }
    }
    false
}

fn format_exec_result(exec: ExecResult, ordered_merged: String) -> Value {
    let merged = if ordered_merged.is_empty() {
        let mut fallback = String::new();
        append_stream_chunk(&mut fallback, &exec.stdout, &exec.stderr);
        fallback
    } else {
        ordered_merged
    };
    let (mut text, truncated) = truncate_complete_lines(&merged, MAX_TERMINAL_OUTPUT_BYTES);
    let mut exit = exec.exit_code;
    let stream_truncated = exec.stdout_truncated || exec.stderr_truncated;
    if truncated || stream_truncated {
        if !text.is_empty() && !text.ends_with('\n') {
            text.push('\n');
        }
        text.push_str("[output truncated]\n");
        exit = 124;
    }
    text = append_exit_trailer(text, exit);
    json!({
        "text": text,
        "exit_code": exit,
        "session_reset": exit == 124 || exit == 130 || exit == 125
    })
}

fn format_output_limit_result(ordered_merged: String) -> Value {
    let (mut text, _) = truncate_complete_lines(&ordered_merged, MAX_TERMINAL_OUTPUT_BYTES);
    if !text.is_empty() && !text.ends_with('\n') {
        text.push('\n');
    }
    text.push_str("[output truncated]\n");
    text = append_exit_trailer(text, 124);
    json!({
        "text": text,
        "exit_code": 124,
        "session_reset": true
    })
}

fn terminal_result(message: &str, exit_code: i32, session_reset: bool) -> Value {
    let mut text = sanitize_error_message(message);
    if !text.ends_with('\n') {
        text.push('\n');
    }
    text = append_exit_trailer(text, exit_code);
    json!({
        "text": text,
        "exit_code": exit_code,
        "session_reset": session_reset
    })
}

fn sanitize_error_message(message: &str) -> String {
    let mut out = message.to_string();
    for marker in [
        "file://", "C:\\", "c:\\", "/Users/", "/home/", "D:\\", "d:\\",
    ] {
        if let Some(idx) = out.find(marker) {
            out.truncate(idx);
            out.push_str("[redacted]");
            break;
        }
    }
    out
}

fn deadline_from_unix_ms(deadline_unix_ms: Option<i64>) -> Instant {
    let now = Instant::now();
    let default = now + Duration::from_secs(15);
    let Some(ms) = deadline_unix_ms else {
        return default;
    };
    if ms < 0 {
        return now;
    }
    let target = UNIX_EPOCH + Duration::from_millis(ms as u64);
    let now_sys = SystemTime::now();
    match target.duration_since(now_sys) {
        Ok(d) => now + d.min(Duration::from_secs(15)),
        Err(_) => now,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::rpc::RpcHost;
    use std::time::Duration;

    fn test_rpc() -> SharedRpc {
        Arc::new(RpcHost::new(tokio::io::stdout()))
    }

    #[tokio::test]
    async fn sessions_are_isolated() {
        let sessions = SessionManager::new(test_rpc());
        let a = sessions.execute("s-a", "FOO=alpha; echo $FOO", None).await;
        let b = sessions.execute("s-b", "echo ${FOO:-unset}", None).await;
        assert_eq!(a["exit_code"], 0);
        assert!(a["text"].as_str().unwrap().contains("alpha"));
        assert_eq!(b["exit_code"], 0);
        assert!(b["text"].as_str().unwrap().contains("unset"));
    }

    #[tokio::test]
    async fn same_session_is_fifo() {
        let sessions = SessionManager::new(test_rpc());
        let first = sessions.execute("fifo", "X=1; echo $X", None);
        let second = sessions.execute("fifo", "echo $X", None);
        let (r1, r2) = tokio::join!(first, second);
        assert!(r1["text"].as_str().unwrap().contains('1'));
        assert!(r2["text"].as_str().unwrap().contains('1'));
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn cancel_does_not_block_on_exec_lock() {
        let sessions = Arc::new(SessionManager::new(test_rpc()));
        let sessions_exec = sessions.clone();
        let exec = tokio::spawn(async move {
            sessions_exec
                .execute("cancel-me", "while true; do :; done", None)
                .await
        });
        // Let the loop start holding the bash lock.
        tokio::time::sleep(Duration::from_millis(80)).await;
        let cancel_started = Instant::now();
        sessions.cancel("cancel-me").await;
        assert!(
            cancel_started.elapsed() < Duration::from_millis(500),
            "cancel must not wait for bash.exec lock"
        );
        let result = tokio::time::timeout(Duration::from_secs(5), exec)
            .await
            .expect("exec should finish after cancel")
            .expect("join");
        let code = result["exit_code"].as_i64().unwrap_or(-1);
        assert!(
            code == 130 || code == 124,
            "expected cancel/timeout exit, got {result}"
        );
    }

    #[tokio::test]
    async fn forbidden_command_has_no_host_paths() {
        let sessions = SessionManager::new(test_rpc());
        let result = sessions
            .execute("deny", "curl http://example.com", None)
            .await;
        let text = result["text"].as_str().unwrap_or("");
        assert!(text.contains("not permitted") || result["exit_code"] != 0);
        assert!(!text.contains("file://"));
        assert!(!text.contains("/Users/"));
        assert!(!text.contains("C:\\"));
    }

    #[tokio::test]
    async fn output_truncation_marks_session_reset() {
        let sessions = SessionManager::new(test_rpc());
        // Generate more than 1 MiB of complete lines.
        let result = sessions
            .execute(
                "trunc",
                "i=0; while [ $i -lt 200000 ]; do echo 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'; i=$((i+1)); done",
                None,
            )
            .await;
        let text = result["text"].as_str().unwrap_or("");
        assert!(
            text.contains("[output truncated]") || result["exit_code"] == 124,
            "expected truncation, got {result}"
        );
    }

    #[tokio::test]
    async fn combined_stdout_and_stderr_stop_at_total_output_limit() {
        let sessions = SessionManager::new(test_rpc());
        let result = sessions
            .execute("combined-trunc", "seq 1 120000; seq 1 120000 1>&2", None)
            .await;
        let text = result["text"].as_str().unwrap_or("");
        assert_eq!(result["exit_code"], 124, "{result}");
        assert_eq!(result["session_reset"], true, "{result}");
        assert!(text.contains("[output truncated]"), "{result}");
        assert!(
            text.len() <= MAX_TERMINAL_OUTPUT_BYTES + 64,
            "combined terminal result exceeded the cap: {}",
            text.len()
        );
    }

    #[tokio::test]
    async fn echo_stdout_and_stderr_merge_in_stream_order() {
        let sessions = SessionManager::new(test_rpc());
        // printf to both fds in known order within one simple command sequence.
        let result = sessions
            .execute(
                "merge",
                "echo out1; echo err1 1>&2; echo out2; echo err2 1>&2",
                None,
            )
            .await;
        assert_eq!(result["exit_code"], 0, "{result}");
        let text = result["text"].as_str().unwrap_or("");
        let out1 = text.find("out1").expect("out1");
        let err1 = text.find("err1").expect("err1");
        let out2 = text.find("out2").expect("out2");
        let err2 = text.find("err2").expect("err2");
        assert!(
            out1 < err1 && err1 < out2 && out2 < err2,
            "order was: {text}"
        );
    }

    #[test]
    fn sanitize_redacts_host_paths() {
        assert!(sanitize_error_message("failed C:\\Users\\x\\a.pdf").contains("[redacted]"));
        assert!(sanitize_error_message("open /Users/me/secret").contains("[redacted]"));
        assert!(!sanitize_error_message("ok").contains("[redacted]"));
    }

    #[test]
    fn brace_detection_ignores_parameter_expansion() {
        assert!(script_may_expand_braces("echo {1..3}"));
        assert!(script_may_expand_braces("echo {a,b}"));
        assert!(!script_may_expand_braces("echo ${VALUE:-fallback}"));
    }

    #[tokio::test]
    async fn brace_expansion_is_capped_at_two_thousand_results() {
        let sessions = SessionManager::new(test_rpc());
        let result = sessions
            .execute("brace-limit", "printf '%s\\n' {1..2001}", None)
            .await;
        assert_ne!(result["exit_code"], 0, "{result}");
        assert!(
            result["text"]
                .as_str()
                .unwrap_or("")
                .contains("cancelled by before_tool hook"),
            "{result}"
        );
    }
}
