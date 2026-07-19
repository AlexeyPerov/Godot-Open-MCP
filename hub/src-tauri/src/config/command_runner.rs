//! Maintainer npm command runner (P11.3).
//!
//! Ports Unity Hub's `resolve_npm_cwd` (OpenMcp → `<root>/mcp-server`) and a
//! blocking, output-capped command runner for the maintainer panel: build,
//! test, publish dry-run, publish (confirm-gated in the UI), and version sync.
//!
//! Version sync (`scripts/sync-version.mjs`) runs from the **repo root**, not
//! `mcp-server/` — some commands deliberately bypass the npm cwd, mirrored here
//! with `resolve_version_sync_cwd`.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::Serialize;

use crate::config::project_kind::ProjectKind;

/// Max lines captured from a command before truncation (mirrors the Unity
/// per-panel log cap).
const MAX_LOG_LINES: usize = 1000;

/// Resolve the cwd for npm commands. For the OpenMcp monorepo all npm commands
/// run in `mcp-server/` (the publishable package); other kinds run at the root.
pub fn resolve_npm_cwd(project_path: &str, kind: ProjectKind) -> PathBuf {
    let base = PathBuf::from(project_path);
    match kind {
        ProjectKind::OpenMcp => base.join("mcp-server"),
        _ => base,
    }
}

/// Version sync deliberately runs from the repo ROOT (`scripts/sync-version.mjs`
/// resolves its targets relative to the repo root), NOT `mcp-server/`.
pub fn resolve_version_sync_cwd(project_path: &str) -> PathBuf {
    PathBuf::from(project_path)
}

fn npm_bin() -> &'static str {
    if cfg!(target_os = "windows") {
        "npm.cmd"
    } else {
        "npm"
    }
}

/// One captured line, tagged by stream.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandLine {
    pub stream: String, // "meta" | "stdout" | "stderr"
    pub text: String,
}

/// Result of a blocking command run.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandResult {
    pub ok: bool,
    pub exit_code: Option<i32>,
    pub cwd: String,
    pub argv: String,
    pub lines: Vec<CommandLine>,
    pub truncated: bool,
}

fn cap_lines(lines: Vec<CommandLine>) -> (Vec<CommandLine>, bool) {
    if lines.len() <= MAX_LOG_LINES {
        (lines, false)
    } else {
        let start = lines.len() - MAX_LOG_LINES;
        (lines[start..].to_vec(), true)
    }
}

/// Run a program with args in `cwd`, capturing combined output (capped). The
/// first line is a `meta` header echoing the resolved cwd + argv so the console
/// always shows where a command ran (safety: log cwd in the header).
pub fn run_capture(program: &str, args: &[String], cwd: &Path) -> CommandResult {
    let argv = format!("{program} {}", args.join(" "));
    let mut lines = vec![CommandLine {
        stream: "meta".to_string(),
        text: format!("$ ({}) {argv}", cwd.display()),
    }];

    let output = Command::new(program).args(args).current_dir(cwd).output();

    match output {
        Ok(out) => {
            for l in String::from_utf8_lossy(&out.stdout).lines() {
                lines.push(CommandLine {
                    stream: "stdout".to_string(),
                    text: l.to_string(),
                });
            }
            for l in String::from_utf8_lossy(&out.stderr).lines() {
                lines.push(CommandLine {
                    stream: "stderr".to_string(),
                    text: l.to_string(),
                });
            }
            let (lines, truncated) = cap_lines(lines);
            CommandResult {
                ok: out.status.success(),
                exit_code: out.status.code(),
                cwd: cwd.to_string_lossy().to_string(),
                argv,
                lines,
                truncated,
            }
        }
        Err(e) => {
            lines.push(CommandLine {
                stream: "stderr".to_string(),
                text: format!("failed to spawn '{program}': {e}"),
            });
            CommandResult {
                ok: false,
                exit_code: None,
                cwd: cwd.to_string_lossy().to_string(),
                argv,
                lines,
                truncated: false,
            }
        }
    }
}

/// Run an npm subcommand in the resolved npm cwd for the project kind.
pub fn run_npm(project_path: &str, kind: ProjectKind, args: &[String]) -> CommandResult {
    let cwd = resolve_npm_cwd(project_path, kind);
    run_capture(npm_bin(), args, &cwd)
}

/// Run the repo's version-sync script from the repo root.
pub fn run_version_sync(project_path: &str, args: &[String]) -> CommandResult {
    let cwd = resolve_version_sync_cwd(project_path);
    let mut full = vec!["scripts/sync-version.mjs".to_string()];
    full.extend(args.iter().cloned());
    run_capture("node", &full, &cwd)
}

/// Package identity for the maintainer panel header.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct McpPackageInfo {
    pub name: String,
    pub version: String,
    pub manifest_path: String,
}

/// Read `<root>/mcp-server/package.json` name + version.
pub fn read_mcp_package_info(project_path: &str) -> Result<McpPackageInfo, String> {
    let manifest = PathBuf::from(project_path)
        .join("mcp-server")
        .join("package.json");
    let text = std::fs::read_to_string(&manifest)
        .map_err(|e| format!("Could not read {}: {e}", manifest.display()))?;
    let json: serde_json::Value =
        serde_json::from_str(&text).map_err(|e| format!("Invalid package.json: {e}"))?;
    let name = json
        .get("name")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let version = json
        .get("version")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    Ok(McpPackageInfo {
        name,
        version,
        manifest_path: manifest.to_string_lossy().to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolve_npm_cwd_open_mcp_appends_mcp_server() {
        assert_eq!(
            resolve_npm_cwd("/repos/gom", ProjectKind::OpenMcp),
            PathBuf::from("/repos/gom/mcp-server")
        );
    }

    #[test]
    fn resolve_npm_cwd_godot_project_is_root() {
        assert_eq!(
            resolve_npm_cwd("/games/foo", ProjectKind::GodotProject),
            PathBuf::from("/games/foo")
        );
    }

    #[test]
    fn resolve_npm_cwd_custom_is_root() {
        assert_eq!(
            resolve_npm_cwd("/misc/bar", ProjectKind::Custom),
            PathBuf::from("/misc/bar")
        );
    }

    #[test]
    fn version_sync_cwd_is_repo_root_not_mcp_server() {
        assert_eq!(
            resolve_version_sync_cwd("/repos/gom"),
            PathBuf::from("/repos/gom")
        );
        assert_ne!(
            resolve_version_sync_cwd("/repos/gom"),
            resolve_npm_cwd("/repos/gom", ProjectKind::OpenMcp)
        );
    }

    #[test]
    fn read_package_info_reads_name_and_version() {
        let dir = tempfile::tempdir().unwrap();
        let manifest = dir.path().join("mcp-server/package.json");
        std::fs::create_dir_all(manifest.parent().unwrap()).unwrap();
        std::fs::write(&manifest, r#"{ "name": "godot-open-mcp", "version": "1.2.3" }"#).unwrap();
        let info = read_mcp_package_info(dir.path().to_str().unwrap()).unwrap();
        assert_eq!(info.name, "godot-open-mcp");
        assert_eq!(info.version, "1.2.3");
    }

    #[test]
    fn run_capture_reports_spawn_failure() {
        let dir = tempfile::tempdir().unwrap();
        let r = run_capture("definitely-not-a-real-binary-xyz", &[], dir.path());
        assert!(!r.ok);
        assert!(r.lines.iter().any(|l| l.text.contains("failed to spawn")));
    }
}
