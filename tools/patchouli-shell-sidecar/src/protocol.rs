use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const PROTOCOL_VERSION: &str = "1";

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum MessageType {
    Request,
    Response,
    Notification,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Envelope {
    pub protocol_version: String,
    pub message_type: MessageType,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub request_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub execution_id: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub method: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payload: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<RpcError>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RpcError {
    pub code: String,
    pub message: String,
}

impl Envelope {
    pub fn request(request_id: u64, method: impl Into<String>, payload: Value) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.to_string(),
            message_type: MessageType::Request,
            request_id: Some(request_id),
            execution_id: None,
            method: Some(method.into()),
            payload: Some(payload),
            error: None,
        }
    }

    pub fn response_ok(request_id: Option<u64>, payload: Value) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.to_string(),
            message_type: MessageType::Response,
            request_id,
            execution_id: None,
            method: None,
            payload: Some(payload),
            error: None,
        }
    }

    pub fn response_err(
        request_id: Option<u64>,
        code: impl Into<String>,
        message: impl Into<String>,
    ) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.to_string(),
            message_type: MessageType::Response,
            request_id,
            execution_id: None,
            method: None,
            payload: None,
            error: Some(RpcError {
                code: code.into(),
                message: message.into(),
            }),
        }
    }

    pub fn notification(method: impl Into<String>, payload: Value) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.to_string(),
            message_type: MessageType::Notification,
            request_id: None,
            execution_id: None,
            method: Some(method.into()),
            payload: Some(payload),
            error: None,
        }
    }
}
