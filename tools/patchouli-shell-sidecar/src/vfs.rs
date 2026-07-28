use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, UNIX_EPOCH};

use async_trait::async_trait;
use bashkit::{DirEntry, FileSystem, FileType, FsBackend, Metadata, PosixFs, Result};
use serde_json::{json, Value};

use crate::rpc::{RpcCallError, SharedRpc};

fn ro_err() -> bashkit::Error {
    std::io::Error::new(std::io::ErrorKind::PermissionDenied, "read-only filesystem").into()
}

#[derive(Clone)]
pub struct PatchouliFsBackend {
    rpc: SharedRpc,
}

impl PatchouliFsBackend {
    pub fn new(rpc: SharedRpc) -> Self {
        Self { rpc }
    }

    fn path_str(path: &Path) -> String {
        let s = path.to_string_lossy().replace('\\', "/");
        if s.is_empty() {
            "/".to_string()
        } else if s.starts_with('/') {
            s
        } else {
            format!("/{s}")
        }
    }

    async fn domain_call(&self, method: &str, payload: Value) -> Result<Value> {
        self.rpc.call(method, payload).await.map_err(map_rpc_err)
    }
}

#[async_trait]
impl FsBackend for PatchouliFsBackend {
    async fn read(&self, path: &Path) -> Result<Vec<u8>> {
        let payload = self
            .domain_call("vfs.read", json!({ "path": Self::path_str(path) }))
            .await?;
        let content = payload
            .get("content")
            .and_then(|v| v.as_str())
            .unwrap_or("");
        Ok(content.as_bytes().to_vec())
    }

    async fn write(&self, _path: &Path, _content: &[u8]) -> Result<()> {
        Err(ro_err())
    }

    async fn append(&self, _path: &Path, _content: &[u8]) -> Result<()> {
        Err(ro_err())
    }

    async fn mkdir(&self, _path: &Path, _recursive: bool) -> Result<()> {
        Err(ro_err())
    }

    async fn remove(&self, _path: &Path, _recursive: bool) -> Result<()> {
        Err(ro_err())
    }

    async fn rename(&self, _from: &Path, _to: &Path) -> Result<()> {
        Err(ro_err())
    }

    async fn copy(&self, _from: &Path, _to: &Path) -> Result<()> {
        Err(ro_err())
    }

    async fn symlink(&self, _target: &Path, _link: &Path) -> Result<()> {
        Err(ro_err())
    }

    async fn read_link(&self, _path: &Path) -> Result<PathBuf> {
        Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "not a symlink").into())
    }

    async fn chmod(&self, _path: &Path, _mode: u32) -> Result<()> {
        Err(ro_err())
    }

    async fn stat(&self, path: &Path) -> Result<Metadata> {
        let payload = self
            .domain_call("vfs.stat", json!({ "path": Self::path_str(path) }))
            .await?;
        Ok(metadata_from_json(&payload))
    }

    async fn read_dir(&self, path: &Path) -> Result<Vec<DirEntry>> {
        let payload = self
            .domain_call(
                "vfs.list",
                json!({
                    "path": Self::path_str(path),
                    "limit": 1000,
                }),
            )
            .await?;
        let entries = payload
            .get("entries")
            .and_then(|v| v.as_array())
            .cloned()
            .unwrap_or_default();
        Ok(entries
            .into_iter()
            .filter_map(|e| {
                let name = e.get("name")?.as_str()?.to_string();
                Some(DirEntry {
                    name,
                    metadata: metadata_from_json(&e),
                })
            })
            .collect())
    }

    async fn exists(&self, path: &Path) -> Result<bool> {
        match self
            .domain_call("vfs.resolve", json!({ "path": Self::path_str(path) }))
            .await
        {
            Ok(v) => Ok(v.get("exists").and_then(|x| x.as_bool()).unwrap_or(false)),
            Err(e) => {
                let msg = format!("{e}");
                if msg.contains("not_found") {
                    Ok(false)
                } else {
                    Err(e)
                }
            }
        }
    }
}

fn metadata_from_json(value: &Value) -> Metadata {
    let kind = value
        .get("kind")
        .or_else(|| value.get("type"))
        .and_then(|v| v.as_str())
        .unwrap_or("file");
    let file_type = match kind {
        "directory" | "dir" => FileType::Directory,
        _ => FileType::File,
    };
    let size = value.get("size").and_then(|v| v.as_u64()).unwrap_or(0);
    let epoch = UNIX_EPOCH + Duration::from_secs(1_700_000_000);
    Metadata {
        file_type,
        size,
        mode: if file_type.is_dir() { 0o555 } else { 0o444 },
        modified: epoch,
        created: epoch,
    }
}

fn map_rpc_err(err: RpcCallError) -> bashkit::Error {
    match err {
        RpcCallError::Domain { code, message } => {
            let kind = if code.contains("not_found") {
                std::io::ErrorKind::NotFound
            } else if code.contains("permission") || code == "read_only" {
                std::io::ErrorKind::PermissionDenied
            } else {
                std::io::ErrorKind::Other
            };
            std::io::Error::new(kind, format!("{code}: {message}")).into()
        }
        RpcCallError::Io(msg) => std::io::Error::other(msg).into(),
        RpcCallError::Cancelled => {
            std::io::Error::new(std::io::ErrorKind::Interrupted, "cancelled").into()
        }
    }
}

pub fn build_readonly_fs(rpc: SharedRpc) -> Arc<dyn FileSystem> {
    let backend = PatchouliFsBackend::new(rpc);
    Arc::new(PosixFs::new(backend))
}
