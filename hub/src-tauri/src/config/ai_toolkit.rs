//! Install / launch / readiness glue — the Rust port of the CLI's
//! `install-plugin.ts` + `addon-source.ts`, `godot-editor.ts`, and the
//! `ping-poller` single-ping classifier.
//!
//! Three concerns:
//!   1. **Install** — materialize `res://addons/godot_open_mcp/` from a local
//!      source (monorepo `packages/bridge`) + bundle verify `Editor/`, then
//!      enable the plugin in `project.godot`. Idempotent (stage-then-swap).
//!   2. **Launch** — discover a Godot editor binary and spawn
//!      `<godot> --editor --path <project>` detached.
//!   3. **Ping** — one-shot `GET /ping` against the loopback bridge, classified
//!      into ready / compiling / offline (the wizard polls this).
//!
//! No cloud/HTTP-MCP env vars are ever set (ADR-001).

use std::io::{Read, Write};
use std::net::TcpStream;
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::Serialize;

use crate::config::bridge_port;
use crate::config::project_godot::{
    is_godot_project_root, project_godot_path, toggle_plugin_in_text, ToggleResult,
    GODOT_OPEN_MCP_PLUGIN_PATH,
};

// ─────────────────────────────────────────────────────────────────────────────
// Install
// ─────────────────────────────────────────────────────────────────────────────

const COPY_EXCLUDE_DIRS: &[&str] = &["Tests", "obj", "bin", ".godot"];
const COPY_EXCLUDE_FILES: &[&str] = &[".gitkeep", "AGENTS.md"];

/// Result of an install-addon operation.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallResult {
    pub ok: bool,
    /// True when files were copied and/or the plugin was newly enabled.
    pub changed: bool,
    pub addon_dir: String,
    pub project_godot_path: String,
    pub plugin_path: String,
    pub enabled_plugins: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error_label: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
}

fn install_fail(label: &str, message: &str, addon_dir: &Path, manifest: &Path) -> InstallResult {
    InstallResult {
        ok: false,
        changed: false,
        addon_dir: addon_dir.to_string_lossy().to_string(),
        project_godot_path: manifest.to_string_lossy().to_string(),
        plugin_path: GODOT_OPEN_MCP_PLUGIN_PATH.to_string(),
        enabled_plugins: vec![],
        error_label: Some(label.to_string()),
        warnings: vec![format!("{message}")],
    }
}

/// Resolve an addon source dir. Accepts either a dir that IS the addon root
/// (contains `plugin.cfg`) or a parent that contains
/// `addons/godot_open_mcp/plugin.cfg`. When `source` is None, falls back to
/// `<monorepo>/packages/bridge`.
fn resolve_addon_source(source: Option<&str>, monorepo: Option<&str>) -> Result<PathBuf, String> {
    let candidate: Option<PathBuf> = match source.filter(|s| !s.is_empty()) {
        Some(s) => Some(PathBuf::from(s)),
        None => monorepo
            .filter(|s| !s.is_empty())
            .map(|m| PathBuf::from(m).join("packages").join("bridge")),
    };
    let Some(dir) = candidate else {
        return Err(
            "No addon source. Pick a monorepo checkout (local source) or pass an explicit \
             addon source directory containing addons/godot_open_mcp/plugin.cfg."
                .to_string(),
        );
    };
    let resolved = dir;
    if resolved.join("plugin.cfg").exists() {
        return Ok(resolved);
    }
    let nested = resolved.join("addons").join("godot_open_mcp");
    if nested.join("plugin.cfg").exists() {
        return Ok(nested);
    }
    Err(format!(
        "{} does not contain a godot_open_mcp addon (no plugin.cfg at it or addons/godot_open_mcp/).",
        resolved.display()
    ))
}

/// Resolve the sibling verify `Editor/` dir to bundle into the addon tree.
fn resolve_verify_editor(addon_source: &Path) -> Option<PathBuf> {
    let c1 = addon_source.join("..").join("verify").join("Editor");
    if c1.exists() {
        return Some(c1);
    }
    let c2 = addon_source
        .join("..")
        .join("..")
        .join("packages")
        .join("verify")
        .join("Editor");
    if c2.exists() {
        return Some(c2);
    }
    None
}

fn copy_dir_filtered(src: &Path, dest: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(dest)?;
    for entry in std::fs::read_dir(src)? {
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().to_string();
        let ft = entry.file_type()?;
        if ft.is_dir() {
            if COPY_EXCLUDE_DIRS.contains(&name.as_str()) {
                continue;
            }
            copy_dir_filtered(&entry.path(), &dest.join(&name))?;
        } else if ft.is_file() {
            if COPY_EXCLUDE_FILES.contains(&name.as_str()) {
                continue;
            }
            std::fs::copy(entry.path(), dest.join(&name))?;
        }
    }
    Ok(())
}

/// Structural + byte-content equality of two dir trees (idempotent short-circuit).
fn dirs_equal(a: &Path, b: &Path) -> bool {
    let (Ok(ea), Ok(eb)) = (std::fs::read_dir(a), std::fs::read_dir(b)) else {
        return false;
    };
    let mut va: Vec<_> = ea.filter_map(|e| e.ok()).collect();
    let mut vb: Vec<_> = eb.filter_map(|e| e.ok()).collect();
    if va.len() != vb.len() {
        return false;
    }
    va.sort_by_key(|e| e.file_name());
    vb.sort_by_key(|e| e.file_name());
    for (x, y) in va.iter().zip(vb.iter()) {
        if x.file_name() != y.file_name() {
            return false;
        }
        let (Ok(tx), Ok(ty)) = (x.file_type(), y.file_type()) else {
            return false;
        };
        if tx.is_dir() != ty.is_dir() {
            return false;
        }
        if tx.is_dir() {
            if !dirs_equal(&x.path(), &y.path()) {
                return false;
            }
        } else {
            let (Ok(bx), Ok(by)) = (std::fs::read(x.path()), std::fs::read(y.path())) else {
                return false;
            };
            if bx != by {
                return false;
            }
        }
    }
    true
}

/// Materialize the addon into a fresh staging dir, then swap it into
/// `addon_dir` — unless the installed tree already matches (idempotent
/// short-circuit). Returns `true` when the addon dir was actually written.
fn materialize_addon(
    source_dir: &Path,
    addon_dir: &Path,
    verify_editor: Option<&Path>,
    staging: &Path,
) -> Result<bool, String> {
    std::fs::create_dir_all(staging).map_err(|e| e.to_string())?;
    copy_dir_filtered(source_dir, staging).map_err(|e| e.to_string())?;
    if !staging.join("plugin.cfg").exists() {
        return Err(format!(
            "Copy completed but no plugin.cfg materialized from {}.",
            source_dir.display()
        ));
    }
    if let Some(ve) = verify_editor {
        copy_dir_filtered(ve, &staging.join("Verify")).map_err(|e| e.to_string())?;
    }
    if addon_dir.exists() && dirs_equal(staging, addon_dir) {
        return Ok(false); // no content change — skip the swap
    }
    let _ = std::fs::remove_dir_all(addon_dir);
    if let Some(parent) = addon_dir.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    std::fs::rename(staging, addon_dir).map_err(|e| e.to_string())?;
    Ok(true)
}

/// Install the addon into `project_path`. Idempotent: a re-run whose staged
/// tree matches the installed one and whose plugin is already enabled reports
/// `changed: false`.
pub fn install_addon(
    project_path: &str,
    source: Option<&str>,
    monorepo: Option<&str>,
) -> InstallResult {
    let project = PathBuf::from(project_path);
    let addon_dir = project.join("addons").join("godot_open_mcp");
    let manifest = project_godot_path(&project);

    if !is_godot_project_root(&project) {
        return install_fail(
            "not_godot_project",
            &format!("Not a valid Godot project (missing project.godot): {project_path}"),
            &addon_dir,
            &manifest,
        );
    }

    let source_dir = match resolve_addon_source(source, monorepo) {
        Ok(d) => d,
        Err(e) => return install_fail("source_missing", &e, &addon_dir, &manifest),
    };

    let mut warnings = vec![];
    let verify_editor = resolve_verify_editor(&source_dir);
    if verify_editor.is_none() {
        warnings.push(format!(
            "Could not locate the sibling verify package next to {}. The bridge references \
             GodotOpenMcp.Verify.* and will not compile until verify source is bundled under \
             addons/godot_open_mcp/Verify/.",
            source_dir.display()
        ));
    }

    // Stage-then-swap materialize.
    let staging = project.join(format!(
        ".godot-open-mcp-addon-staging-{}-{}",
        std::process::id(),
        chrono::Utc::now().timestamp_millis()
    ));
    let _ = std::fs::remove_dir_all(&staging);
    let materialize =
        materialize_addon(&source_dir, &addon_dir, verify_editor.as_deref(), &staging);
    let _ = std::fs::remove_dir_all(&staging);

    let files_changed = match materialize {
        Ok(changed) => changed,
        Err(e) => return install_fail("materialize_failed", &e, &addon_dir, &manifest),
    };

    // Enable the plugin in project.godot.
    let manifest_text = match std::fs::read_to_string(&manifest) {
        Ok(t) => t,
        Err(e) => {
            return install_fail(
                "project_godot_write_failed",
                &format!("Could not read {}: {e}", manifest.display()),
                &addon_dir,
                &manifest,
            )
        }
    };
    let (enabled_plugins, manifest_changed) =
        match toggle_plugin_in_text(&manifest_text, GODOT_OPEN_MCP_PLUGIN_PATH, true) {
            ToggleResult::Changed { text, enabled } => {
                if let Err(e) = std::fs::write(&manifest, text) {
                    return install_fail(
                        "project_godot_write_failed",
                        &format!("Could not write {}: {e}", manifest.display()),
                        &addon_dir,
                        &manifest,
                    );
                }
                (enabled, true)
            }
            ToggleResult::Unchanged { enabled } => (enabled, false),
        };

    InstallResult {
        ok: true,
        changed: files_changed || manifest_changed,
        addon_dir: addon_dir.to_string_lossy().to_string(),
        project_godot_path: manifest.to_string_lossy().to_string(),
        plugin_path: GODOT_OPEN_MCP_PLUGIN_PATH.to_string(),
        enabled_plugins,
        error_label: None,
        warnings,
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Editor discovery + launch
// ─────────────────────────────────────────────────────────────────────────────

const GODOT_ENV_VARS: &[&str] = &["GODOT", "GODOT_EDITOR", "GODOT_BIN", "GODOT4_BIN"];

/// Whether a file name looks like a Godot editor binary.
pub fn is_godot_binary_name(name: &str) -> bool {
    let lower = name.to_lowercase();
    if cfg!(target_os = "windows") {
        if lower == "godot.exe" || lower == "godot_mono.exe" {
            return true;
        }
        return lower.starts_with("godot_v") && lower.contains("win") && lower.ends_with(".exe");
    }
    if lower == "godot" || lower == "godot_mono" {
        return true;
    }
    if cfg!(target_os = "macos") {
        return lower.starts_with("godot_v") && (lower.contains("macos") || lower.contains("osx"));
    }
    lower.starts_with("godot_v") && (lower.contains("linux") || lower.contains("x11"))
}

fn exists_as_file(p: &Path) -> bool {
    std::fs::metadata(p).map(|m| m.is_file()).unwrap_or(false)
}

fn path_candidate_names() -> Vec<&'static str> {
    if cfg!(target_os = "windows") {
        vec!["godot_mono.exe", "godot.exe", "Godot_mono.exe", "Godot.exe"]
    } else {
        vec!["godot", "godot_mono", "Godot"]
    }
}

fn find_on_path() -> Option<PathBuf> {
    let path_env = std::env::var_os("PATH")?;
    for dir in std::env::split_paths(&path_env) {
        for name in path_candidate_names() {
            let candidate = dir.join(name);
            if exists_as_file(&candidate) {
                return Some(candidate);
            }
        }
    }
    None
}

/// Resolve the Godot editor binary. Order: explicit path → env vars → PATH →
/// common install roots (bounded scan). Mirrors the CLI discovery order.
pub fn find_godot_binary(explicit: Option<&str>) -> Option<PathBuf> {
    if let Some(e) = explicit {
        let t = e.trim();
        if !t.is_empty() {
            let r = PathBuf::from(t);
            return if exists_as_file(&r) { Some(r) } else { None };
        }
    }
    for var in GODOT_ENV_VARS {
        if let Ok(raw) = std::env::var(var) {
            let t = raw.trim();
            if !t.is_empty() {
                let r = PathBuf::from(t);
                if exists_as_file(&r) {
                    return Some(r);
                }
            }
        }
    }
    if let Some(p) = find_on_path() {
        return Some(p);
    }
    for root in common_install_roots() {
        if let Some(hit) = scan_for_godot(&root, 3) {
            return Some(hit);
        }
    }
    None
}

fn common_install_roots() -> Vec<PathBuf> {
    let home = dirs::home_dir().unwrap_or_else(|| PathBuf::from("."));
    if cfg!(target_os = "macos") {
        vec![
            PathBuf::from("/Applications"),
            home.join("Applications"),
            home.join("Downloads"),
        ]
    } else if cfg!(target_os = "windows") {
        let pf = std::env::var("PROGRAMFILES").unwrap_or_else(|_| "C:\\Program Files".into());
        vec![
            home.join("Downloads"),
            PathBuf::from(pf).join("Godot"),
        ]
    } else {
        vec![
            PathBuf::from("/usr/local/bin"),
            PathBuf::from("/usr/bin"),
            home.join(".local").join("bin"),
            PathBuf::from("/opt"),
            home.join("Downloads"),
        ]
    }
}

fn scan_for_godot(root: &Path, max_depth: usize) -> Option<PathBuf> {
    fn walk(dir: &Path, depth: usize, max: usize) -> Option<PathBuf> {
        let entries = std::fs::read_dir(dir).ok()?;
        let mut subdirs = vec![];
        for entry in entries.filter_map(|e| e.ok()) {
            let ft = match entry.file_type() {
                Ok(t) => t,
                Err(_) => continue,
            };
            if ft.is_file() {
                if is_godot_binary_name(&entry.file_name().to_string_lossy()) {
                    return Some(entry.path());
                }
            } else if ft.is_dir() && depth < max {
                subdirs.push(entry.path());
            }
        }
        for sd in subdirs {
            if let Some(hit) = walk(&sd, depth + 1, max) {
                return Some(hit);
            }
        }
        None
    }
    walk(root, 0, max_depth)
}

/// Result of a launch attempt.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub editor_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pid: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error_label: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

/// Spawn `<godot> --editor --path <project>` detached. Sets no cloud env vars.
pub fn launch_editor(project_path: &str, explicit_editor: Option<&str>) -> LaunchResult {
    let project = PathBuf::from(project_path);
    if !is_godot_project_root(&project) {
        return LaunchResult {
            ok: false,
            editor_path: None,
            pid: None,
            error_label: Some("not_godot_project".to_string()),
            message: Some(format!("Missing project.godot at {project_path}")),
        };
    }
    let Some(editor) = find_godot_binary(explicit_editor) else {
        return LaunchResult {
            ok: false,
            editor_path: None,
            pid: None,
            error_label: Some("editor_not_found".to_string()),
            message: Some(
                "Could not find a Godot editor binary. Set the editor path in Settings or the \
                 GODOT env var."
                    .to_string(),
            ),
        };
    };
    let abs_project = std::fs::canonicalize(&project).unwrap_or(project);
    let child = std::process::Command::new(&editor)
        .arg("--editor")
        .arg("--path")
        .arg(&abs_project)
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn();
    match child {
        Ok(c) => LaunchResult {
            ok: true,
            editor_path: Some(editor.to_string_lossy().to_string()),
            pid: Some(c.id()),
            error_label: None,
            message: None,
        },
        Err(e) => LaunchResult {
            ok: false,
            editor_path: Some(editor.to_string_lossy().to_string()),
            pid: None,
            error_label: Some("launch_failed".to_string()),
            message: Some(format!("Failed to launch Godot: {e}")),
        },
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Bridge ping (one-shot)
// ─────────────────────────────────────────────────────────────────────────────

/// Readiness classification of a single `/ping`.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PingResult {
    /// ready | compiling | offline | error
    pub status: String,
    pub port: u16,
    pub base_url: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<serde_json::Value>,
    pub ready: bool,
}

/// Read the optional bridge auth token from the instance lock, if any.
fn lock_auth_token(project_path: &str) -> Option<String> {
    let raw = std::fs::read_to_string(bridge_port::lock_path(project_path)).ok()?;
    let v: serde_json::Value = serde_json::from_str(&raw).ok()?;
    v.get("authToken")
        .and_then(|t| t.as_str())
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
}

/// One-shot `GET /ping` against `http://127.0.0.1:<port>`. Classifies the
/// result the same way the CLI's `singlePing` does: 503 → compiling; 200 with
/// `compiling:true` → compiling; 200 with `connected:false` → offline; 200
/// otherwise → ready; refused/timeout → offline/error.
pub fn poll_bridge_ping(project_path: &str, override_port: Option<u16>) -> PingResult {
    let port = bridge_port::resolve_port(project_path, override_port);
    let base_url = format!("http://127.0.0.1:{port}");
    let token = lock_auth_token(project_path);

    match http_get_ping(port, token.as_deref(), Duration::from_secs(5)) {
        Ok((code, body)) => {
            let json: Option<serde_json::Value> = serde_json::from_str(&body).ok();
            if code == 503 {
                return PingResult {
                    status: "compiling".into(),
                    port,
                    base_url,
                    body: json,
                    ready: false,
                };
            }
            if code == 200 {
                if let Some(ref j) = json {
                    if j.get("compiling").and_then(|v| v.as_bool()) == Some(true) {
                        return PingResult { status: "compiling".into(), port, base_url, body: json, ready: false };
                    }
                    if j.get("connected").and_then(|v| v.as_bool()) == Some(false) {
                        return PingResult { status: "offline".into(), port, base_url, body: json, ready: false };
                    }
                    return PingResult { status: "ready".into(), port, base_url, body: json, ready: true };
                }
                return PingResult { status: "compiling".into(), port, base_url, body: None, ready: false };
            }
            PingResult { status: "offline".into(), port, base_url, body: json, ready: false }
        }
        Err(kind) => PingResult {
            status: kind,
            port,
            base_url,
            body: None,
            ready: false,
        },
    }
}

/// Minimal dependency-free HTTP/1.0 GET of `/ping`. Returns `(status_code,
/// body)` on success; `Err("offline" | "error")` on connect/timeout failure.
fn http_get_ping(port: u16, token: Option<&str>, timeout: Duration) -> Result<(u16, String), String> {
    let addr = format!("127.0.0.1:{port}");
    let sock_addr = addr.parse().map_err(|_| "error".to_string())?;
    let mut stream = TcpStream::connect_timeout(&sock_addr, timeout).map_err(|_| "offline".to_string())?;
    stream.set_read_timeout(Some(timeout)).ok();
    stream.set_write_timeout(Some(timeout)).ok();

    let mut req = format!(
        "GET /ping HTTP/1.0\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n"
    );
    if let Some(t) = token {
        req.push_str(&format!("Authorization: Bearer {t}\r\n"));
    }
    req.push_str("\r\n");
    stream.write_all(req.as_bytes()).map_err(|_| "error".to_string())?;

    let mut raw = Vec::new();
    stream.read_to_end(&mut raw).map_err(|_| "error".to_string())?;
    let text = String::from_utf8_lossy(&raw);
    let (head, body) = match text.split_once("\r\n\r\n") {
        Some((h, b)) => (h, b.to_string()),
        None => (text.as_ref(), String::new()),
    };
    let status_line = head.lines().next().unwrap_or("");
    // "HTTP/1.1 200 OK" → 200
    let code = status_line
        .split_whitespace()
        .nth(1)
        .and_then(|c| c.parse::<u16>().ok())
        .ok_or_else(|| "error".to_string())?;
    Ok((code, body))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn godot_binary_name_matches_fixed_and_stamped() {
        if cfg!(target_os = "windows") {
            assert!(is_godot_binary_name("godot.exe"));
            assert!(is_godot_binary_name("Godot_v4.5-stable_mono_win64.exe"));
        } else {
            assert!(is_godot_binary_name("godot"));
            assert!(is_godot_binary_name("godot_mono"));
        }
        assert!(!is_godot_binary_name("not-godot"));
    }

    #[test]
    fn install_rejects_non_godot_project() {
        let dir = tempfile::tempdir().unwrap();
        let r = install_addon(dir.path().to_str().unwrap(), None, None);
        assert!(!r.ok);
        assert_eq!(r.error_label.as_deref(), Some("not_godot_project"));
    }

    #[test]
    fn install_reports_source_missing_without_source() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("project.godot"), "[application]\n").unwrap();
        let r = install_addon(dir.path().to_str().unwrap(), None, None);
        assert!(!r.ok);
        assert_eq!(r.error_label.as_deref(), Some("source_missing"));
    }

    #[test]
    fn install_materializes_and_enables_then_idempotent() {
        // Fake a source addon tree + a project, run install twice.
        let src = tempfile::tempdir().unwrap();
        std::fs::write(src.path().join("plugin.cfg"), "[plugin]\nname=\"x\"\n").unwrap();
        std::fs::create_dir_all(src.path().join("Editor")).unwrap();
        std::fs::write(src.path().join("Editor/Foo.cs"), "// code").unwrap();
        // Excluded dir should not be copied.
        std::fs::create_dir_all(src.path().join("Tests")).unwrap();
        std::fs::write(src.path().join("Tests/T.cs"), "// test").unwrap();

        let proj = tempfile::tempdir().unwrap();
        std::fs::write(proj.path().join("project.godot"), "[application]\n").unwrap();

        let r1 = install_addon(
            proj.path().to_str().unwrap(),
            Some(src.path().to_str().unwrap()),
            None,
        );
        assert!(r1.ok, "install failed: {:?}", r1.warnings);
        assert!(r1.changed);
        let addon = proj.path().join("addons/godot_open_mcp");
        assert!(addon.join("plugin.cfg").exists());
        assert!(!addon.join("Tests").exists(), "Tests/ must be excluded");
        assert!(r1
            .enabled_plugins
            .iter()
            .any(|p| p == GODOT_OPEN_MCP_PLUGIN_PATH));

        let r2 = install_addon(
            proj.path().to_str().unwrap(),
            Some(src.path().to_str().unwrap()),
            None,
        );
        assert!(r2.ok);
        assert!(!r2.changed, "second install must be idempotent (changed:false)");
    }

    #[test]
    fn ping_offline_when_nothing_listening() {
        // A project with no bridge running → offline (connect refused). Uses a
        // fixed override port unlikely to be bound.
        let r = poll_bridge_ping("/no/such/project", Some(59999));
        assert!(!r.ready);
        assert!(matches!(r.status.as_str(), "offline" | "error"));
        assert_eq!(r.port, 59999);
    }
}
