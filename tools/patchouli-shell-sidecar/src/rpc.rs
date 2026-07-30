use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use parking_lot::Mutex as ParkingMutex;
use serde_json::Value;
use tokio::io::{AsyncWrite, AsyncWriteExt};
use tokio::sync::{oneshot, Mutex};

use crate::protocol::{Envelope, MessageType};

pub struct RpcHost {
    stdout: Mutex<Box<dyn AsyncWrite + Send + Unpin>>,
    next_id: AtomicU64,
    pending: ParkingMutex<HashMap<u64, PendingCall>>,
}

struct PendingCall {
    execution_id: u64,
    response: oneshot::Sender<Envelope>,
}

tokio::task_local! {
    static EXECUTION_ID: u64;
}

impl RpcHost {
    pub fn new(stdout: impl AsyncWrite + Send + Unpin + 'static) -> Self {
        Self {
            stdout: Mutex::new(Box::new(stdout)),
            // Rust uses even request IDs
            next_id: AtomicU64::new(2),
            pending: ParkingMutex::new(HashMap::new()),
        }
    }

    pub async fn send_envelope(&self, envelope: &Envelope) -> Result<(), std::io::Error> {
        let bytes = serde_json::to_vec(envelope)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e.to_string()))?;
        if bytes.len() > crate::limits::MAX_RPC_FRAME_BYTES {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "frame too large",
            ));
        }
        let mut out = self.stdout.lock().await;
        let len = (bytes.len() as u32).to_be_bytes();
        out.write_all(&len).await?;
        out.write_all(&bytes).await?;
        out.flush().await?;
        Ok(())
    }

    pub async fn send_notification(
        &self,
        method: &str,
        payload: Value,
    ) -> Result<(), std::io::Error> {
        self.send_envelope(&Envelope::notification(method, payload))
            .await
    }

    pub async fn respond_ok(
        &self,
        request_id: Option<u64>,
        payload: Value,
    ) -> Result<(), std::io::Error> {
        self.send_envelope(&Envelope::response_ok(request_id, payload))
            .await
    }

    pub async fn respond_error(
        &self,
        request_id: Option<u64>,
        code: &str,
        message: impl Into<String>,
    ) -> Result<(), std::io::Error> {
        self.send_envelope(&Envelope::response_err(request_id, code, message))
            .await
    }

    pub async fn call(&self, method: &str, payload: Value) -> Result<Value, RpcCallError> {
        let execution_id = EXECUTION_ID
            .try_with(|execution_id| *execution_id)
            .map_err(|_| {
                RpcCallError::Io("reverse RPC call has no execution context".to_string())
            })?;
        let id = self.next_id.fetch_add(2, Ordering::Relaxed);
        let (tx, rx) = oneshot::channel();
        self.pending.lock().insert(
            id,
            PendingCall {
                execution_id,
                response: tx,
            },
        );
        let mut envelope = Envelope::request(id, method, payload);
        envelope.execution_id = Some(execution_id);
        if let Err(error) = self.send_envelope(&envelope).await {
            self.pending.lock().remove(&id);
            return Err(RpcCallError::Io(error.to_string()));
        }
        let response = rx.await.map_err(|_| RpcCallError::Cancelled)?;
        if let Some(err) = response.error {
            return Err(RpcCallError::Domain {
                code: err.code,
                message: err.message,
            });
        }
        Ok(response.payload.unwrap_or(Value::Null))
    }

    pub async fn complete_response(&self, envelope: Envelope) {
        if envelope.message_type != MessageType::Response {
            return;
        }
        if let Some(id) = envelope.request_id {
            if let Some(pending) = self.pending.lock().remove(&id) {
                let _ = pending.response.send(envelope);
            }
        }
    }

    pub async fn cancel_execution(&self, execution_id: u64) {
        let cancelled = {
            let mut pending = self.pending.lock();
            let request_ids: Vec<_> = pending
                .iter()
                .filter_map(|(id, call)| (call.execution_id == execution_id).then_some(*id))
                .collect();
            for request_id in &request_ids {
                if let Some(call) = pending.remove(request_id) {
                    let _ = call.response.send(Envelope::response_err(
                        Some(*request_id),
                        "cancelled",
                        "request cancelled",
                    ));
                }
            }
            request_ids
        };

        if !cancelled.is_empty() {
            let mut envelope = Envelope::notification(
                "reverse.cancel",
                serde_json::json!({ "request_ids": cancelled }),
            );
            envelope.execution_id = Some(execution_id);
            let _ = self.send_envelope(&envelope).await;
        }
    }

    pub fn cancel_all_pending(&self) {
        let mut pending = self.pending.lock();
        for (request_id, call) in pending.drain() {
            let _ = call.response.send(Envelope::response_err(
                Some(request_id),
                "cancelled",
                "request cancelled",
            ));
        }
    }
}

#[derive(Debug, thiserror::Error)]
pub enum RpcCallError {
    #[error("io: {0}")]
    Io(String),
    #[error("{code}: {message}")]
    Domain { code: String, message: String },
    #[error("cancelled")]
    Cancelled,
}

pub async fn with_execution_id<F: std::future::Future>(execution_id: u64, future: F) -> F::Output {
    EXECUTION_ID.scope(execution_id, future).await
}

pub type SharedRpc = Arc<RpcHost>;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::PROTOCOL_VERSION;
    use tokio::io::{AsyncReadExt, DuplexStream};

    async fn read_envelope(stream: &mut DuplexStream) -> Envelope {
        let mut length = [0; 4];
        stream.read_exact(&mut length).await.unwrap();
        let mut payload = vec![0; u32::from_be_bytes(length) as usize];
        stream.read_exact(&mut payload).await.unwrap();
        serde_json::from_slice(&payload).unwrap()
    }

    #[tokio::test]
    async fn cancelling_one_execution_leaves_other_reverse_call_pending() {
        let (writer, mut reader) = tokio::io::duplex(4096);
        let host = Arc::new(RpcHost::new(writer));
        let call_a = tokio::spawn({
            let host = host.clone();
            async move { with_execution_id(11, host.call("vfs.read", Value::Null)).await }
        });
        let call_b = tokio::spawn({
            let host = host.clone();
            async move { with_execution_id(22, host.call("vfs.read", Value::Null)).await }
        });

        let first = read_envelope(&mut reader).await;
        let second = read_envelope(&mut reader).await;
        let (request_a, request_b) = if first.execution_id == Some(11) {
            (first.request_id.unwrap(), second.request_id.unwrap())
        } else {
            (second.request_id.unwrap(), first.request_id.unwrap())
        };

        host.cancel_execution(11).await;
        let cancel = read_envelope(&mut reader).await;
        assert_eq!(cancel.method.as_deref(), Some("reverse.cancel"));
        assert_eq!(cancel.execution_id, Some(11));
        assert!(
            matches!(call_a.await.unwrap(), Err(RpcCallError::Domain { code, .. }) if code == "cancelled")
        );
        assert!(!call_b.is_finished());

        host.complete_response(Envelope {
            protocol_version: PROTOCOL_VERSION.to_string(),
            message_type: MessageType::Response,
            request_id: Some(request_b),
            execution_id: None,
            method: None,
            payload: Some(serde_json::json!({ "ok": true })),
            error: None,
        })
        .await;
        assert_eq!(call_b.await.unwrap().unwrap()["ok"], true);
        assert_ne!(request_a, request_b);
    }
}
