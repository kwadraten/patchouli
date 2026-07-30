use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, UNIX_EPOCH};

use async_trait::async_trait;
use bashkit::{DirEntry, FileSystem, FileType, FsBackend, Metadata, PosixFs, Result};
use serde_json::{json, Value};

use crate::limits::MAX_GLOB_EXPANSION_RESULTS;
use crate::rpc::{RpcCallError, SharedRpc};

fn ro_err() -> bashkit::Error {
    std::io::Error::new(std::io::ErrorKind::PermissionDenied, "read-only filesystem").into()
}

#[derive(Clone)]
pub struct PatchouliFsBackend {
    rpc: SharedRpc,
    cache: VfsCache,
}

#[derive(Clone, Default)]
pub struct VfsCache {
    values: Arc<parking_lot::Mutex<HashMap<String, Value>>>,
}

impl VfsCache {
    pub fn clear(&self) {
        self.values.lock().clear();
    }
}

impl PatchouliFsBackend {
    pub fn new(rpc: SharedRpc) -> Self {
        Self::with_cache(rpc, VfsCache::default())
    }

    pub fn with_cache(rpc: SharedRpc, cache: VfsCache) -> Self {
        Self { rpc, cache }
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
        let key = cache_key(method, &payload);
        if let Some(value) = self.cache.values.lock().get(&key).cloned() {
            return Ok(value);
        }
        let value = self.rpc.call(method, payload).await.map_err(map_rpc_err)?;
        self.cache.values.lock().insert(key, value.clone());
        Ok(value)
    }
}

fn cache_key(method: &str, payload: &Value) -> String {
    format!("{method}:{payload}")
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
            .domain_call("vfs.stat", stat_request(Self::path_str(path)))
            .await?;
        Ok(metadata_from_json(&payload))
    }

    async fn read_dir(&self, path: &Path) -> Result<Vec<DirEntry>> {
        let path = Self::path_str(path);
        let mut entries = Vec::new();
        let mut after = None;
        let mut seen_cursors = HashSet::new();
        loop {
            let mut request = json!({ "path": path, "limit": 1000 });
            if let Some(cursor) = &after {
                request["after"] = json!(cursor);
            }
            let payload = self.domain_call("vfs.list", request).await?;
            let page = payload
                .get("entries")
                .and_then(|v| v.as_array())
                .cloned()
                .unwrap_or_default();
            let next = next_after(&payload)?;
            if entries.len() + page.len() > MAX_GLOB_EXPANSION_RESULTS
                || (entries.len() + page.len() == MAX_GLOB_EXPANSION_RESULTS && next.is_some())
            {
                return Err(std::io::Error::other(format!(
                    "directory listing exceeds {MAX_GLOB_EXPANSION_RESULTS} results"
                ))
                .into());
            }
            entries.extend(page);
            let Some(cursor) = next else {
                break;
            };
            if seen_cursors.len() >= MAX_GLOB_EXPANSION_RESULTS {
                return Err(std::io::Error::other(format!(
                    "directory listing exceeds {MAX_GLOB_EXPANSION_RESULTS} continuations"
                ))
                .into());
            }
            if !seen_cursors.insert(cursor.clone()) {
                return Err(
                    std::io::Error::other("vfs.list returned a repeated continuation").into(),
                );
            }
            after = Some(cursor);
        }

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

fn stat_request(path: String) -> Value {
    json!({ "path": path, "include_size": false })
}

fn next_after(payload: &Value) -> Result<Option<String>> {
    if let Some(cursor) = payload.get("next_after") {
        return match cursor {
            Value::Null => Ok(None),
            Value::String(value) if !value.is_empty() => Ok(Some(value.clone())),
            _ => Err(std::io::Error::other("vfs.list returned an invalid next_after").into()),
        };
    }
    let Some(command) = payload.get("continuation_command").and_then(Value::as_str) else {
        return Ok(None);
    };
    if command.is_empty() {
        return Ok(None);
    }
    parse_after_argument(command).map(Some).ok_or_else(|| {
        std::io::Error::other("vfs.list returned an invalid continuation_command").into()
    })
}

fn parse_after_argument(command: &str) -> Option<String> {
    let marker = "--after";
    let start = command.match_indices(marker).find_map(|(index, _)| {
        let before = command[..index].chars().next_back();
        let after = command[index + marker.len()..].chars().next();
        (before.is_none_or(char::is_whitespace) && after.is_some_and(char::is_whitespace))
            .then_some(index + marker.len())
    })?;
    let rest = command.get(start..)?.trim_start();
    if rest.is_empty() {
        return None;
    }
    let (quote, rest) = match rest.as_bytes()[0] {
        b'\'' => (Some('\''), &rest[1..]),
        b'"' => (Some('"'), &rest[1..]),
        _ => (None, rest),
    };
    match quote {
        Some(quote) => rest.find(quote).map(|end| rest[..end].to_string()),
        None => Some(rest.split_whitespace().next()?.to_string()),
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

pub fn build_readonly_fs(rpc: SharedRpc, cache: VfsCache) -> Arc<dyn FileSystem> {
    let backend = PatchouliFsBackend::with_cache(rpc, cache);
    Arc::new(PosixFs::new(backend))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn continuation_prefers_machine_readable_cursor() {
        let payload = json!({
            "next_after": "patchouli://next",
            "continuation_command": "ls --after ignored /"
        });
        assert_eq!(
            next_after(&payload).unwrap().as_deref(),
            Some("patchouli://next")
        );
    }

    #[test]
    fn continuation_command_extracts_after_uri() {
        let payload = json!({
            "continuation_command": "ls --after patchouli://items/item-100 /items"
        });
        assert_eq!(
            next_after(&payload).unwrap().as_deref(),
            Some("patchouli://items/item-100")
        );
    }

    #[test]
    fn malformed_continuation_is_visible() {
        let payload = json!({ "continuation_command": "ls /items" });
        assert!(next_after(&payload).is_err());
        let payload = json!({ "continuation_command": "ls --afterthought nope /items" });
        assert!(next_after(&payload).is_err());
    }

    #[test]
    fn cache_is_shared_and_clearable() {
        let cache = VfsCache::default();
        cache
            .values
            .lock()
            .insert("key".into(), json!({"ok": true}));
        let clone = cache.clone();
        assert!(clone.values.lock().contains_key("key"));
        clone.clear();
        assert!(cache.values.lock().is_empty());
    }

    #[test]
    fn generic_stat_does_not_request_content_size() {
        assert_eq!(
            stat_request("/texts/page.md".to_string()),
            json!({ "path": "/texts/page.md", "include_size": false })
        );
    }
}
