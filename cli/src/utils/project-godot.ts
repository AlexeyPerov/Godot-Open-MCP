// Pure `project.godot` helpers — enable/disable the Godot Open MCP plugin in the
// `[editor_plugins]` `enabled` array without touching unrelated sections.
//
// Adapted from the Godot-MCP behavior reference
// (`cli/src/utils/project-godot.ts`) with two intentional deltas:
//   1. The plugin path is `res:// addons/godot_open_mcp/plugin.cfg` (not
//      `godot_mcp`) — ADR-003 naming.
//   2. `togglePluginInText(text, pluginPath, enable)` takes the plugin path as
//      the second arg and the boolean last, so a single helper can toggle any
//      addon path (forward-compat for a future sibling verify addon).
//
// Pure — `parseEnabledPlugins` and `togglePluginInText` operate on the file text
// only, no I/O, no throws. Exported for unit tests. The only fs-touching helper
// is `isGodotProjectRoot` (a single existsSync). The Godot `project.godot`
// ini-like format writes the enabled editor plugins as a `PackedStringArray`:
//
//   [editor_plugins]
//
//   enabled=PackedStringArray("res://addons/foo/plugin.cfg")
//
// Conventions preserved from the Godot editor: a blank line sits between the
// section header and the `enabled` key, and section order / surrounding sections
// are left untouched.

import * as fs from "fs";
import * as path from "path";

/** Canonical resource path of the installed plugin.cfg inside a Godot project. */
export const GODOT_OPEN_MCP_PLUGIN_PATH =
  "res://addons/godot_open_mcp/plugin.cfg";

/** Absolute path to a project's `project.godot` manifest. */
export function projectGodotPath(projectRoot: string): string {
  return path.join(projectRoot, "project.godot");
}

/** True when `<projectRoot>/project.godot` exists. */
export function isGodotProjectRoot(projectRoot: string): boolean {
  return fs.existsSync(projectGodotPath(projectRoot));
}

/**
 * Parse the `enabled=PackedStringArray("a", "b")` entry inside the
 * `[editor_plugins]` section of a `project.godot` body, returning the ordered
 * list of enabled plugin.cfg paths. Returns an empty array when the section or
 * the `enabled` key is absent.
 *
 * Pure — operates on the file text only. Exported for unit tests.
 */
export function parseEnabledPlugins(projectGodotText: string): string[] {
  const section = extractEditorPluginsSection(projectGodotText);
  if (section === null) return [];

  const match = section.match(/enabled\s*=\s*PackedStringArray\(([^)]*)\)/);
  if (!match) return [];

  const inner = match[1];
  const paths: string[] = [];
  // Match each double-quoted string literal in the array.
  const re = /"([^"]*)"/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(inner)) !== null) {
    paths.push(m[1]);
  }
  return paths;
}

/**
 * Extract the raw text of the `[editor_plugins]` section (everything between
 * its header and the next `[section]` header or EOF). Returns null when the
 * section is absent.
 */
function extractEditorPluginsSection(text: string): string | null {
  const lines = text.split("\n");
  const headerIdx = lines.findIndex((l) => l.trim() === "[editor_plugins]");
  if (headerIdx === -1) return null;

  let endIdx = headerIdx + 1;
  while (endIdx < lines.length && !lines[endIdx].trim().startsWith("[")) {
    endIdx++;
  }
  return lines.slice(headerIdx + 1, endIdx).join("\n");
}

/** Render a `PackedStringArray` literal from a list of plugin paths. */
function renderEnabledLine(paths: string[]): string {
  const quoted = paths.map((p) => `"${p}"`).join(", ");
  return `enabled=PackedStringArray(${quoted})`;
}

export type PluginToggleResult =
  | { kind: "changed"; text: string; enabled: string[] }
  | { kind: "unchanged"; text: string; enabled: string[] };

/**
 * Return a new `project.godot` body with `pluginPath` added to (enable) or
 * removed from (disable) the `[editor_plugins]` `enabled` array.
 *
 * - Creates the `[editor_plugins]` section / `enabled` line when enabling and
 *   they are absent.
 * - Idempotent: enabling an already-enabled plugin (or disabling an
 *   already-disabled one) returns `{ kind: "unchanged" }` with the original
 *   text untouched.
 *
 * Pure — returns the new text; never touches the filesystem. Exported for unit
 * tests.
 */
export function togglePluginInText(
  projectGodotText: string,
  pluginPath: string,
  enable: boolean,
): PluginToggleResult {
  const current = parseEnabledPlugins(projectGodotText);
  const has = current.includes(pluginPath);

  if (enable && has) {
    return { kind: "unchanged", text: projectGodotText, enabled: current };
  }
  if (!enable && !has) {
    return { kind: "unchanged", text: projectGodotText, enabled: current };
  }

  const next = enable
    ? [...current, pluginPath]
    : current.filter((p) => p !== pluginPath);

  const newText = writeEnabledArray(projectGodotText, next);
  return { kind: "changed", text: newText, enabled: next };
}

/**
 * Write the `enabled=PackedStringArray(...)` line into the `[editor_plugins]`
 * section, creating the section/line if needed. The Godot `[editor_plugins]`
 * section is conventionally written with a blank line between the header and
 * the `enabled` line; this preserves that shape on creation.
 */
function writeEnabledArray(text: string, paths: string[]): string {
  const lines = text.split("\n");
  const headerIdx = lines.findIndex((l) => l.trim() === "[editor_plugins]");
  const enabledLine = renderEnabledLine(paths);

  if (headerIdx === -1) {
    // Append a fresh [editor_plugins] section.
    const prefix = text.length > 0 && !text.endsWith("\n") ? "\n" : "";
    const sep = text.trim().length > 0 ? "\n" : "";
    return `${text}${prefix}${sep}[editor_plugins]\n\n${enabledLine}\n`;
  }

  // Find an existing `enabled` line within the section.
  let endIdx = headerIdx + 1;
  while (endIdx < lines.length && !lines[endIdx].trim().startsWith("[")) {
    endIdx++;
  }
  const enabledIdx = lines.findIndex(
    (l, i) => i > headerIdx && i < endIdx && /^\s*enabled\s*=/.test(l),
  );

  if (enabledIdx >= 0) {
    lines[enabledIdx] = enabledLine;
  } else {
    // Insert after the header (and a following blank line if present).
    const insertAt =
      headerIdx + 1 < lines.length && lines[headerIdx + 1].trim() === ""
        ? headerIdx + 2
        : headerIdx + 1;
    lines.splice(insertAt, 0, enabledLine);
  }

  return lines.join("\n");
}
