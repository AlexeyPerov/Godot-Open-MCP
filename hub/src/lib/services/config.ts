/**
 * Tauri IPC wrappers for the Hub backend.
 *
 * Thin typed wrappers over `invoke(...)`. Command names + argument names
 * mirror the `#[tauri::command]` functions in `src-tauri/src/lib.rs`
 * exactly. This layer only moves bytes between the UI and Rust; all the
 * install / setup-mcp / launch behavior lives in the Rust `config`
 * modules (which mirror the `godot-open-mcp-cli` contracts).
 */

import { invoke } from "@tauri-apps/api/core";
import { revealItemInDir } from "@tauri-apps/plugin-opener";

// ── Persistence + inventory (P11.1) ──────────────────────────────────────────

export interface Settings {
  /** Explicit Godot editor binary path (overrides discovery). */
  godotEditorPath: string | null;
  /** Preferred MCP source: published npx package vs local monorepo checkout. */
  mcpSource: "npx-published" | "local-checkout";
  /** Preferred MCP client id (e.g. "cursor"). */
  defaultClientId: string | null;
}

export interface ProjectEntry {
  id: string;
  path: string;
  name: string;
  addedAt: string;
  lastOpenedAt: string | null;
}

export interface ProjectsFile {
  projects: ProjectEntry[];
}

/** Stable error labels returned by `add_project` (mirror lib.rs). */
export type AddProjectErrorLabel =
  | "not_godot_project"
  | "path_not_found"
  | "duplicate_project"
  | "invalid_path";

export interface AddProjectResult {
  ok: boolean;
  errorLabel?: AddProjectErrorLabel;
  message?: string;
  project?: ProjectEntry;
}

export function loadSettings(): Promise<Settings> {
  return invoke<Settings>("load_settings");
}

export function saveSettings(settings: Settings): Promise<void> {
  return invoke("save_settings", { settings });
}

export function loadProjects(): Promise<ProjectsFile> {
  return invoke<ProjectsFile>("load_projects");
}

export function addProject(path: string): Promise<AddProjectResult> {
  return invoke<AddProjectResult>("add_project", { path });
}

export function removeProject(id: string): Promise<ProjectsFile> {
  return invoke<ProjectsFile>("remove_project", { id });
}

export function touchProjectOpened(id: string): Promise<ProjectsFile> {
  return invoke<ProjectsFile>("touch_project_opened", { id });
}

/**
 * Resolve the deterministic bridge port for a project (env override >
 * live lock > sha256 hash). Mirrors `instance-discovery.ts` /
 * `InstancePortResolver.cs` byte-for-byte.
 */
export function resolveBridgePort(
  projectPath: string,
  overridePort?: number,
): Promise<number> {
  return invoke<number>("resolve_bridge_port", {
    projectPath,
    overridePort: overridePort ?? null,
  });
}

// ── Project kind detection (P11.3 maintainer gate) ───────────────────────────

export type ProjectKind = "openMcp" | "godotProject" | "custom";

export function detectProjectKind(path: string): Promise<ProjectKind> {
  return invoke<ProjectKind>("detect_project_kind", { path });
}

// ── Reveal in file manager ───────────────────────────────────────────────────

export function revealPath(path: string): Promise<void> {
  return revealItemInDir(path);
}
