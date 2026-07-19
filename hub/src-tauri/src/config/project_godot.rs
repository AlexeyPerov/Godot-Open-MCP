//! Pure `project.godot` helpers — the Rust port of the CLI's
//! `cli/src/utils/project-godot.ts`. Enable/disable the Godot Open MCP plugin
//! in the `[editor_plugins]` `enabled` PackedStringArray without touching
//! unrelated sections, plus read the display name and the project marker.
//!
//! Kept behaviorally identical to the TS so the Hub's install path produces
//! the same `project.godot` edits the CLI's `install-plugin` does.

use std::path::{Path, PathBuf};

/// Canonical resource path of the installed plugin.cfg inside a Godot project.
pub const GODOT_OPEN_MCP_PLUGIN_PATH: &str = "res://addons/godot_open_mcp/plugin.cfg";

/// Absolute path to a project's `project.godot` manifest.
pub fn project_godot_path(project_root: &Path) -> PathBuf {
    project_root.join("project.godot")
}

/// True when `<projectRoot>/project.godot` exists.
pub fn is_godot_project_root(project_root: &Path) -> bool {
    project_godot_path(project_root).is_file()
}

/// Read the `config/name="..."` value from the `[application]` section, if any.
pub fn read_config_name(text: &str) -> Option<String> {
    for line in text.lines() {
        let trimmed = line.trim_start();
        if let Some(rest) = trimmed.strip_prefix("config/name") {
            // rest looks like: `="My Game"` (optional spaces around `=`)
            let rest = rest.trim_start();
            if let Some(after_eq) = rest.strip_prefix('=') {
                let val = after_eq.trim();
                let unquoted = val.trim_matches('"');
                if !unquoted.is_empty() {
                    return Some(unquoted.to_string());
                }
            }
        }
    }
    None
}

/// Extract the raw text of the `[editor_plugins]` section (between its header
/// and the next `[section]` header or EOF). Returns `None` when absent.
fn extract_editor_plugins_section(text: &str) -> Option<String> {
    let lines: Vec<&str> = text.split('\n').collect();
    let header_idx = lines.iter().position(|l| l.trim() == "[editor_plugins]")?;
    let mut end = header_idx + 1;
    while end < lines.len() && !lines[end].trim().starts_with('[') {
        end += 1;
    }
    Some(lines[(header_idx + 1)..end].join("\n"))
}

/// Parse the ordered list of enabled plugin.cfg paths from the
/// `enabled=PackedStringArray("a", "b")` line inside `[editor_plugins]`.
pub fn parse_enabled_plugins(text: &str) -> Vec<String> {
    let Some(section) = extract_editor_plugins_section(text) else {
        return vec![];
    };
    let Some(start) = section.find("enabled") else {
        return vec![];
    };
    let after = &section[start..];
    let Some(open) = after.find("PackedStringArray(") else {
        return vec![];
    };
    let inner_start = open + "PackedStringArray(".len();
    let Some(close_rel) = after[inner_start..].find(')') else {
        return vec![];
    };
    let inner = &after[inner_start..inner_start + close_rel];
    let mut out = vec![];
    let mut chars = inner.chars().peekable();
    let mut current = String::new();
    let mut in_str = false;
    while let Some(c) = chars.next() {
        if in_str {
            if c == '"' {
                out.push(std::mem::take(&mut current));
                in_str = false;
            } else {
                current.push(c);
            }
        } else if c == '"' {
            in_str = true;
        }
    }
    out
}

/// Render a `PackedStringArray(...)` literal from plugin paths.
fn render_enabled_line(paths: &[String]) -> String {
    let quoted: Vec<String> = paths.iter().map(|p| format!("\"{p}\"")).collect();
    format!("enabled=PackedStringArray({})", quoted.join(", "))
}

/// Outcome of a plugin toggle.
pub enum ToggleResult {
    Changed { text: String, enabled: Vec<String> },
    Unchanged { enabled: Vec<String> },
}

/// Return a new `project.godot` body with `plugin_path` added (enable) or
/// removed (disable) from `[editor_plugins] enabled`. Creates the section/line
/// when enabling and it is absent. Idempotent.
pub fn toggle_plugin_in_text(text: &str, plugin_path: &str, enable: bool) -> ToggleResult {
    let current = parse_enabled_plugins(text);
    let has = current.iter().any(|p| p == plugin_path);

    if enable && has {
        return ToggleResult::Unchanged { enabled: current };
    }
    if !enable && !has {
        return ToggleResult::Unchanged { enabled: current };
    }

    let next: Vec<String> = if enable {
        let mut v = current.clone();
        v.push(plugin_path.to_string());
        v
    } else {
        current.into_iter().filter(|p| p != plugin_path).collect()
    };

    let new_text = write_enabled_array(text, &next);
    ToggleResult::Changed {
        text: new_text,
        enabled: next,
    }
}

/// Write the `enabled=PackedStringArray(...)` line into `[editor_plugins]`,
/// creating the section/line as needed. Preserves the Godot convention of a
/// blank line between the header and the `enabled` line on creation.
fn write_enabled_array(text: &str, paths: &[String]) -> String {
    let mut lines: Vec<String> = text.split('\n').map(|s| s.to_string()).collect();
    let header_idx = lines.iter().position(|l| l.trim() == "[editor_plugins]");
    let enabled_line = render_enabled_line(paths);

    let Some(header_idx) = header_idx else {
        // Append a fresh [editor_plugins] section.
        let prefix = if !text.is_empty() && !text.ends_with('\n') {
            "\n"
        } else {
            ""
        };
        let sep = if !text.trim().is_empty() { "\n" } else { "" };
        return format!("{text}{prefix}{sep}[editor_plugins]\n\n{enabled_line}\n");
    };

    let mut end = header_idx + 1;
    while end < lines.len() && !lines[end].trim().starts_with('[') {
        end += 1;
    }
    let enabled_idx = (header_idx + 1..end).find(|&i| {
        let t = lines[i].trim_start();
        t.starts_with("enabled") && t[7..].trim_start().starts_with('=')
    });

    if let Some(idx) = enabled_idx {
        lines[idx] = enabled_line;
    } else {
        let insert_at = if header_idx + 1 < lines.len() && lines[header_idx + 1].trim().is_empty() {
            header_idx + 2
        } else {
            header_idx + 1
        };
        lines.insert(insert_at, enabled_line);
    }

    lines.join("\n")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_config_name() {
        let text = "[application]\n\nconfig/name=\"My Cool Game\"\n";
        assert_eq!(read_config_name(text).as_deref(), Some("My Cool Game"));
    }

    #[test]
    fn parses_enabled_plugins() {
        let text =
            "[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/a/plugin.cfg\", \"res://addons/godot_open_mcp/plugin.cfg\")\n";
        let got = parse_enabled_plugins(text);
        assert_eq!(got.len(), 2);
        assert!(got.iter().any(|p| p == GODOT_OPEN_MCP_PLUGIN_PATH));
    }

    #[test]
    fn enable_is_idempotent() {
        let text = format!(
            "[editor_plugins]\n\nenabled=PackedStringArray(\"{}\")\n",
            GODOT_OPEN_MCP_PLUGIN_PATH
        );
        match toggle_plugin_in_text(&text, GODOT_OPEN_MCP_PLUGIN_PATH, true) {
            ToggleResult::Unchanged { enabled } => assert_eq!(enabled.len(), 1),
            ToggleResult::Changed { .. } => panic!("expected unchanged"),
        }
    }

    #[test]
    fn enable_creates_section_when_absent() {
        let text = "[application]\n\nconfig/name=\"X\"\n";
        match toggle_plugin_in_text(text, GODOT_OPEN_MCP_PLUGIN_PATH, true) {
            ToggleResult::Changed { text: out, enabled } => {
                assert!(out.contains("[editor_plugins]"));
                assert!(out.contains(GODOT_OPEN_MCP_PLUGIN_PATH));
                assert_eq!(enabled, vec![GODOT_OPEN_MCP_PLUGIN_PATH.to_string()]);
            }
            ToggleResult::Unchanged { .. } => panic!("expected changed"),
        }
    }

    #[test]
    fn enable_appends_to_existing_array() {
        let text = "[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/other/plugin.cfg\")\n";
        match toggle_plugin_in_text(text, GODOT_OPEN_MCP_PLUGIN_PATH, true) {
            ToggleResult::Changed { text: out, enabled } => {
                assert_eq!(enabled.len(), 2);
                assert!(out.contains("res://addons/other/plugin.cfg"));
                assert!(out.contains(GODOT_OPEN_MCP_PLUGIN_PATH));
            }
            ToggleResult::Unchanged { .. } => panic!("expected changed"),
        }
    }
}
