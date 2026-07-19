//! Engine project detection for the Validation Suite.
//!
//! Cheap, filesystem-only detection (never shells out) driven entirely
//! by the engine profile's declared markers instead of a hardcoded
//! ladder — so each engine profile declares its own detection rules
//! without backend changes.
//!
//! A folder is valid when it has all of `markers.dirs` and at least one
//! of `markers.files`. The Godot profile declares no required dirs and a
//! single `project.godot` marker file (empty `markers.dirs` is accepted).

use std::path::Path;

use crate::schemas::{EngineProfile, ProjectCheck};

/// Validate a candidate project folder against an engine profile's
/// markers. Never panics — returns a `ProjectCheck` with a clear,
/// human-readable reason on rejection so the project bar can show
/// actionable copy (phase-1 task 3: reject non-Unity folders with
/// clear error).
pub fn check_project(path: &Path, profile: &EngineProfile) -> ProjectCheck {
    let path_str = path.to_string_lossy().to_string();
    if !path.is_dir() {
        return ProjectCheck {
            valid: false,
            path: path_str.clone(),
            reason: Some(format!(
                "Not a directory: {path_str}. Pick the {display} project root folder.",
                display = profile.display_name
            )),
        };
    }
    // All declared dirs must exist.
    for d in &profile.markers.dirs {
        if !path.join(d).is_dir() {
            return ProjectCheck {
                valid: false,
                path: path_str,
                reason: Some(format!(
                    "Not a {display} project: missing required folder \"{d}\".",
                    display = profile.display_name
                )),
            };
        }
    }
    // At least one declared marker file must exist.
    if !profile.markers.files.is_empty() {
        let any = profile.markers.files.iter().any(|f| path.join(f).is_file());
        if !any {
            return ProjectCheck {
                valid: false,
                path: path_str,
                reason: Some(format!(
                    "Not a {display} project: none of the marker files ({files}) were found.",
                    display = profile.display_name,
                    files = profile.markers.files.join(", ")
                )),
            };
        }
    }
    ProjectCheck {
        valid: true,
        path: path_str,
        reason: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schemas::{CompanionRule, ProfilePaths, ProjectMarkers};

    fn godot_profile() -> EngineProfile {
        EngineProfile {
            id: "godot".to_string(),
            display_name: "Godot Open MCP".to_string(),
            mcp_cli_binary: "godot-open-mcp".to_string(),
            paths: ProfilePaths {
                fixture_root: "_ValidationSuite/<test-id>/".to_string(),
                state_root: ".godot-open-mcp/ValidationSuite/".to_string(),
                state_file: ".godot-open-mcp/ValidationSuite/.state.json".to_string(),
                actuals_dir: ".godot-open-mcp/ValidationSuite/actuals/".to_string(),
                exports_dir: ".godot-open-mcp/ValidationSuite/exports/".to_string(),
            },
            markers: ProjectMarkers {
                dirs: vec![],
                files: vec!["project.godot".to_string()],
            },
            companions: vec![CompanionRule {
                primary: "*.tscn".to_string(),
                companion: "*.tscn.uid".to_string(),
            }],
            placeholders: vec!["{fixtureRoot}".to_string(), "{projectRoot}".to_string()],
            tool_name_prefix: "godot_open_mcp_".to_string(),
        }
    }

    fn mkdir(p: &Path) {
        std::fs::create_dir_all(p).unwrap();
    }
    fn touch(p: &Path) {
        if let Some(parent) = p.parent() {
            mkdir(parent);
        }
        std::fs::write(p, b"[application]").unwrap();
    }

    #[test]
    fn valid_godot_project_passes() {
        // A folder with `project.godot` and no required dirs is accepted —
        // empty `markers.dirs` must not reject.
        let dir = tempfile::tempdir().unwrap();
        touch(&dir.path().join("project.godot"));
        let check = check_project(dir.path(), &godot_profile());
        assert!(check.valid);
        assert!(check.reason.is_none());
    }

    #[test]
    fn missing_project_godot_rejected_with_reason() {
        let dir = tempfile::tempdir().unwrap();
        mkdir(&dir.path().join("Scenes"));
        let check = check_project(dir.path(), &godot_profile());
        assert!(!check.valid);
        assert!(check.reason.unwrap().contains("marker files"));
    }

    #[test]
    fn non_directory_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("notafolder");
        std::fs::write(&file, b"x").unwrap();
        let check = check_project(&file, &godot_profile());
        assert!(!check.valid);
        assert!(check.reason.unwrap().contains("Not a directory"));
    }
}
