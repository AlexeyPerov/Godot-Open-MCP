//! Tauri command surface — the single IPC seam between the Svelte UI and the
//! Rust `config` modules. Commands are thin: they map camelCase JS args to the
//! module functions (which mirror the `godot-open-mcp-cli` contracts) and
//! return plain serializable types. All behavior lives in `config::*`.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::config::ai_toolkit::{self, InstallResult, LaunchResult, PingResult};
use crate::config::command_runner::{self, CommandResult, McpPackageInfo};
use crate::config::mcp_config::{self, McpInput, McpPlan};
use crate::config::project_kind::{self, ProjectKind};
use crate::config::projects;
use crate::config::schemas::{AddProjectResult, McpSource, ProjectsFile, Settings};
use crate::config::{bridge_port, persistence, wizard};

// ── P11.1: persistence + inventory + port ────────────────────────────────────

#[tauri::command]
pub fn load_settings() -> Settings {
    persistence::load_settings()
}

#[tauri::command]
pub fn save_settings(settings: Settings) -> Result<(), String> {
    persistence::save_settings(&settings).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn load_projects() -> ProjectsFile {
    persistence::load_projects()
}

#[tauri::command]
pub fn add_project(path: String) -> AddProjectResult {
    projects::add_project(&path)
}

#[tauri::command]
pub fn remove_project(id: String) -> Result<ProjectsFile, String> {
    projects::remove_project(&id).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn touch_project_opened(id: String) -> Result<ProjectsFile, String> {
    projects::touch_opened(&id).map_err(|e| e.to_string())
}

#[tauri::command]
pub fn resolve_bridge_port(project_path: String, override_port: Option<u16>) -> u16 {
    bridge_port::resolve_port(&project_path, override_port)
}

// ── P11.3 gate (also used by the Projects tab) ───────────────────────────────

#[tauri::command]
pub fn detect_project_kind(path: String) -> ProjectKind {
    project_kind::detect_project_kind(Path::new(&path))
}

// ── P11.2: wizard detection + MCP client roster ──────────────────────────────

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentInfo {
    pub id: String,
    pub name: String,
    pub scope: String,
    pub config_path_display: String,
}

#[tauri::command]
pub fn list_agents() -> Vec<AgentInfo> {
    mcp_config::agent_registry()
        .into_iter()
        .map(|a| AgentInfo {
            id: a.id.to_string(),
            name: a.name.to_string(),
            scope: match a.scope {
                mcp_config::Scope::Project => "project".to_string(),
                mcp_config::Scope::Global => "global".to_string(),
            },
            config_path_display: a.config_path_display.to_string(),
        })
        .collect()
}

#[tauri::command]
pub fn detect_project_state(
    project_path: String,
    client_ids: Vec<String>,
) -> wizard::ProjectState {
    wizard::detect_project_state(&project_path, &client_ids)
}

// ── P11.2: MCP config plan/write ─────────────────────────────────────────────

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpInputDto {
    pub project_path: String,
    pub agent_id: String,
    pub source: McpSource,
    #[serde(default)]
    pub package_version: Option<String>,
    #[serde(default)]
    pub monorepo_path: Option<String>,
    #[serde(default)]
    pub override_port: Option<u16>,
    #[serde(default)]
    pub config_path: Option<String>,
}

impl From<McpInputDto> for McpInput {
    fn from(d: McpInputDto) -> Self {
        McpInput {
            project_path: d.project_path,
            agent_id: d.agent_id,
            source: d.source,
            package_version: d.package_version,
            monorepo_path: d.monorepo_path,
            override_port: d.override_port,
            config_path: d.config_path,
        }
    }
}

#[tauri::command]
pub fn plan_mcp_config(input: McpInputDto) -> Result<McpPlan, String> {
    mcp_config::plan_mcp(&input.into())
}

#[tauri::command]
pub fn write_mcp_config(input: McpInputDto) -> Result<McpPlan, String> {
    mcp_config::write_mcp(&input.into())
}

// ── P11.2: install addon / launch / ping / clear / skill ──────────────────────

#[tauri::command]
pub fn install_addon(
    project_path: String,
    source: Option<String>,
    monorepo_path: Option<String>,
) -> InstallResult {
    ai_toolkit::install_addon(&project_path, source.as_deref(), monorepo_path.as_deref())
}

#[tauri::command]
pub fn launch_godot(project_path: String, editor_path: Option<String>) -> LaunchResult {
    ai_toolkit::launch_editor(&project_path, editor_path.as_deref())
}

#[tauri::command]
pub fn poll_bridge_ping(project_path: String, override_port: Option<u16>) -> PingResult {
    ai_toolkit::poll_bridge_ping(&project_path, override_port)
}

#[tauri::command]
pub fn clear_ai_setup(
    project_path: String,
    client_ids: Vec<String>,
    disable_plugin: bool,
) -> wizard::ClearResult {
    wizard::clear_ai_setup(&project_path, &client_ids, disable_plugin)
}

/// Optional skill copy: if `<monorepo>/skills/godot-open-mcp/SKILL.md` exists,
/// copy the skill dir into `<project>/.godot-open-mcp/skills/godot-open-mcp/`.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillCopyResult {
    pub ok: bool,
    pub copied: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dest: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

#[tauri::command]
pub fn copy_skill_files(project_path: String, monorepo_path: Option<String>) -> SkillCopyResult {
    let Some(repo) = monorepo_path.filter(|s| !s.is_empty()) else {
        return SkillCopyResult {
            ok: true,
            copied: false,
            dest: None,
            message: Some("No monorepo path — skill copy skipped (optional).".into()),
        };
    };
    let src = PathBuf::from(&repo).join("skills").join("godot-open-mcp");
    if !src.join("SKILL.md").exists() {
        return SkillCopyResult {
            ok: true,
            copied: false,
            dest: None,
            message: Some("No skills/godot-open-mcp/ found — skipped (optional).".into()),
        };
    }
    let dest = PathBuf::from(&project_path)
        .join(".godot-open-mcp")
        .join("skills")
        .join("godot-open-mcp");
    match copy_tree(&src, &dest) {
        Ok(()) => SkillCopyResult {
            ok: true,
            copied: true,
            dest: Some(dest.to_string_lossy().to_string()),
            message: None,
        },
        Err(e) => SkillCopyResult {
            ok: false,
            copied: false,
            dest: None,
            message: Some(format!("Skill copy failed: {e}")),
        },
    }
}

fn copy_tree(src: &Path, dest: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(dest)?;
    for entry in std::fs::read_dir(src)? {
        let entry = entry?;
        let ft = entry.file_type()?;
        let target = dest.join(entry.file_name());
        if ft.is_dir() {
            copy_tree(&entry.path(), &target)?;
        } else if ft.is_file() {
            std::fs::copy(entry.path(), target)?;
        }
    }
    Ok(())
}

// ── P11.3: maintainer npm ─────────────────────────────────────────────────────

#[tauri::command]
pub fn read_mcp_package_info(project_path: String) -> Result<McpPackageInfo, String> {
    command_runner::read_mcp_package_info(&project_path)
}

#[tauri::command]
pub fn run_npm_script(project_path: String, args: Vec<String>) -> CommandResult {
    let kind = project_kind::detect_project_kind(Path::new(&project_path));
    command_runner::run_npm(&project_path, kind, &args)
}

#[tauri::command]
pub fn run_version_sync(project_path: String, args: Vec<String>) -> CommandResult {
    command_runner::run_version_sync(&project_path, &args)
}
