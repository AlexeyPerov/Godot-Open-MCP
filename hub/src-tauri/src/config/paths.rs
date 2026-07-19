//! Hub config-dir path resolution.
//!
//! The Hub persists `settings.json` + `projects.json` under a named subdir of
//! the OS config dir. Kept distinct from the Validation Suite's dir so the two
//! apps never share state.
//!
//! | Platform | Path |
//! |----------|------|
//! | macOS / Linux | `~/.config/godot-open-mcp-hub/` |
//! | Windows | `%APPDATA%\godot-open-mcp-hub\` |
//!
//! (`dirs::config_dir()` resolves the correct base per platform; on macOS it
//! returns `~/Library/Application Support`, which is the platform-native
//! config location and is acceptable — the subdir name is what matters.)

use std::path::PathBuf;

const CONFIG_DIR_NAME: &str = "godot-open-mcp-hub";

/// The OS config dir for the Hub. Falls back to `.` if the OS has no config
/// dir concept (keeps the app usable in odd environments).
pub fn config_dir() -> PathBuf {
    let base = dirs::config_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join(CONFIG_DIR_NAME)
}

pub fn settings_path() -> PathBuf {
    config_dir().join("settings.json")
}

pub fn projects_path() -> PathBuf {
    config_dir().join("projects.json")
}

/// Create the config dir if it does not yet exist.
pub fn ensure_config_dir() -> std::io::Result<()> {
    let dir = config_dir();
    if !dir.exists() {
        std::fs::create_dir_all(&dir)?;
    }
    Ok(())
}
