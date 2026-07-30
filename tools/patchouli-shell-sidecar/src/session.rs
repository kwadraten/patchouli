use std::collections::{HashMap, VecDeque};
use std::pin::pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
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
    COMMAND_TIMEOUT, MAX_BRACE_EXPANSION_RESULTS, MAX_COMMAND_TIMEOUT, MAX_GLOB_EXPANSION_RESULTS,
    MAX_TERMINAL_OUTPUT_BYTES, MIN_COMMAND_TIMEOUT,
};
use crate::rpc::{with_execution_id, SharedRpc};
use crate::vfs::{build_readonly_fs, VfsCache};

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

pub const MAX_ACTIVE_SESSIONS: usize = 128;
pub const MAX_QUEUED_COMMANDS_PER_SESSION: usize = 32;

/// Control plane that cancel/timeout can touch without the bash exec lock.
struct SessionControl {
    /// Points at the active Bash cancellation token; rebound on session reset.
    cancel_flag: RwLock<Arc<AtomicBool>>,
    cancel_notify: Notify,
    queue: AsyncMutex<VecDeque<QueuedCommand>>,
    running: AtomicBool,
    active_execution_id: AtomicU64,
}

struct SessionState {
    control: Arc<SessionControl>,
    bash: AsyncMutex<Bash>,
    cache: VfsCache,
    command_timeout: Duration,
}

struct QueuedCommand {
    execution_id: u64,
    command: String,
    deadline: Instant,
    response: tokio::sync::oneshot::Sender<Value>,
}

pub struct SessionManager {
    rpc: SharedRpc,
    sessions: AsyncMutex<HashMap<String, Arc<SessionState>>>,
    command_timeout_ms: AtomicU64,
    next_execution_id: AtomicU64,
}

impl SessionManager {
    pub fn new(rpc: SharedRpc) -> Self {
        Self {
            rpc,
            sessions: AsyncMutex::new(HashMap::new()),
            command_timeout_ms: AtomicU64::new(COMMAND_TIMEOUT.as_millis() as u64),
            next_execution_id: AtomicU64::new(1),
        }
    }

    pub fn set_command_timeout_ms(&self, requested_ms: Option<u64>) -> Duration {
        let requested =
            Duration::from_millis(requested_ms.unwrap_or(COMMAND_TIMEOUT.as_millis() as u64));
        let effective = requested.clamp(MIN_COMMAND_TIMEOUT, MAX_COMMAND_TIMEOUT);
        self.command_timeout_ms
            .store(effective.as_millis() as u64, Ordering::SeqCst);
        effective
    }

    pub fn command_timeout(&self) -> Duration {
        Duration::from_millis(self.command_timeout_ms.load(Ordering::SeqCst))
    }

    async fn get_or_create(&self, session_id: &str) -> Result<Arc<SessionState>, &'static str> {
        let mut map = self.sessions.lock().await;
        if let Some(s) = map.get(session_id) {
            return Ok(s.clone());
        }
        if map.len() >= MAX_ACTIVE_SESSIONS {
            return Err("maximum active shell sessions reached");
        }
        let state = Arc::new(build_session(self.rpc.clone(), self.command_timeout()));
        map.insert(session_id.to_string(), state.clone());
        Ok(state)
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

        let deadline = deadline_from_unix_ms(deadline_unix_ms, self.command_timeout());
        let state = match self.get_or_create(session_id).await {
            Ok(state) => state,
            Err(message) => return terminal_result(message, 2, false),
        };
        let (tx, rx) = tokio::sync::oneshot::channel();
        let execution_id = self.next_execution_id.fetch_add(1, Ordering::Relaxed);

        {
            let mut queue = state.control.queue.lock().await;
            if Instant::now() >= deadline {
                return terminal_result("command timed out; shell session reset", 124, true);
            }
            if queue.len() >= MAX_QUEUED_COMMANDS_PER_SESSION {
                return terminal_result(
                    "maximum queued commands for shell session reached",
                    2,
                    false,
                );
            }
            queue.push_back(QueuedCommand {
                execution_id,
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
            self.rpc
                .cancel_execution(state.control.active_execution_id.load(Ordering::SeqCst))
                .await;
            drain_queue(
                &state.control,
                "command cancelled; shell session reset",
                130,
            )
            .await;
            if let Ok(mut bash) = state.bash.try_lock() {
                state.cache.clear();
                *bash = build_bash(self.rpc.clone(), state.cache.clone(), state.command_timeout);
                rebind_cancel_flag(&state.control, bash.cancellation_token());
            }
        }
    }

    pub async fn close(&self, session_id: &str) {
        let mut map = self.sessions.lock().await;
        if let Some(state) = map.remove(session_id) {
            signal_cancel(&state.control);
            self.rpc
                .cancel_execution(state.control.active_execution_id.load(Ordering::SeqCst))
                .await;
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

fn build_session(rpc: SharedRpc, command_timeout: Duration) -> SessionState {
    let cache = VfsCache::default();
    let bash = build_bash(rpc, cache.clone(), command_timeout);
    let cancel_flag = bash.cancellation_token();
    cancel_flag.store(false, Ordering::SeqCst);
    SessionState {
        control: Arc::new(SessionControl {
            cancel_flag: RwLock::new(cancel_flag),
            cancel_notify: Notify::new(),
            queue: AsyncMutex::new(VecDeque::new()),
            running: AtomicBool::new(false),
            active_execution_id: AtomicU64::new(0),
        }),
        bash: AsyncMutex::new(bash),
        cache,
        command_timeout,
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

        state
            .control
            .active_execution_id
            .store(next.execution_id, Ordering::SeqCst);

        if Instant::now() >= next.deadline
            || state.control.cancel_flag.read().load(Ordering::SeqCst)
        {
            let (msg, code) = if state.control.cancel_flag.read().load(Ordering::SeqCst) {
                ("command cancelled; shell session reset", 130)
            } else {
                ("command timed out; shell session reset", 124)
            };
            let _ = next.response.send(terminal_result(msg, code, true));
            reset_session(&state, &rpc, next.execution_id).await;
            continue;
        }

        state.cache.clear();

        let remaining = next.deadline.saturating_duration_since(Instant::now());
        let cancel_flag = state.control.cancel_flag.read().clone();
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
            let result = with_execution_id(
                next.execution_id,
                bash.exec_streaming(
                    &command,
                    Box::new(move |stdout_chunk, stderr_chunk| {
                        let mut guard = merged_cb.lock();
                        append_stream_chunk(&mut guard, stdout_chunk, stderr_chunk);
                        if guard.len() >= MAX_TERMINAL_OUTPUT_BYTES {
                            limit_hit_cb.store(true, Ordering::SeqCst);
                            cancel_output.store(true, Ordering::SeqCst);
                        }
                    }),
                ),
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
                rpc.cancel_execution(next.execution_id).await;
                // Let bashkit observe cancellation and drop the bash lock.
                let _ = exec.await;
                let (msg, code) = if Instant::now() >= next.deadline {
                    ("command timed out; shell session reset", 124)
                } else {
                    ("command cancelled; shell session reset", 130)
                };
                let _ = next.response.send(terminal_result(msg, code, true));
                reset_session(&state, &rpc, next.execution_id).await;
                continue;
            }
        };

        let (payload, reset_required) = if output_limit_hit {
            (format_output_limit_result(ordered), true)
        } else {
            match result {
                Ok(exec_result) => {
                    let payload = format_exec_result(exec_result, ordered);
                    let reset = payload.get("session_reset").and_then(Value::as_bool) == Some(true);
                    (payload, reset)
                }
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
                    (
                        terminal_result(
                            &sanitize_error_message(&msg),
                            code,
                            code == 124 || code == 130,
                        ),
                        code == 124 || code == 130,
                    )
                }
            }
        };
        if reset_required {
            reset_session(&state, &rpc, next.execution_id).await;
        }
        state.cache.clear();
        state.control.active_execution_id.store(0, Ordering::SeqCst);
        let _ = next.response.send(payload);
    }
}

async fn reset_session(state: &Arc<SessionState>, rpc: &SharedRpc, execution_id: u64) {
    rpc.cancel_execution(execution_id).await;
    drain_queue(&state.control, "shell session reset before execution", 125).await;
    let mut bash = state.bash.lock().await;
    state.cache.clear();
    *bash = build_bash(rpc.clone(), state.cache.clone(), state.command_timeout);
    rebind_cancel_flag(&state.control, bash.cancellation_token());
    state.control.active_execution_id.store(0, Ordering::SeqCst);
}

fn build_bash(rpc: SharedRpc, cache: VfsCache, command_timeout: Duration) -> Bash {
    let fs = build_readonly_fs(rpc.clone(), cache);
    let domain = DomainBuiltins::new(rpc);
    let forbidden: Arc<Mutex<Vec<&'static str>>> =
        Arc::new(Mutex::new(FORBIDDEN_COMMANDS.to_vec()));
    let brace_expansion_present = Arc::new(AtomicBool::new(false));

    let mut builder = BashBuilder::default()
        .fs(fs)
        .cwd("/")
        .username("agent")
        .hostname("patchouli")
        .limits(execution_limits(command_timeout))
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

fn deadline_from_unix_ms(deadline_unix_ms: Option<i64>, command_timeout: Duration) -> Instant {
    let now = Instant::now();
    let default = now + command_timeout;
    let Some(ms) = deadline_unix_ms else {
        return default;
    };
    if ms < 0 {
        return now;
    }
    let target = UNIX_EPOCH + Duration::from_millis(ms as u64);
    let now_sys = SystemTime::now();
    match target.duration_since(now_sys) {
        Ok(d) => now + d.min(command_timeout),
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
    async fn active_session_limit_returns_explicit_error() {
        let sessions = SessionManager::new(test_rpc());
        for index in 0..MAX_ACTIVE_SESSIONS {
            sessions
                .get_or_create(&format!("session-{index}"))
                .await
                .expect("session within limit");
        }

        let result = sessions.execute("one-too-many", "pwd", None).await;

        assert_eq!(result["exit_code"], 2);
        assert!(result["text"]
            .as_str()
            .unwrap_or("")
            .contains("maximum active shell sessions reached"));
    }

    #[tokio::test]
    async fn per_session_queue_limit_returns_explicit_error() {
        let sessions = SessionManager::new(test_rpc());
        let state = sessions.get_or_create("full-queue").await.unwrap();
        state.control.running.store(true, Ordering::SeqCst);
        let mut queue = state.control.queue.lock().await;
        for execution_id in 1..=MAX_QUEUED_COMMANDS_PER_SESSION as u64 {
            let (response, _) = tokio::sync::oneshot::channel();
            queue.push_back(QueuedCommand {
                execution_id,
                command: "pwd".to_string(),
                deadline: Instant::now() + Duration::from_secs(5),
                response,
            });
        }
        drop(queue);

        let result = sessions.execute("full-queue", "pwd", None).await;

        assert_eq!(result["exit_code"], 2);
        assert!(result["text"]
            .as_str()
            .unwrap_or("")
            .contains("maximum queued commands for shell session reached"));
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
        let next = sessions
            .execute("trunc", "echo ${AFTER_TRUNCATION:-reset}", None)
            .await;
        assert_eq!(next["exit_code"], 0, "{next}");
        assert!(
            next["text"].as_str().unwrap_or("").contains("reset"),
            "{next}"
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

    #[test]
    fn runtime_timeout_is_clamped_to_compiled_bounds() {
        let sessions = SessionManager::new(test_rpc());
        assert_eq!(
            sessions.set_command_timeout_ms(Some(1)),
            MIN_COMMAND_TIMEOUT
        );
        assert_eq!(
            sessions.set_command_timeout_ms(Some(120_000)),
            MAX_COMMAND_TIMEOUT
        );
        assert_eq!(
            sessions.set_command_timeout_ms(Some(7_500)),
            Duration::from_millis(7_500)
        );
    }

    #[test]
    fn command_deadline_uses_runtime_timeout_as_upper_bound() {
        let started = Instant::now();
        let deadline = deadline_from_unix_ms(None, Duration::from_secs(2));
        let elapsed = deadline.saturating_duration_since(started);
        assert!(elapsed >= Duration::from_millis(1_900));
        assert!(elapsed <= Duration::from_millis(2_100));
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
