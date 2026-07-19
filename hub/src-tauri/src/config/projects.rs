//! Project inventory operations — add (with `project.godot` validation),
//! remove, and touch-last-opened. Mirrors the CLI's project validation
//! (absolute path, `project.godot` marker) and adds Hub-only inventory
//! bookkeeping (stable id, display name, timestamps).

use std::path::{Path, PathBuf};

use crate::config::persistence;
use crate::config::project_godot::{is_godot_project_root, project_godot_path, read_config_name};
use crate::config::schemas::{
    AddProjectErrorLabel, AddProjectResult, ProjectEntry, ProjectsFile,
};

fn now_iso() -> String {
    chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
}

fn fail(label: AddProjectErrorLabel, message: impl Into<String>) -> AddProjectResult {
    AddProjectResult {
        ok: false,
        error_label: Some(label),
        message: Some(message.into()),
        project: None,
    }
}

/// Normalize a candidate path to an absolute, canonical form. Returns `None`
/// when the path is empty or cannot be resolved on disk.
fn normalize(path: &str) -> Option<PathBuf> {
    let trimmed = path.trim();
    if trimmed.is_empty() {
        return None;
    }
    std::fs::canonicalize(trimmed).ok()
}

/// Derive a display name: `project.godot` `config/name`, else folder basename.
fn derive_name(project_root: &Path) -> String {
    if let Ok(text) = std::fs::read_to_string(project_godot_path(project_root)) {
        if let Some(name) = read_config_name(&text) {
            return name;
        }
    }
    project_root
        .file_name()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| project_root.to_string_lossy().to_string())
}

/// Validate + append a project to the inventory, persisting the result.
/// Pure-ish: the only side effect is the atomic `projects.json` write.
pub fn add_project(path: &str) -> AddProjectResult {
    if path.trim().is_empty() {
        return fail(AddProjectErrorLabel::InvalidPath, "Path is empty.");
    }
    let Some(abs) = normalize(path) else {
        return fail(
            AddProjectErrorLabel::PathNotFound,
            format!("Folder not found: {path}"),
        );
    };
    if !abs.is_dir() {
        return fail(
            AddProjectErrorLabel::InvalidPath,
            format!("Not a folder: {}", abs.display()),
        );
    }
    if !is_godot_project_root(&abs) {
        return fail(
            AddProjectErrorLabel::NotGodotProject,
            format!(
                "Not a Godot project (missing project.godot): {}",
                abs.display()
            ),
        );
    }

    let mut file = persistence::load_projects();
    let abs_str = abs.to_string_lossy().to_string();
    if file
        .projects
        .iter()
        .any(|p| normalize(&p.path).map(|n| n == abs).unwrap_or(p.path == abs_str))
    {
        return fail(
            AddProjectErrorLabel::DuplicateProject,
            format!("Project already added: {}", abs.display()),
        );
    }

    let entry = ProjectEntry {
        id: uuid::Uuid::new_v4().to_string(),
        path: abs_str,
        name: derive_name(&abs),
        added_at: now_iso(),
        last_opened_at: None,
    };
    file.projects.push(entry.clone());
    if let Err(e) = persistence::save_projects(&file) {
        return fail(
            AddProjectErrorLabel::InvalidPath,
            format!("Could not persist projects.json: {e}"),
        );
    }

    AddProjectResult {
        ok: true,
        error_label: None,
        message: None,
        project: Some(entry),
    }
}

/// Remove a project by id, persisting the result. Returns the new file.
pub fn remove_project(id: &str) -> std::io::Result<ProjectsFile> {
    let mut file = persistence::load_projects();
    file.projects.retain(|p| p.id != id);
    persistence::save_projects(&file)?;
    Ok(file)
}

/// Update `last_opened_at` for a project by id.
pub fn touch_opened(id: &str) -> std::io::Result<ProjectsFile> {
    let mut file = persistence::load_projects();
    if let Some(p) = file.projects.iter_mut().find(|p| p.id == id) {
        p.last_opened_at = Some(now_iso());
    }
    persistence::save_projects(&file)?;
    Ok(file)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn derive_name_prefers_config_name() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("project.godot"),
            "[application]\n\nconfig/name=\"Fixture Game\"\n",
        )
        .unwrap();
        assert_eq!(derive_name(dir.path()), "Fixture Game");
    }

    #[test]
    fn derive_name_falls_back_to_basename() {
        let dir = tempfile::tempdir().unwrap();
        let sub = dir.path().join("MyProj");
        std::fs::create_dir_all(&sub).unwrap();
        std::fs::write(sub.join("project.godot"), "[application]\n").unwrap();
        assert_eq!(derive_name(&sub), "MyProj");
    }

    #[test]
    fn normalize_rejects_empty() {
        assert!(normalize("   ").is_none());
    }
}
