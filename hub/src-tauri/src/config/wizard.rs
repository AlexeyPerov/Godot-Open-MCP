//! Wizard detection + clear-setup orchestration (P11.2).
//!
//! `detect_project_state` gathers the readiness signals the wizard gates on:
//! project validity, addon present/enabled, per-client MCP configured, Node
//! availability, and the resolved bridge port. `clear_ai_setup` removes our
//! MCP server key from selected clients (never sibling servers) and optionally
//! disables the plugin — the safe "Clear AI Setup" path.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::Serialize;

use crate::config::bridge_port;
use crate::config::mcp_config::{self, MCP_SERVER_NAME};
use crate::config::project_godot::{
    is_godot_project_root, parse_enabled_plugins, project_godot_path, toggle_plugin_in_text,
    ToggleResult, GODOT_OPEN_MCP_PLUGIN_PATH,
};

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AddonState {
    pub installed: bool,
    pub enabled: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientMcpState {
    pub client_id: String,
    pub configured: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NodeState {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BridgeState {
    pub port: u16,
}

/// The full detection snapshot returned to the wizard.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectState {
    pub project_path: String,
    pub is_godot_project: bool,
    pub addon: AddonState,
    pub mcp: Vec<ClientMcpState>,
    pub node: NodeState,
    pub bridge: BridgeState,
}

/// Addon presence + plugin-enabled check (mirrors CLI `inspectAddon`).
fn inspect_addon(project: &Path) -> AddonState {
    let cfg = project.join("addons").join("godot_open_mcp").join("plugin.cfg");
    let installed = cfg.exists();
    let mut enabled = false;
    if installed {
        if let Ok(text) = std::fs::read_to_string(project_godot_path(project)) {
            enabled = parse_enabled_plugins(&text)
                .iter()
                .any(|p| p == GODOT_OPEN_MCP_PLUGIN_PATH);
        }
    }
    AddonState {
        installed,
        enabled,
        path: installed.then(|| cfg.to_string_lossy().to_string()),
    }
}

/// Is a client's config already carrying our stdio server entry?
fn client_configured(project: &Path, client_id: &str) -> ClientMcpState {
    let Some(agent) = mcp_config::get_agent(client_id) else {
        return ClientMcpState {
            client_id: client_id.to_string(),
            configured: false,
            path: None,
        };
    };
    let config_path = mcp_config::agent_config_path(&agent, project);
    let mut configured = false;
    if let Ok(text) = std::fs::read_to_string(&config_path) {
        if let Ok(json) = serde_json::from_str::<serde_json::Value>(&text) {
            // Walk to the servers map and check our key exists with a command.
            let mut cursor = Some(&json);
            for seg in agent_body_path(client_id) {
                cursor = cursor.and_then(|v| v.get(seg));
            }
            if let Some(entry) = cursor.and_then(|v| v.get(MCP_SERVER_NAME)) {
                configured = entry.get("command").is_some();
            }
        }
    }
    ClientMcpState {
        client_id: client_id.to_string(),
        configured,
        path: Some(config_path.to_string_lossy().to_string()),
    }
}

/// The body path segments per client (kept aligned with the registry).
fn agent_body_path(client_id: &str) -> Vec<&'static str> {
    match client_id {
        "vscode-copilot" => vec!["servers"],
        "opencode" => vec!["mcp"],
        _ => vec!["mcpServers"],
    }
}

/// Check Node availability + semver >= 18.
pub fn check_node() -> NodeState {
    let node_bin = if cfg!(target_os = "windows") {
        "node.exe"
    } else {
        "node"
    };
    match Command::new(node_bin).arg("--version").output() {
        Ok(out) if out.status.success() => {
            let raw = String::from_utf8_lossy(&out.stdout).trim().to_string();
            let version = raw.trim_start_matches('v').to_string();
            let major = version
                .split('.')
                .next()
                .and_then(|m| m.parse::<u32>().ok())
                .unwrap_or(0);
            if major >= 18 {
                NodeState {
                    ok: true,
                    version: Some(version),
                    error: None,
                }
            } else {
                NodeState {
                    ok: false,
                    version: Some(version),
                    error: Some("Node 18 or newer is required for the npx/node MCP server.".into()),
                }
            }
        }
        Ok(_) => NodeState {
            ok: false,
            version: None,
            error: Some("`node --version` failed.".into()),
        },
        Err(_) => NodeState {
            ok: false,
            version: None,
            error: Some("Node was not found on PATH. Install Node 18+ to run the MCP server.".into()),
        },
    }
}

/// Gather the full detection snapshot for the wizard.
pub fn detect_project_state(project_path: &str, client_ids: &[String]) -> ProjectState {
    let project = PathBuf::from(project_path);
    let is_godot = is_godot_project_root(&project);
    let addon = inspect_addon(&project);
    let mcp = client_ids
        .iter()
        .map(|c| client_configured(&project, c))
        .collect();
    let node = check_node();
    let bridge = BridgeState {
        port: bridge_port::resolve_port(project_path, None),
    };
    ProjectState {
        project_path: project_path.to_string(),
        is_godot_project: is_godot,
        addon,
        mcp,
        node,
        bridge,
    }
}

/// Result of a Clear AI Setup operation.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClearResult {
    pub ok: bool,
    /// Per-client removal results: (clientId, removed, configPath).
    pub clients: Vec<ClientClear>,
    pub plugin_disabled: bool,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientClear {
    pub client_id: String,
    pub removed: bool,
    pub config_path: String,
}

/// Remove our MCP key from the selected clients and optionally disable the
/// plugin. Never deletes sibling servers or the whole addons/ tree.
pub fn clear_ai_setup(
    project_path: &str,
    client_ids: &[String],
    disable_plugin: bool,
) -> ClearResult {
    let mut warnings = vec![];
    let mut clients = vec![];
    for id in client_ids {
        match mcp_config::clear_mcp(project_path, id) {
            Ok((removed, path)) => clients.push(ClientClear {
                client_id: id.clone(),
                removed,
                config_path: path,
            }),
            Err(e) => warnings.push(format!("{id}: {e}")),
        }
    }

    let mut plugin_disabled = false;
    if disable_plugin {
        let manifest = project_godot_path(Path::new(project_path));
        if let Ok(text) = std::fs::read_to_string(&manifest) {
            if let ToggleResult::Changed { text: out, .. } =
                toggle_plugin_in_text(&text, GODOT_OPEN_MCP_PLUGIN_PATH, false)
            {
                if std::fs::write(&manifest, out).is_ok() {
                    plugin_disabled = true;
                } else {
                    warnings.push("Could not write project.godot to disable the plugin.".into());
                }
            }
        }
    }

    ClearResult {
        ok: warnings.is_empty(),
        clients,
        plugin_disabled,
        warnings,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detect_reports_addon_and_mcp_absent_on_bare_project() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("project.godot"), "[application]\n").unwrap();
        let state =
            detect_project_state(dir.path().to_str().unwrap(), &["cursor".to_string()]);
        assert!(state.is_godot_project);
        assert!(!state.addon.installed);
        assert_eq!(state.mcp.len(), 1);
        assert!(!state.mcp[0].configured);
        assert!(state.bridge.port >= 20000 && state.bridge.port <= 29999);
    }

    #[test]
    fn detect_reports_enabled_addon() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("project.godot"),
            format!(
                "[editor_plugins]\n\nenabled=PackedStringArray(\"{}\")\n",
                GODOT_OPEN_MCP_PLUGIN_PATH
            ),
        )
        .unwrap();
        let cfg = dir.path().join("addons/godot_open_mcp");
        std::fs::create_dir_all(&cfg).unwrap();
        std::fs::write(cfg.join("plugin.cfg"), "[plugin]\n").unwrap();
        let state = detect_project_state(dir.path().to_str().unwrap(), &[]);
        assert!(state.addon.installed);
        assert!(state.addon.enabled);
    }

    #[test]
    fn clear_removes_only_our_key() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("project.godot"), "[application]\n").unwrap();
        let cursor_cfg = dir.path().join(".cursor/mcp.json");
        std::fs::create_dir_all(cursor_cfg.parent().unwrap()).unwrap();
        std::fs::write(
            &cursor_cfg,
            r#"{ "mcpServers": { "godot-open-mcp": { "command": "npx" }, "other": { "command": "x" } } }"#,
        )
        .unwrap();
        let r = clear_ai_setup(dir.path().to_str().unwrap(), &["cursor".to_string()], false);
        assert!(r.ok);
        assert!(r.clients[0].removed);
        let after: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&cursor_cfg).unwrap()).unwrap();
        assert!(after["mcpServers"].get("godot-open-mcp").is_none());
        assert!(after["mcpServers"].get("other").is_some());
    }
}
