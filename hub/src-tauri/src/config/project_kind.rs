//! Project-kind detection for the maintainer gate (P11.3).
//!
//! | Kind | Meaning | Maintainer npm panel |
//! |------|---------|----------------------|
//! | `OpenMcp` | This tooling monorepo (`mcp-server/package.json` name `godot-open-mcp`) | show |
//! | `GodotProject` | Game project with `project.godot` | hide |
//! | `Custom` | Unknown folder | hide |
//!
//! Cheap, filesystem-only — never shells out.

use std::path::Path;

use serde::Serialize;

use crate::config::project_godot::is_godot_project_root;

/// The npm package name that identifies this monorepo's publishable package.
pub const OPEN_MCP_PACKAGE_NAME: &str = "godot-open-mcp";

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ProjectKind {
    OpenMcp,
    GodotProject,
    Custom,
}

/// Detect the kind of a selected folder. `OpenMcp` wins when the monorepo
/// marker (`mcp-server/package.json` with name `godot-open-mcp`) is present,
/// even if the folder also happens to contain a `project.godot`.
pub fn detect_project_kind(path: &Path) -> ProjectKind {
    if is_open_mcp_repo(path) {
        return ProjectKind::OpenMcp;
    }
    if is_godot_project_root(path) {
        return ProjectKind::GodotProject;
    }
    ProjectKind::Custom
}

/// True when `<path>/mcp-server/package.json` exists and its `name` field is
/// `godot-open-mcp`.
pub fn is_open_mcp_repo(path: &Path) -> bool {
    let manifest = path.join("mcp-server").join("package.json");
    let Ok(text) = std::fs::read_to_string(&manifest) else {
        return false;
    };
    let Ok(json) = serde_json::from_str::<serde_json::Value>(&text) else {
        return false;
    };
    json.get("name").and_then(|v| v.as_str()) == Some(OPEN_MCP_PACKAGE_NAME)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write(p: &Path, body: &str) {
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent).unwrap();
        }
        std::fs::write(p, body).unwrap();
    }

    #[test]
    fn detects_open_mcp_repo() {
        let dir = tempfile::tempdir().unwrap();
        write(
            &dir.path().join("mcp-server/package.json"),
            r#"{ "name": "godot-open-mcp", "version": "0.0.1" }"#,
        );
        assert_eq!(detect_project_kind(dir.path()), ProjectKind::OpenMcp);
        assert!(is_open_mcp_repo(dir.path()));
    }

    #[test]
    fn open_mcp_wins_even_with_project_godot() {
        let dir = tempfile::tempdir().unwrap();
        write(
            &dir.path().join("mcp-server/package.json"),
            r#"{ "name": "godot-open-mcp" }"#,
        );
        write(&dir.path().join("project.godot"), "[application]\n");
        assert_eq!(detect_project_kind(dir.path()), ProjectKind::OpenMcp);
    }

    #[test]
    fn detects_godot_game_project() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("project.godot"), "[application]\n");
        assert_eq!(detect_project_kind(dir.path()), ProjectKind::GodotProject);
    }

    #[test]
    fn wrong_package_name_is_not_open_mcp() {
        let dir = tempfile::tempdir().unwrap();
        write(
            &dir.path().join("mcp-server/package.json"),
            r#"{ "name": "something-else" }"#,
        );
        assert_eq!(detect_project_kind(dir.path()), ProjectKind::Custom);
    }

    #[test]
    fn empty_folder_is_custom() {
        let dir = tempfile::tempdir().unwrap();
        assert_eq!(detect_project_kind(dir.path()), ProjectKind::Custom);
    }
}
