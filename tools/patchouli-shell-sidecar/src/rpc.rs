use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use parking_lot::Mutex as ParkingMutex;
use serde_json::Value;
use tokio::io::{AsyncWriteExt, Stdout};
use tokio::sync::{oneshot, Mutex};

use crate::protocol::{Envelope, MessageType};

pub struct RpcHost {
    stdout: Mutex<Stdout>,
    next_id: AtomicU64,
    pending: ParkingMutex<HashMap<u64, oneshot::Sender<Envelope>>>,
}

impl RpcHost {
    pub fn new(stdout: Stdout) -> Self {
        Self {
            stdout: Mutex::new(stdout),
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
        let id = self.next_id.fetch_add(2, Ordering::Relaxed);
        let (tx, rx) = oneshot::channel();
        self.pending.lock().insert(id, tx);
        self.send_envelope(&Envelope::request(id, method, payload))
            .await
            .map_err(|e| RpcCallError::Io(e.to_string()))?;
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
            if let Some(tx) = self.pending.lock().remove(&id) {
                let _ = tx.send(envelope);
            }
        }
    }

    pub fn cancel_all_pending(&self) {
        let mut pending = self.pending.lock();
        for (_, tx) in pending.drain() {
            let _ = tx.send(Envelope::response_err(
                None,
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

pub type SharedRpc = Arc<RpcHost>;
