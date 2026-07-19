//! Bundled engine-profile loader.
//!
//! Reads `engine-profiles/<id>.json` from the resolved resource/dev
//! directory and deserializes it into an [`EngineProfile`]. Profiles
//! are bundled with the app, so a missing/bad profile is a build error
//! surfaced as a readable error string (not a silent default).

use std::path::PathBuf;

use crate::paths;
use crate::schemas::EngineProfile;

/// Load a profile by id (e.g. `"godot"`). Returns an error string on a
/// missing or malformed file so the Tauri command can forward it to
/// the UI as a readable message.
pub fn load_profile(
    id: &str,
    resource_dir: Option<&PathBuf>,
) -> Result<EngineProfile, String> {
    let dir = paths::engine_profiles_dir(resource_dir);
    let file = dir.join(format!("{id}.json"));
    if !file.is_file() {
        return Err(format!(
            "Engine profile \"{id}\" not found at {}",
            file.display()
        ));
    }
    let content = std::fs::read_to_string(&file)
        .map_err(|e| format!("Cannot read profile \"{id}\": {e}"))?;
    serde_json::from_str::<EngineProfile>(&content)
        .map_err(|e| format!("Profile \"{id}\" is malformed: {e}"))
}

/// Resolve the active profile for a project. v1 always loads `godot`;
/// the indirection exists so a future multi-engine app can derive the
/// profile id from the selected project.
pub fn active_profile(
    resource_dir: Option<&PathBuf>,
) -> Result<EngineProfile, String> {
    load_profile("godot", resource_dir)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn load_godot_profile_from_repo() {
        // The bundled godot.json ships in the repo; the dev/test
        // fallback in paths resolves it via CARGO_MANIFEST_DIR.
        let profile = load_profile("godot", None).expect("godot.json should load");
        assert_eq!(profile.id, "godot");
        assert_eq!(profile.display_name, "Godot Open MCP");
        // Godot detects projects by a `project.godot` marker file with no
        // required directories (empty `markers.dirs` is accepted).
        assert!(profile.markers.dirs.is_empty());
        assert_eq!(profile.markers.files, vec!["project.godot"]);
        assert_eq!(profile.tool_name_prefix, "godot_open_mcp_");
        assert!(!profile.companions.is_empty());
    }

    #[test]
    fn missing_profile_returns_readable_error() {
        let err = load_profile("does-not-exist", None).unwrap_err();
        assert!(err.contains("not found"));
    }
}
