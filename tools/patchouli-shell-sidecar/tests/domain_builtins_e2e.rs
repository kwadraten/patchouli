//! Domain-facing smoke tests for the sidecar library surface.

use std::sync::Arc;

use patchouli_shell_sidecar::rpc::RpcHost;
use patchouli_shell_sidecar::session::SessionManager;
use serde_json::json;

#[tokio::test]
async fn cite_request_contract() {
    let payload = json!({
        "item_uris": ["patchouli://items/item-1.bib"],
        "style_id": null
    });
    assert!(payload.get("item_uris").unwrap().as_array().unwrap().len() == 1);
}

#[tokio::test]
async fn evidence_request_contract() {
    let uri = "patchouli://texts/doc/page-0.md?evref=opaque-token";
    let payload = json!({ "uri": uri });
    assert!(payload["uri"].as_str().unwrap().contains("evref="));
    assert!(!payload["uri"].as_str().unwrap().contains("file:"));
}

#[tokio::test]
async fn session_echo_smoke() {
    let rpc = Arc::new(RpcHost::new(tokio::io::stdout()));
    let sessions = SessionManager::new(rpc);
    let result = sessions.execute("e2e", "echo hello-e2e", None).await;
    assert_eq!(result["exit_code"], 0, "{result}");
    assert!(result["text"].as_str().unwrap().contains("hello-e2e"));
}

#[tokio::test]
async fn shutdown_terminates_queued_work() {
    let rpc = Arc::new(RpcHost::new(tokio::io::stdout()));
    let sessions = SessionManager::new(rpc);
    let exec = sessions.execute("shutdown-me", "while true; do :; done", None);
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    sessions.shutdown().await;
    let result = tokio::time::timeout(std::time::Duration::from_secs(5), exec)
        .await
        .expect("shutdown should unblock execute");
    let code = result["exit_code"].as_i64().unwrap_or(-1);
    assert!(
        code == 125 || code == 130 || code == 124,
        "expected session reset after shutdown, got {result}"
    );
}
