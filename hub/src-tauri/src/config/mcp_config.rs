//! MCP-client config writer — the Rust port of the CLI's `utils/agents.ts` +
//! `lib/setup-mcp.ts`.
//!
//! Godot Open MCP is **stdio only** (ADR-001): an AI client spawns the MCP
//! server (`godot-open-mcp`) as a child process; the server reaches the bridge
//! over loopback HTTP. Every agent writes a stdio entry — `command`/`args`/
//! `env` — never a `url` / `type:"http"` entry. The merge strips any foreign
//! HTTP/transport keys a prior entry could have left behind, preserves sibling
//! servers, and reports `changed:false` on an idempotent re-run.
//!
//! Parity: the entry body this module produces MUST match the CLI's
//! `setup-mcp` output for the same inputs (server key, env keys, no `url`). A
//! golden fixture asserts this (see the frontend `ai_toolkit.test.ts` and the
//! Rust tests below).

use std::path::{Path, PathBuf};

use serde::Serialize;
use serde_json::{Map, Value};

use crate::config::project_godot::is_godot_project_root;
use crate::config::schemas::McpSource;

/// The MCP server key written into every client config. Matches the npm
/// package name and its bin — the stdio server the AI client spawns.
pub const MCP_SERVER_NAME: &str = "godot-open-mcp";

/// Whether the agent's config lives inside the project or user-global.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Scope {
    Project,
    Global,
}

/// The client-specific envelope shape for the server entry.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Shape {
    /// `{ command, args, env }` under `mcpServers` (Cursor, Claude, …).
    Bare,
    /// `{ type:"stdio", command, args, env }` under `servers` (VS Code).
    Vscode,
    /// `{ type:"local", command:[...], enabled, environment }` under `mcp`.
    Opencode,
}

/// A registered MCP client.
pub struct Agent {
    pub id: &'static str,
    pub name: &'static str,
    pub scope: Scope,
    pub config_path_display: &'static str,
    body_path: &'static [&'static str],
    shape: Shape,
    remove_keys: &'static [&'static str],
}

const BARE_REMOVE_KEYS: &[&str] = &[
    "url",
    "type",
    "headers",
    "disabled",
    "serverUrl",
    "tools",
    "tool_timeout_sec",
    "startup_timeout_sec",
    "environment",
];
const VSCODE_REMOVE_KEYS: &[&str] =
    &["url", "headers", "disabled", "serverUrl", "tools", "environment"];
const OPENCODE_REMOVE_KEYS: &[&str] =
    &["url", "headers", "args", "env", "disabled", "serverUrl", "tools"];

/// The shipped agent roster. The minimum bar is `cursor` + `claude-desktop` +
/// `claude-code` (P11.2 Done-when); the rest mirror the CLI registry so the two
/// stay in lockstep.
pub fn agent_registry() -> Vec<Agent> {
    vec![
        Agent {
            id: "cursor",
            name: "Cursor",
            scope: Scope::Project,
            config_path_display: ".cursor/mcp.json",
            body_path: &["mcpServers"],
            shape: Shape::Bare,
            remove_keys: BARE_REMOVE_KEYS,
        },
        Agent {
            id: "claude-code",
            name: "Claude Code",
            scope: Scope::Project,
            config_path_display: ".mcp.json",
            body_path: &["mcpServers"],
            shape: Shape::Bare,
            remove_keys: BARE_REMOVE_KEYS,
        },
        Agent {
            id: "claude-desktop",
            name: "Claude Desktop",
            scope: Scope::Global,
            config_path_display: "Claude/claude_desktop_config.json",
            body_path: &["mcpServers"],
            shape: Shape::Bare,
            remove_keys: BARE_REMOVE_KEYS,
        },
        Agent {
            id: "vscode-copilot",
            name: "Visual Studio Code (Copilot)",
            scope: Scope::Project,
            config_path_display: ".vscode/mcp.json",
            body_path: &["servers"],
            shape: Shape::Vscode,
            remove_keys: VSCODE_REMOVE_KEYS,
        },
        Agent {
            id: "opencode",
            name: "OpenCode",
            scope: Scope::Project,
            config_path_display: "opencode.json",
            body_path: &["mcp"],
            shape: Shape::Opencode,
            remove_keys: OPENCODE_REMOVE_KEYS,
        },
        Agent {
            id: "gemini",
            name: "Gemini",
            scope: Scope::Project,
            config_path_display: ".gemini/settings.json",
            body_path: &["mcpServers"],
            shape: Shape::Bare,
            remove_keys: BARE_REMOVE_KEYS,
        },
        Agent {
            id: "custom",
            name: "Custom (generic MCP client)",
            scope: Scope::Project,
            config_path_display: "mcp.json",
            body_path: &["mcpServers"],
            shape: Shape::Bare,
            remove_keys: BARE_REMOVE_KEYS,
        },
    ]
}

pub fn get_agent(id: &str) -> Option<Agent> {
    agent_registry().into_iter().find(|a| a.id == id)
}

/// Resolve the absolute config-file path for an agent + project. Mirrors the
/// CLI `getConfigPath` per-client logic (project-local vs OS-specific global).
pub fn agent_config_path(agent: &Agent, project_path: &Path) -> PathBuf {
    match agent.id {
        "cursor" => project_path.join(".cursor").join("mcp.json"),
        "claude-code" => project_path.join(".mcp.json"),
        "claude-desktop" => claude_desktop_path(),
        "vscode-copilot" => project_path.join(".vscode").join("mcp.json"),
        "opencode" => project_path.join("opencode.json"),
        "gemini" => project_path.join(".gemini").join("settings.json"),
        _ => project_path.join("mcp.json"),
    }
}

fn claude_desktop_path() -> PathBuf {
    let home = dirs::home_dir().unwrap_or_else(|| PathBuf::from("."));
    if cfg!(target_os = "windows") {
        let appdata = std::env::var("APPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|_| home.join("AppData").join("Roaming"));
        appdata.join("Claude").join("claude_desktop_config.json")
    } else if cfg!(target_os = "macos") {
        home.join("Library")
            .join("Application Support")
            .join("Claude")
            .join("claude_desktop_config.json")
    } else {
        home.join(".config")
            .join("Claude")
            .join("claude_desktop_config.json")
    }
}

// ── stdio props builder ─────────────────────────────────────────────────────

/// Inputs for planning/writing an MCP config entry.
#[derive(Clone, Debug)]
pub struct McpInput {
    pub project_path: String,
    pub agent_id: String,
    pub source: McpSource,
    /// Version to pin the npx spec (`godot-open-mcp@<version>`).
    pub package_version: Option<String>,
    /// Monorepo root for the local-checkout source (`node <root>/mcp-server/dist/index.js`).
    pub monorepo_path: Option<String>,
    /// Optional bridge-port override written as `GODOT_OPEN_MCP_BRIDGE_PORT`.
    pub override_port: Option<u16>,
    /// Explicit config path override (else the agent default).
    pub config_path: Option<String>,
}

/// The uniform stdio descriptor (command/args/env) before shaping.
struct StdioProps {
    command: String,
    args: Vec<String>,
    env: Map<String, Value>,
}

fn build_env(project_path: &str, override_port: Option<u16>) -> Map<String, Value> {
    let mut env = Map::new();
    env.insert(
        "GODOT_PROJECT_PATH".to_string(),
        Value::String(project_path.to_string()),
    );
    if let Some(port) = override_port {
        env.insert(
            "GODOT_OPEN_MCP_BRIDGE_PORT".to_string(),
            Value::String(port.to_string()),
        );
    }
    env
}

fn build_stdio_props(input: &McpInput, abs_project: &str) -> Result<StdioProps, String> {
    let env = build_env(abs_project, input.override_port);
    match input.source {
        McpSource::LocalCheckout => {
            let repo = input
                .monorepo_path
                .as_deref()
                .filter(|s| !s.is_empty())
                .ok_or_else(|| {
                    "local-checkout source requires the monorepo path (…/Godot-Open-MCP)."
                        .to_string()
                })?;
            let entry = Path::new(repo)
                .join("mcp-server")
                .join("dist")
                .join("index.js");
            Ok(StdioProps {
                command: "node".to_string(),
                args: vec![entry.to_string_lossy().to_string()],
                env,
            })
        }
        McpSource::NpxPublished => {
            let pkg = match input.package_version.as_deref().filter(|s| !s.is_empty()) {
                Some(v) => format!("godot-open-mcp@{v}"),
                None => "godot-open-mcp".to_string(),
            };
            Ok(StdioProps {
                command: "npx".to_string(),
                args: vec!["-y".to_string(), pkg],
                env,
            })
        }
    }
}

/// Shape the uniform stdio props into a client-specific entry body.
fn shape_entry(shape: Shape, props: &StdioProps) -> Value {
    let args: Vec<Value> = props.args.iter().map(|a| Value::String(a.clone())).collect();
    match shape {
        Shape::Bare => {
            let mut m = Map::new();
            m.insert("command".into(), Value::String(props.command.clone()));
            m.insert("args".into(), Value::Array(args));
            m.insert("env".into(), Value::Object(props.env.clone()));
            Value::Object(m)
        }
        Shape::Vscode => {
            let mut m = Map::new();
            m.insert("type".into(), Value::String("stdio".into()));
            m.insert("command".into(), Value::String(props.command.clone()));
            m.insert("args".into(), Value::Array(args));
            m.insert("env".into(), Value::Object(props.env.clone()));
            Value::Object(m)
        }
        Shape::Opencode => {
            let mut cmd = vec![Value::String(props.command.clone())];
            cmd.extend(args);
            let mut m = Map::new();
            m.insert("type".into(), Value::String("local".into()));
            m.insert("command".into(), Value::Array(cmd));
            m.insert("enabled".into(), Value::Bool(true));
            m.insert("environment".into(), Value::Object(props.env.clone()));
            Value::Object(m)
        }
    }
}

// ── pure JSON merge (mirrors mergeStdioServerEntry) ─────────────────────────

fn is_object(v: &Value) -> bool {
    v.is_object()
}

/// Merge a stdio server entry into a parsed config root, preserving siblings.
/// Returns `(new_root, changed)`. `changed` is false when the entry already
/// matched (idempotent re-run).
pub fn merge_stdio_server_entry(
    root: &Value,
    body_path: &[&str],
    server_name: &str,
    props: &Value,
    remove_keys: &[&str],
) -> (Value, bool) {
    let mut next = root.clone();
    if !next.is_object() {
        next = Value::Object(Map::new());
    }

    // Walk/create the body path.
    {
        let mut cursor = next.as_object_mut().unwrap();
        for (i, seg) in body_path.iter().enumerate() {
            let is_last = i == body_path.len() - 1;
            if !cursor.get(*seg).map(is_object).unwrap_or(false) {
                cursor.insert(seg.to_string(), Value::Object(Map::new()));
            }
            if is_last {
                break;
            }
            cursor = cursor.get_mut(*seg).unwrap().as_object_mut().unwrap();
        }
    }

    // Locate the servers map (walk again on the fresh clone).
    let before = walk_existing_entry(root, body_path, server_name);

    let servers = {
        let mut cursor = next.as_object_mut().unwrap();
        for seg in body_path {
            cursor = cursor.get_mut(*seg).unwrap().as_object_mut().unwrap();
        }
        cursor
    };

    // Old entry (clone), strip foreign keys.
    let mut entry = match servers.get(server_name) {
        Some(Value::Object(m)) => m.clone(),
        _ => Map::new(),
    };
    for k in remove_keys {
        entry.remove(*k);
    }
    // Merge new props (overwrite command/args/env/type/...).
    if let Value::Object(p) = props {
        for (k, v) in p {
            entry.insert(k.clone(), v.clone());
        }
    }
    let new_entry = Value::Object(entry);
    let changed = before.as_ref() != Some(&new_entry);
    servers.insert(server_name.to_string(), new_entry);

    (next, changed)
}

fn walk_existing_entry(root: &Value, body_path: &[&str], server_name: &str) -> Option<Value> {
    let mut cursor = root;
    for seg in body_path {
        cursor = cursor.get(*seg)?;
    }
    cursor.get(server_name).cloned()
}

// ── plan / write ────────────────────────────────────────────────────────────

/// Preview + apply result for an MCP config write.
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct McpPlan {
    pub agent_id: String,
    pub config_path: String,
    pub server_name: String,
    pub transport: String,
    /// The entry body (client-shaped) that will be / was written.
    pub entry: Value,
    /// Whether applying would change (plan) or did change (write) the file.
    pub changed: bool,
    /// Whether the file was actually written (`false` for plan, or no-op write).
    pub wrote: bool,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
}

fn resolve_abs_project(input: &McpInput) -> Result<String, String> {
    let p = Path::new(&input.project_path);
    if !p.is_absolute() {
        return Err(format!(
            "godotProjectPath must be absolute (got '{}').",
            input.project_path
        ));
    }
    Ok(input.project_path.clone())
}

fn read_existing_config(config_path: &Path) -> Result<Value, String> {
    if !config_path.exists() {
        return Ok(Value::Object(Map::new()));
    }
    let text = std::fs::read_to_string(config_path)
        .map_err(|e| format!("Could not read {}: {e}", config_path.display()))?;
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return Ok(Value::Object(Map::new()));
    }
    let parsed: Value = serde_json::from_str(trimmed)
        .map_err(|e| format!("Existing config is not valid JSON: {e}"))?;
    if !parsed.is_object() {
        return Err("Existing config root is not a JSON object.".to_string());
    }
    Ok(parsed)
}

/// Compute the plan (preview) without writing.
pub fn plan_mcp(input: &McpInput) -> Result<McpPlan, String> {
    let abs = resolve_abs_project(input)?;
    let agent = get_agent(&input.agent_id)
        .ok_or_else(|| format!("Unknown agent '{}'.", input.agent_id))?;
    let mut warnings = vec![];
    if agent.scope == Scope::Project && !is_godot_project_root(Path::new(&abs)) {
        warnings.push(format!(
            "No project.godot at {abs} — {} is a project-scoped client.",
            agent.name
        ));
    }
    let config_path = match input.config_path.as_deref().filter(|s| !s.is_empty()) {
        Some(c) => PathBuf::from(c),
        None => agent_config_path(&agent, Path::new(&abs)),
    };
    let props = build_stdio_props(input, &abs)?;
    let entry = shape_entry(agent.shape, &props);
    let root = read_existing_config(&config_path)?;
    let (_next, changed) =
        merge_stdio_server_entry(&root, agent.body_path, MCP_SERVER_NAME, &entry, agent.remove_keys);
    Ok(McpPlan {
        agent_id: agent.id.to_string(),
        config_path: config_path.to_string_lossy().to_string(),
        server_name: MCP_SERVER_NAME.to_string(),
        transport: "stdio".to_string(),
        entry,
        changed,
        wrote: false,
        warnings,
    })
}

/// Apply the plan: merge + atomic write (only when changed).
pub fn write_mcp(input: &McpInput) -> Result<McpPlan, String> {
    let abs = resolve_abs_project(input)?;
    let agent = get_agent(&input.agent_id)
        .ok_or_else(|| format!("Unknown agent '{}'.", input.agent_id))?;
    let mut warnings = vec![];
    if agent.scope == Scope::Project && !is_godot_project_root(Path::new(&abs)) {
        return Err(format!(
            "Not a valid Godot project (missing project.godot): {abs}"
        ));
    }
    let config_path = match input.config_path.as_deref().filter(|s| !s.is_empty()) {
        Some(c) => PathBuf::from(c),
        None => agent_config_path(&agent, Path::new(&abs)),
    };
    let props = build_stdio_props(input, &abs)?;
    let entry = shape_entry(agent.shape, &props);
    let root = read_existing_config(&config_path)?;
    let (next, changed) =
        merge_stdio_server_entry(&root, agent.body_path, MCP_SERVER_NAME, &entry, agent.remove_keys);

    let mut wrote = false;
    if changed {
        if let Some(parent) = config_path.parent() {
            std::fs::create_dir_all(parent)
                .map_err(|e| format!("Could not create {}: {e}", parent.display()))?;
        }
        let body = serde_json::to_string_pretty(&next)
            .map_err(|e| format!("Serialize failed: {e}"))?
            + "\n";
        let tmp = config_path.with_extension(format!("json.{}.tmp", std::process::id()));
        std::fs::write(&tmp, &body)
            .map_err(|e| format!("Could not write {}: {e}", tmp.display()))?;
        std::fs::rename(&tmp, &config_path)
            .map_err(|e| format!("Could not finalize {}: {e}", config_path.display()))?;
        wrote = true;
        warnings.push(format!("Wrote {}", config_path.display()));
    }

    Ok(McpPlan {
        agent_id: agent.id.to_string(),
        config_path: config_path.to_string_lossy().to_string(),
        server_name: MCP_SERVER_NAME.to_string(),
        transport: "stdio".to_string(),
        entry,
        changed,
        wrote,
        warnings,
    })
}

/// Remove our server key from an agent's config (Clear AI Setup). Never
/// deletes sibling servers. Returns `(removed, config_path)`.
pub fn clear_mcp(project_path: &str, agent_id: &str) -> Result<(bool, String), String> {
    let agent = get_agent(agent_id).ok_or_else(|| format!("Unknown agent '{agent_id}'."))?;
    let config_path = agent_config_path(&agent, Path::new(project_path));
    if !config_path.exists() {
        return Ok((false, config_path.to_string_lossy().to_string()));
    }
    let mut root = read_existing_config(&config_path)?;
    let mut removed = false;
    {
        let mut cursor = root.as_object_mut();
        let mut ok = true;
        for seg in agent.body_path {
            cursor = match cursor.and_then(|c| c.get_mut(*seg)).and_then(|v| v.as_object_mut()) {
                Some(m) => Some(m),
                None => {
                    ok = false;
                    None
                }
            };
            if !ok {
                break;
            }
        }
        if let Some(servers) = cursor {
            if servers.remove(MCP_SERVER_NAME).is_some() {
                removed = true;
            }
        }
    }
    if removed {
        let body = serde_json::to_string_pretty(&root)
            .map_err(|e| format!("Serialize failed: {e}"))?
            + "\n";
        std::fs::write(&config_path, body)
            .map_err(|e| format!("Could not write {}: {e}", config_path.display()))?;
    }
    Ok((removed, config_path.to_string_lossy().to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn npx_input(project: &str) -> McpInput {
        McpInput {
            project_path: project.to_string(),
            agent_id: "cursor".to_string(),
            source: McpSource::NpxPublished,
            package_version: Some("0.0.1".to_string()),
            monorepo_path: None,
            override_port: None,
            config_path: None,
        }
    }

    #[test]
    fn npx_entry_matches_canonical_shape() {
        let input = npx_input("/abs/proj");
        let props = build_stdio_props(&input, "/abs/proj").unwrap();
        let entry = shape_entry(Shape::Bare, &props);
        assert_eq!(entry["command"], Value::String("npx".into()));
        assert_eq!(entry["args"][0], Value::String("-y".into()));
        assert_eq!(entry["args"][1], Value::String("godot-open-mcp@0.0.1".into()));
        assert_eq!(entry["env"]["GODOT_PROJECT_PATH"], Value::String("/abs/proj".into()));
        // Never emit a url / http type.
        assert!(entry.get("url").is_none());
        assert!(entry.get("type").is_none());
    }

    #[test]
    fn local_entry_uses_node_and_dist_index() {
        let mut input = npx_input("/abs/proj");
        input.source = McpSource::LocalCheckout;
        input.monorepo_path = Some("/repos/Godot-Open-MCP".to_string());
        let props = build_stdio_props(&input, "/abs/proj").unwrap();
        assert_eq!(props.command, "node");
        assert!(props.args[0].ends_with("mcp-server/dist/index.js")
            || props.args[0].contains("mcp-server"));
    }

    #[test]
    fn override_port_written_as_env() {
        let mut input = npx_input("/abs/proj");
        input.override_port = Some(24567);
        let props = build_stdio_props(&input, "/abs/proj").unwrap();
        assert_eq!(
            props.env.get("GODOT_OPEN_MCP_BRIDGE_PORT"),
            Some(&Value::String("24567".into()))
        );
    }

    #[test]
    fn merge_preserves_siblings_and_strips_url() {
        let root = serde_json::json!({
            "mcpServers": {
                "other": { "command": "x" },
                "godot-open-mcp": { "url": "http://x", "type": "http", "notes": "keep" }
            }
        });
        let props = serde_json::json!({
            "command": "npx", "args": ["-y", "godot-open-mcp"], "env": { "GODOT_PROJECT_PATH": "/p" }
        });
        let (next, changed) =
            merge_stdio_server_entry(&root, &["mcpServers"], MCP_SERVER_NAME, &props, BARE_REMOVE_KEYS);
        assert!(changed);
        let entry = &next["mcpServers"]["godot-open-mcp"];
        assert!(entry.get("url").is_none());
        assert!(entry.get("type").is_none());
        assert_eq!(entry["command"], Value::String("npx".into()));
        // User-authored non-foreign key preserved.
        assert_eq!(entry["notes"], Value::String("keep".into()));
        // Sibling preserved.
        assert_eq!(next["mcpServers"]["other"]["command"], Value::String("x".into()));
    }

    #[test]
    fn merge_is_idempotent() {
        let props = serde_json::json!({
            "command": "npx", "args": ["-y", "godot-open-mcp"], "env": { "GODOT_PROJECT_PATH": "/p" }
        });
        let root = serde_json::json!({});
        let (once, c1) =
            merge_stdio_server_entry(&root, &["mcpServers"], MCP_SERVER_NAME, &props, BARE_REMOVE_KEYS);
        assert!(c1);
        let (_twice, c2) =
            merge_stdio_server_entry(&once, &["mcpServers"], MCP_SERVER_NAME, &props, BARE_REMOVE_KEYS);
        assert!(!c2, "re-applying the same entry must report changed:false");
    }

    #[test]
    fn opencode_shape_uses_command_array_and_environment() {
        let input = McpInput {
            agent_id: "opencode".to_string(),
            ..npx_input("/abs/proj")
        };
        let props = build_stdio_props(&input, "/abs/proj").unwrap();
        let entry = shape_entry(Shape::Opencode, &props);
        assert_eq!(entry["type"], Value::String("local".into()));
        assert!(entry["command"].is_array());
        assert_eq!(entry["command"][0], Value::String("npx".into()));
        assert!(entry.get("environment").is_some());
        assert!(entry.get("env").is_none());
    }
}
