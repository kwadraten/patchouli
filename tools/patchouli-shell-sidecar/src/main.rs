use std::process::ExitCode;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use patchouli_shell_sidecar::limits;
use patchouli_shell_sidecar::parent_watch;
use patchouli_shell_sidecar::protocol::{Envelope, MessageType, PROTOCOL_VERSION};
use patchouli_shell_sidecar::rpc::RpcHost;
use patchouli_shell_sidecar::session::SessionManager;
use tokio::io::{AsyncReadExt, BufReader};
use tokio::sync::Notify;

#[tokio::main]
async fn main() -> ExitCode {
    match run().await {
        Ok(()) => ExitCode::SUCCESS,
        Err(code) => ExitCode::from(code),
    }
}

async fn run() -> Result<(), u8> {
    parent_watch::spawn();

    let stdin = tokio::io::stdin();
    let stdout = tokio::io::stdout();
    let host = Arc::new(RpcHost::new(stdout));
    let sessions = Arc::new(SessionManager::new(host.clone()));
    let initialized = Arc::new(AtomicBool::new(false));
    let shutdown = Arc::new(Notify::new());

    host.send_notification(
        "hello",
        serde_json::json!({
            "protocol_version": PROTOCOL_VERSION,
            "sidecar": "patchouli-shell-sidecar",
            "bashkit": "0.14.4"
        }),
    )
    .await
    .map_err(|_| 2u8)?;

    let mut reader = BufReader::new(stdin);

    loop {
        let frame = tokio::select! {
            biased;
            _ = shutdown.notified() => break,
            frame = read_frame(&mut reader) => match frame {
                Ok(Some(bytes)) => bytes,
                Ok(None) => break,
                Err(_) => return Err(2),
            },
        };

        let envelope: Envelope = match serde_json::from_slice(&frame) {
            Ok(v) => v,
            Err(_) => {
                let _ = host
                    .send_notification(
                        "protocol.error",
                        serde_json::json!({"code": "invalid_frame", "message": "JSON parse failed"}),
                    )
                    .await;
                return Err(2);
            }
        };

        if envelope.protocol_version != PROTOCOL_VERSION {
            let _ = host
                .respond_error(
                    envelope.request_id,
                    "protocol_incompatible",
                    format!(
                        "expected protocol_version {PROTOCOL_VERSION}, got {}",
                        envelope.protocol_version
                    ),
                )
                .await;
            return Err(3);
        }

        match envelope.message_type {
            MessageType::Request => {
                let host = host.clone();
                let sessions = sessions.clone();
                let initialized = initialized.clone();
                let shutdown = shutdown.clone();
                tokio::spawn(async move {
                    handle_request(host, sessions, initialized, shutdown, envelope).await;
                });
            }
            MessageType::Response => {
                host.complete_response(envelope).await;
            }
            MessageType::Notification => {}
        }
    }

    Ok(())
}

async fn handle_request(
    host: Arc<RpcHost>,
    sessions: Arc<SessionManager>,
    initialized: Arc<AtomicBool>,
    shutdown: Arc<Notify>,
    envelope: Envelope,
) {
    let method = envelope.method.clone().unwrap_or_default();
    let request_id = envelope.request_id;
    let payload = envelope.payload.clone().unwrap_or(serde_json::Value::Null);

    match method.as_str() {
        "initialize" => {
            let supports_scoped_cancellation = payload
                .get("capabilities")
                .and_then(|capabilities| capabilities.get("execution_scoped_reverse_cancellation"))
                .and_then(|value| value.as_bool())
                == Some(true);
            if !supports_scoped_cancellation {
                let _ = host
                    .respond_error(
                        request_id,
                        "protocol_incompatible",
                        "host must support execution-scoped reverse cancellation",
                    )
                    .await;
                return;
            }
            let requested_timeout_ms = payload
                .get("limits")
                .and_then(|limits| limits.get("command_timeout_ms"))
                .and_then(|value| value.as_u64());
            let command_timeout = sessions.set_command_timeout_ms(requested_timeout_ms);
            initialized.store(true, Ordering::SeqCst);
            let _ = host
                .respond_ok(
                    request_id,
                    serde_json::json!({
                        "protocol_version": PROTOCOL_VERSION,
                        "status": "ready",
                        "capabilities": {
                            "execution_scoped_reverse_cancellation": true
                        },
                        "limits": {
                            "command_timeout_ms": command_timeout.as_millis(),
                            "max_terminal_output_bytes": limits::MAX_TERMINAL_OUTPUT_BYTES,
                            "max_commands": limits::MAX_COMMANDS,
                            "max_loop_iterations": limits::MAX_LOOP_ITERATIONS,
                            "max_function_depth": limits::MAX_FUNCTION_DEPTH,
                            "max_string_bytes": limits::MAX_STRING_BYTES,
                            "max_active_sessions": patchouli_shell_sidecar::session::MAX_ACTIVE_SESSIONS,
                            "max_queued_commands_per_session": patchouli_shell_sidecar::session::MAX_QUEUED_COMMANDS_PER_SESSION
                        }
                    }),
                )
                .await;
            let _ = host
                .send_notification(
                    "ready",
                    serde_json::json!({"protocol_version": PROTOCOL_VERSION}),
                )
                .await;
        }
        "shell.execute" => {
            if !initialized.load(Ordering::SeqCst) {
                let _ = host
                    .respond_error(request_id, "not_ready", "sidecar not initialized")
                    .await;
                return;
            }
            let session_id = payload
                .get("session_id")
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string();
            let command = payload
                .get("command")
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_string();
            let deadline_ms = payload.get("deadline_unix_ms").and_then(|v| v.as_i64());
            let result = sessions.execute(&session_id, &command, deadline_ms).await;
            let _ = host.respond_ok(request_id, result).await;
        }
        "session.close" => {
            let session_id = payload
                .get("session_id")
                .and_then(|v| v.as_str())
                .unwrap_or("");
            sessions.close(session_id).await;
            let _ = host
                .respond_ok(request_id, serde_json::json!({"closed": true}))
                .await;
        }
        "cancel" => {
            let session_id = payload
                .get("session_id")
                .and_then(|v| v.as_str())
                .unwrap_or("");
            sessions.cancel(session_id).await;
            let _ = host
                .respond_ok(request_id, serde_json::json!({"cancelled": true}))
                .await;
        }
        "shutdown" => {
            sessions.shutdown().await;
            let _ = host
                .respond_ok(request_id, serde_json::json!({"shutdown": true}))
                .await;
            shutdown.notify_one();
        }
        other => {
            let _ = host
                .respond_error(
                    request_id,
                    "unknown_method",
                    format!("unknown method: {other}"),
                )
                .await;
        }
    }
}

async fn read_frame<R: AsyncReadExt + Unpin>(
    reader: &mut R,
) -> Result<Option<Vec<u8>>, std::io::Error> {
    let mut len_buf = [0u8; 4];
    match reader.read_exact(&mut len_buf).await {
        Ok(_) => {}
        Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(e),
    }
    let len = u32::from_be_bytes(len_buf) as usize;
    if len == 0 || len > limits::MAX_RPC_FRAME_BYTES {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "frame too large",
        ));
    }
    let mut buf = vec![0u8; len];
    reader.read_exact(&mut buf).await?;
    Ok(Some(buf))
}
