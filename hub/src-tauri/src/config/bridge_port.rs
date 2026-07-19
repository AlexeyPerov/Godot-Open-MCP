//! Deterministic per-project bridge port — the Rust arm of the three-way
//! contract shared with the C# bridge (`InstancePortResolver.cs`) and the
//! TS `mcp-server`/`cli` (`instance-discovery.ts`).
//!
//! The formula MUST agree byte-for-byte across all three languages so the
//! Hub previews/writes the same `GODOT_OPEN_MCP_BRIDGE_PORT` the bridge
//! actually listens on and the MCP server probes:
//!
//!   1. Normalize the path: `\` → `/`, trim trailing `/` (keep root `/`),
//!      NO lowercasing.
//!   2. SHA-256 (lowercase hex) of the normalized UTF-8 bytes.
//!   3. Port = `20000 + (first 16 hex chars as u64) % 10000`.
//!
//! Resolution precedence (mirrors `resolvePort`):
//!   1. explicit override port (valid 1..=65535) wins;
//!   2. a *live* instance lock's recorded port;
//!   3. the deterministic hash.

use std::path::PathBuf;

use serde::Deserialize;
use sha2::{Digest, Sha256};

pub const PORT_RANGE_START: u64 = 20000;
pub const PORT_RANGE_SIZE: u64 = 10000;

/// Env-var name that overrides the deterministic port. Mirrors the bridge
/// `InstancePortResolver.PortOverrideEnvVar` and the TS
/// `PORT_OVERRIDE_ENV_VAR`.
pub const PORT_OVERRIDE_ENV_VAR: &str = "GODOT_OPEN_MCP_BRIDGE_PORT";

/// A heartbeat older than this is considered stale (mirrors
/// `HEARTBEAT_STALE_MS`). Used only on non-unix as a liveness proxy.
pub const HEARTBEAT_STALE_MS: i64 = 10_000;

/// Path normalization applied BEFORE hashing (mirrors the TS/C#).
pub fn normalize_path(project_path: &str) -> String {
    if project_path.is_empty() {
        return String::new();
    }
    let mut norm = project_path.replace('\\', "/");
    while norm.len() > 1 && norm.ends_with('/') {
        norm.pop();
    }
    norm
}

/// Lowercase hex SHA-256 of the normalized project path.
pub fn project_hash(project_path: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(normalize_path(project_path).as_bytes());
    let digest = hasher.finalize();
    let mut out = String::with_capacity(64);
    for b in digest.iter() {
        use std::fmt::Write;
        let _ = write!(out, "{:02x}", b);
    }
    out
}

/// Deterministic port for a project path: `20000 + (sha256(path) % 10000)`.
/// Uses the first 8 bytes (16 hex chars) of the digest as a u64 so the
/// modulo matches the C# `UInt64` computation and the TS `BigInt` exactly.
pub fn compute_port(project_path: &str) -> u16 {
    let hash = project_hash(project_path);
    let prefix = u64::from_str_radix(&hash[..16], 16).unwrap_or(0);
    (PORT_RANGE_START + (prefix % PORT_RANGE_SIZE)) as u16
}

/// Base scratch dir shared by the bridge + MCP server (`~/.godot-open-mcp`).
pub fn status_dir() -> PathBuf {
    let home = dirs::home_dir().unwrap_or_else(|| PathBuf::from("."));
    home.join(".godot-open-mcp")
}

/// Directory holding one lock file per running bridge instance.
pub fn instances_dir() -> PathBuf {
    status_dir().join("instances")
}

/// Path to this project's instance lock file.
pub fn lock_path(project_path: &str) -> PathBuf {
    instances_dir().join(format!("{}.json", project_hash(project_path)))
}

/// Subset of `~/.godot-open-mcp/instances/<hash>.json` we care about.
#[derive(Debug, Deserialize)]
struct InstanceLock {
    pid: i32,
    port: u16,
    #[serde(default)]
    #[serde(rename = "heartbeatAt")]
    heartbeat_at: Option<String>,
}

/// Read this project's instance lock from disk. Never errors — a missing or
/// unparseable lock yields `None`.
fn read_instance_lock(project_path: &str) -> Option<InstanceLock> {
    let path = lock_path(project_path);
    let raw = std::fs::read_to_string(path).ok()?;
    serde_json::from_str::<InstanceLock>(&raw).ok()
}

/// `kill -0` equivalent on unix. On other platforms we cannot cheaply probe a
/// PID without extra deps, so we treat the lock as live only when its
/// heartbeat is fresh (see `lock_is_live`).
#[cfg(unix)]
fn is_pid_alive(pid: i32) -> bool {
    if pid <= 0 {
        return false;
    }
    // SAFETY: kill with signal 0 performs no signal delivery; it only checks
    // whether the caller could signal the process. errno EPERM means the
    // process exists but we may not signal it → still "alive".
    let rc = unsafe { libc::kill(pid as libc::pid_t, 0) };
    if rc == 0 {
        return true;
    }
    std::io::Error::last_os_error().raw_os_error() == Some(libc::EPERM)
}

#[cfg(not(unix))]
fn is_pid_alive(_pid: i32) -> bool {
    // No cheap cross-platform probe without extra deps; the caller falls back
    // to a heartbeat-freshness check.
    false
}

/// Heartbeat age in ms; `i64::MAX` when absent/unparseable.
fn heartbeat_age_ms(lock: &InstanceLock, now_ms: i64) -> i64 {
    let Some(ref hb) = lock.heartbeat_at else {
        return i64::MAX;
    };
    match chrono::DateTime::parse_from_rfc3339(hb) {
        Ok(t) => (now_ms - t.timestamp_millis()).max(0),
        Err(_) => i64::MAX,
    }
}

/// Whether a lock represents a live bridge whose recorded port we should
/// trust. On unix: PID liveness (matches the TS). On other platforms: a
/// fresh heartbeat as a best-effort proxy.
fn lock_is_live(lock: &InstanceLock) -> bool {
    if is_pid_alive(lock.pid) {
        return true;
    }
    if cfg!(not(unix)) {
        let now = chrono::Utc::now().timestamp_millis();
        return heartbeat_age_ms(lock, now) < HEARTBEAT_STALE_MS;
    }
    false
}

/// Resolve the bridge port for a project with override precedence:
///   1. explicit override (valid 1..=65535);
///   2. live instance lock's port;
///   3. deterministic hash.
pub fn resolve_port(project_path: &str, override_port: Option<u16>) -> u16 {
    if let Some(p) = override_port {
        if p >= 1 {
            return p;
        }
    }
    if let Some(lock) = read_instance_lock(project_path) {
        if lock_is_live(&lock) {
            return lock.port;
        }
    }
    compute_port(project_path)
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── path normalization ────────────────────────────────────────────────
    #[test]
    fn normalize_trims_trailing_slashes_not_root() {
        assert_eq!(normalize_path("/a/b/"), "/a/b");
        assert_eq!(normalize_path("/a/b///"), "/a/b");
        assert_eq!(normalize_path("/"), "/");
        assert_eq!(normalize_path(""), "");
    }

    #[test]
    fn normalize_converts_backslashes() {
        assert_eq!(normalize_path("C:\\Users\\me\\proj"), "C:/Users/me/proj");
    }

    // ── deterministic port fixtures (MUST match instance-discovery.ts) ──────
    // Computed from the shared formula; keep in lockstep with the TS/C# tests.
    #[test]
    fn compute_port_matches_shared_fixtures() {
        assert_eq!(compute_port("/Users/me/MyGame"), 21467);
        assert_eq!(compute_port("C:/Users/me/proj"), 21095);
        assert_eq!(compute_port("/x"), 21604);
        assert_eq!(compute_port("/opt/games/demo"), 26256);
    }

    #[test]
    fn compute_port_backslash_and_forwardslash_agree() {
        assert_eq!(
            compute_port("C:\\Users\\me\\proj"),
            compute_port("C:/Users/me/proj"),
        );
        assert_eq!(compute_port("C:\\Users\\me\\proj"), 21095);
    }

    #[test]
    fn compute_port_trailing_slash_is_normalized() {
        assert_eq!(compute_port("/a/b/"), compute_port("/a/b"));
        assert_eq!(compute_port("/a/b/"), 23597);
    }

    #[test]
    fn compute_port_in_range_and_stable() {
        let p = compute_port("/Users/me/MyGame");
        assert!((20000..=29999).contains(&p));
        assert_eq!(compute_port("/x"), compute_port("/x"));
    }

    #[test]
    fn project_hash_is_lowercase_hex_of_normalized() {
        // sha256("/Users/me/MyGame") begins 4e8811dda2757cbb…
        let h = project_hash("/Users/me/MyGame");
        assert_eq!(&h[..16], "4e8811dda2757cbb");
        assert_eq!(h.len(), 64);
        assert!(h.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
    }

    // ── resolve precedence ─────────────────────────────────────────────────
    #[test]
    fn resolve_override_wins() {
        assert_eq!(resolve_port("/any/project", Some(23456)), 23456);
    }

    #[test]
    fn resolve_no_override_no_lock_is_hash() {
        let p = "/no/such/project/for/resolve";
        assert_eq!(resolve_port(p, None), compute_port(p));
    }
}
