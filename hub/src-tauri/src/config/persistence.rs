//! Atomic persistence + corrupt-recovery for the Hub config files.
//!
//! Write path: serialize → write to a sibling `.tmp` → `sync_all` → rename
//! onto the target (crash-safe atomic replace). Read path: a missing file
//! yields defaults; a corrupt or version-incompatible file is backed up to
//! `<name>.json.corrupt` and replaced with defaults (no migrations — root
//! AGENTS.md).

use std::fs;
use std::io::Write;
use std::path::Path;

use crate::config::paths;
use crate::config::schemas::{ProjectsFile, Settings, CONFIG_VERSION};

/// Rename a corrupt file to `<name>.json.corrupt` so the operator can recover
/// it. Best-effort — never fails the caller.
fn backup_corrupt(path: &Path) {
    let backup = path.with_extension("json.corrupt");
    let _ = fs::rename(path, &backup);
    log::warn!("Corrupt config backed up to {}", backup.display());
}

/// Atomically write `data` to `path` via a temp file + rename.
pub fn atomic_write(path: &Path, data: &str) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let tmp_path = path.with_extension("json.tmp");
    {
        let mut tmp = fs::File::create(&tmp_path)?;
        tmp.write_all(data.as_bytes())?;
        tmp.sync_all()?;
    }
    fs::rename(&tmp_path, path)?;
    Ok(())
}

/// True when the parsed JSON has a `version` matching [`CONFIG_VERSION`].
/// A missing/`null` version is treated as incompatible (reset).
fn version_ok(value: &serde_json::Value) -> bool {
    value
        .get("version")
        .and_then(|v| v.as_u64())
        .map(|v| v as u32 == CONFIG_VERSION)
        .unwrap_or(false)
}

// ── settings.json ─────────────────────────────────────────────────────────

pub fn load_settings() -> Settings {
    let path = paths::settings_path();
    if !path.exists() {
        return Settings::default();
    }
    let content = match fs::read_to_string(&path) {
        Ok(c) => c,
        Err(e) => {
            log::warn!("Cannot read settings.json ({e}); using defaults");
            return Settings::default();
        }
    };
    let value: serde_json::Value = match serde_json::from_str(&content) {
        Ok(v) => v,
        Err(e) => {
            log::warn!("Corrupt settings.json ({e}); resetting to defaults");
            backup_corrupt(&path);
            return Settings::default();
        }
    };
    if !version_ok(&value) {
        log::warn!("Incompatible settings.json version; resetting to defaults");
        backup_corrupt(&path);
        return Settings::default();
    }
    match serde_json::from_value::<Settings>(value) {
        Ok(s) => s,
        Err(e) => {
            log::warn!("settings.json failed schema ({e}); resetting to defaults");
            backup_corrupt(&path);
            Settings::default()
        }
    }
}

pub fn save_settings(settings: &Settings) -> std::io::Result<()> {
    paths::ensure_config_dir()?;
    let json = serde_json::to_string_pretty(settings)?;
    atomic_write(&paths::settings_path(), &json)
}

// ── projects.json ─────────────────────────────────────────────────────────

pub fn load_projects() -> ProjectsFile {
    let path = paths::projects_path();
    if !path.exists() {
        return ProjectsFile::default();
    }
    let content = match fs::read_to_string(&path) {
        Ok(c) => c,
        Err(e) => {
            log::warn!("Cannot read projects.json ({e}); using defaults");
            return ProjectsFile::default();
        }
    };
    let value: serde_json::Value = match serde_json::from_str(&content) {
        Ok(v) => v,
        Err(e) => {
            log::warn!("Corrupt projects.json ({e}); resetting to defaults");
            backup_corrupt(&path);
            return ProjectsFile::default();
        }
    };
    if !version_ok(&value) {
        log::warn!("Incompatible projects.json version; resetting to defaults");
        backup_corrupt(&path);
        return ProjectsFile::default();
    }
    match serde_json::from_value::<ProjectsFile>(value) {
        Ok(p) => p,
        Err(e) => {
            log::warn!("projects.json failed schema ({e}); resetting to defaults");
            backup_corrupt(&path);
            ProjectsFile::default()
        }
    }
}

pub fn save_projects(projects: &ProjectsFile) -> std::io::Result<()> {
    paths::ensure_config_dir()?;
    let json = serde_json::to_string_pretty(projects)?;
    atomic_write(&paths::projects_path(), &json)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn atomic_write_creates_and_overwrites() {
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("x.json");
        atomic_write(&p, "first").unwrap();
        atomic_write(&p, "second").unwrap();
        assert_eq!(fs::read_to_string(&p).unwrap(), "second");
        assert!(!dir.path().join("x.json.tmp").exists());
    }

    #[test]
    fn corrupt_json_is_backed_up() {
        // Mirrors the acceptance criterion: a corrupt file recovers to
        // defaults and leaves a `.json.corrupt` backup. We exercise the
        // shared recovery helper directly (load_* uses the fixed config dir).
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("settings.json");
        fs::write(&p, "{ not json").unwrap();
        // Simulate the load path's recovery step.
        backup_corrupt(&p);
        assert!(!p.exists());
        assert!(p.with_extension("json.corrupt").exists());
    }

    #[test]
    fn version_ok_matches_current_only() {
        assert!(version_ok(&serde_json::json!({ "version": CONFIG_VERSION })));
        assert!(!version_ok(&serde_json::json!({ "version": 999 })));
        assert!(!version_ok(&serde_json::json!({})));
    }
}
