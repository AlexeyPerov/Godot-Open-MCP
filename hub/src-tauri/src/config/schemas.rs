//! Serde DTOs for the Hub backend config surface.
//!
//! Field names are `camelCase` to match the TS service layer
//! (`src/lib/services/config.ts`) so the same object round-trips through
//! IPC without renaming. No migrations: an incompatible on-disk `version`
//! is dropped back to defaults (root AGENTS.md — no migrations).

use serde::{Deserialize, Serialize};

/// The only supported schema version for `settings.json` / `projects.json`.
/// Bumped only on a breaking shape change; a mismatch resets to defaults.
pub const CONFIG_VERSION: u32 = 1;

/// Preferred MCP server source. Slim preset space (P11.2): the published npm
/// package via `npx`, or the local monorepo checkout via `node`.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum McpSource {
    NpxPublished,
    LocalCheckout,
}

impl Default for McpSource {
    fn default() -> Self {
        McpSource::NpxPublished
    }
}

/// App preferences persisted under the OS config dir.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    #[serde(default = "default_version")]
    pub version: u32,
    /// Explicit Godot editor binary path (overrides discovery).
    #[serde(default)]
    pub godot_editor_path: Option<String>,
    /// Preferred MCP source (npx vs local checkout).
    #[serde(default)]
    pub mcp_source: McpSource,
    /// Preferred MCP client id (e.g. "cursor").
    #[serde(default)]
    pub default_client_id: Option<String>,
}

fn default_version() -> u32 {
    CONFIG_VERSION
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            version: CONFIG_VERSION,
            godot_editor_path: None,
            mcp_source: McpSource::default(),
            default_client_id: Some("cursor".to_string()),
        }
    }
}

/// One project in the Hub inventory. Slim by design — no Godot version /
/// render-pipeline fields (those are Unity concepts).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectEntry {
    pub id: String,
    /// Absolute path to the folder containing `project.godot`.
    pub path: String,
    /// Display name (from `project.godot` config/name, else folder basename).
    pub name: String,
    pub added_at: String,
    #[serde(default)]
    pub last_opened_at: Option<String>,
}

/// The `projects.json` root.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectsFile {
    #[serde(default = "default_version")]
    pub version: u32,
    #[serde(default)]
    pub projects: Vec<ProjectEntry>,
}

impl Default for ProjectsFile {
    fn default() -> Self {
        ProjectsFile {
            version: CONFIG_VERSION,
            projects: Vec::new(),
        }
    }
}

/// Stable error labels surfaced by `add_project`.
#[derive(Clone, Copy, Debug, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum AddProjectErrorLabel {
    NotGodotProject,
    PathNotFound,
    DuplicateProject,
    InvalidPath,
}

/// Result of an add-project attempt (never throws across IPC).
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AddProjectResult {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error_label: Option<AddProjectErrorLabel>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub project: Option<ProjectEntry>,
}
