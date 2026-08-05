// Project-specific agent skill generator (P15.5).
//
// Reads project state from disk (`project.godot` — Godot version, enabled
// plugins, autoloads; the rule/fix catalog — available verify rules; a scan of
// `class_name` declarations + `@tool` scripts in `.gd` and `.cs` files), and
// emits a project-specific inventory section that is MERGED with the canonical
// playbook (`skills/godot-open-mcp/SKILL.md`). The canonical playbook stays
// hand-authored (operational rules, fast-start, tool groups); this generator
// appends a `# Project inventory — <name>` section so a single write produces
// one file carrying the full playbook AND this project's actual surface.
//
// Three layers (mirrors Unity generate-skill's layering):
//  1. readProjectState() — disk I/O (project.godot, type scan).
//  2. generateSkillMarkdown() / composeSkillMarkdown() — pure string builders.
//  3. writeSkillToClients() — writes the generated file via client-paths.json.
//
// Adapted from Unity Open MCP's `mcp-server/src/skill/generate-skill.ts`
// (adapt for the project-state-reading + skill-section-composition +
// `clients[]` write logic). Intentional deltas:
//   - Godot project state comes from `project.godot` (INI), not Unity's
//     `ProjectVersion.txt` + `Packages/manifest.json`. The bridge/verify
//     "installed" signal is the enabled plugin entry in `[editor_plugins]`,
//     not a package dependency.
//   - Type scanning is Godot-native: `class_name X` declarations in `.gd` and
//     `: Node` / `: Resource` base classes in `.cs`. Unity scanned
//     MonoBehaviour / ScriptableObject subclasses; those concepts do not apply.
//   - MERGE-with-playbook model (Unity replaces; Godot appends a project-
//     inventory section after a `---` separator). The canonical playbook is the
//     single source of truth for agent guidance and is never overwritten.

import { readFile, writeFile, readdir, stat, mkdir } from "node:fs/promises";
import { join, basename, relative, sep } from "node:path";
import type { CapabilitiesResult } from "../capabilities/build-capabilities.js";
import { clientSkillRelativePath, resolveTemplateSkillPath } from "./client-paths.js";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** One discovered project type (a `class_name` declaration or a C# Godot type). */
export interface TypeEntry {
  /** Declared type name (e.g. `PlayerController`). */
  name: string;
  /** Coarse class: `gdscript` | `csharp`. */
  kind: "gdscript" | "csharp";
  /** `tool` when the script is a `@tool` editor script, else `script`. */
  role: "tool" | "script";
  /** Project-relative source path (forward slashes). */
  filePath: string;
}

/** One enabled editor plugin from `[editor_plugins]`. */
export interface PluginEntry {
  /** Plugin id as written in project.godot, e.g. `res://addons/foo/plugin.cfg`. */
  path: string;
  /** `enabled` when the plugin is enabled (`true`), else `disabled`. */
  enabled: boolean;
}

/** Snapshot of the project state the generator reads from disk. */
export interface ProjectState {
  /** `config/name` from `[application]`, or the directory basename as a fallback. */
  projectName: string;
  /** Godot editor version (`config/features` `4.x` entry, or `unknown`). */
  godotVersion: string;
  /** Features from `[application] config/features` (e.g. `Forward+`, `GL Compatibility`). */
  features: string[];
  /** Enabled + disabled editor plugins from `[editor_plugins]`. */
  plugins: PluginEntry[];
  /** Autoload singletons from `[autoload]` (name → res:// path). */
  autoloads: { name: string; path: string }[];
  /** `true` when the Godot Open MCP bridge addon is enabled. */
  bridgeInstalled: boolean;
  /** `true` when the verify addon is enabled (when shipped as a plugin). */
  verifyInstalled: boolean;
  /** Discovered project types (capped; sorted by name). */
  types: TypeEntry[];
}

/** Result of writing the skill to one client dir. */
export interface SkillWriteTarget {
  client: string;
  /** Project-relative path (e.g. `.claude/skills/godot-open-mcp/SKILL.md`). */
  relativePath: string;
  /** Absolute path on disk. */
  absolutePath: string;
  /** Always `true` for a successful write; `false` for a skipped client. */
  written: boolean;
  /** `true` when a file already existed at that path before this write. */
  existed: boolean;
}

export interface GenerateSkillResult {
  /** Full generated skill markdown (playbook + inventory, or standalone). */
  skill: string;
  /** The project state the skill was composed from. */
  project: ProjectState;
  /** One entry per write target (empty when `write` was false). */
  written: SkillWriteTarget[];
  /** `true` when the template playbook was merged into the output. */
  mergedWithTemplate: boolean;
}

export interface GenerateSkillOptions {
  /** When `true`, write the skill to the client dirs. Default `false` (preview). */
  write?: boolean;
  /** Client keys to write to (only when `write:true`). Defaults to `["claude"]`. */
  clients?: string[];
  /**
   * When `true` (default), compose the canonical playbook template with the
   * project inventory. When `false` (or when the template is missing), emit
   * only the standalone inventory.
   */
  includeWorkflow?: boolean;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Addon ids the generator recognizes for the bridge/verify "installed" signal. */
const BRIDGE_PLUGIN_HINT = "godot_open_mcp";
const VERIFY_PLUGIN_HINT = "godot_open_mcp_verify";

/**
 * Directories never descended into during the type scan. Mirrors Godot's own
 * ignore conventions plus the version-control / build / MCP-client dirs that
 * live inside a project but carry no project scripts.
 */
const SKIP_DIRS = new Set([
  ".git",
  ".godot", // Godot's cache + import output — no source scripts.
  ".vs",
  ".vscode",
  ".cursor",
  ".claude",
  ".opencode",
  ".agents",
  ".cline",
  ".kilocode",
  ".gemini",
  "node_modules",
  "obj",
  "Obj",
  "Bin",
  ".idea",
]);

/** Hard cap on the number of files scanned (pathological-project guard). */
const MAX_FILES_SCAN = 2000;

/** Max depth for the recursive walk (guards against pathological nesting). */
const MAX_WALK_DEPTH = 10;

/** Cap on the number of types surfaced in the skill (keeps the file lean). */
const MAX_TYPES_IN_SKILL = 40;

/**
 * Cap the inventory preview embedded in the tool response so the JSON envelope
 * stays bounded. The full skill is always written to disk when `write:true`;
 * this only bounds the echoed copy.
 */
const MAX_INVENTORY_PREVIEW_CHARS = 6000;

// ---------------------------------------------------------------------------
// 1. Project-state reader (disk I/O)
// ---------------------------------------------------------------------------

/**
 * Read the project state the generator needs. Never throws — a missing or
 * unreadable `project.godot` degrades to a `godotVersion: "unknown"` /
 * empty-plugins state so a preview call still works on a half-set-up project.
 */
export async function readProjectState(
  projectRoot: string,
): Promise<ProjectState> {
  const dirName = basename(projectRoot);
  const godot = await readProjectGodot(projectRoot);

  const plugins = godot.plugins;
  const bridgeInstalled = plugins.some(
    (p) => p.enabled && p.path.includes(BRIDGE_PLUGIN_HINT),
  );
  const verifyInstalled = plugins.some(
    (p) => p.enabled && p.path.includes(VERIFY_PLUGIN_HINT),
  );

  const types = await scanProjectTypes(projectRoot);

  return {
    projectName: godot.name ?? dirName,
    godotVersion: godot.version,
    features: godot.features,
    plugins,
    autoloads: godot.autoloads,
    bridgeInstalled,
    verifyInstalled,
    types,
  };
}

interface ParsedProjectGodot {
  name: string | null;
  version: string;
  features: string[];
  plugins: PluginEntry[];
  autoloads: { name: string; path: string }[];
}

async function readProjectGodot(
  projectRoot: string,
): Promise<ParsedProjectGodot> {
  const out: ParsedProjectGodot = {
    name: null,
    version: "unknown",
    features: [],
    plugins: [],
    autoloads: [],
  };
  let text: string;
  try {
    text = await readFile(join(projectRoot, "project.godot"), "utf-8");
  } catch {
    return out;
  }
  return parseProjectGodot(text, out);
}

/**
 * Parse `project.godot` (Godot INI). Lenient — unknown sections and malformed
 * optional values are skipped, never fatal. Reads only the few fields the
 * inventory surfaces:
 *   - `[application]` `config/name`, `config/features`
 *   - `[editor_plugins]` `enabled` (PackedStringArray of `res://.../plugin.cfg`)
 *   - `[autoload]` (name="*path" or name="*NodeName" entries)
 */
function parseProjectGodot(
  text: string,
  fallback: ParsedProjectGodot,
): ParsedProjectGodot {
  let section = "";
  const enabledPlugins: string[] = [];
  const disabledPlugins: string[] = [];
  const autoloads: { name: string; path: string }[] = [];
  let name: string | null = null;
  let featuresRaw: string | null = null;

  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line === "" || line.startsWith(";") || line.startsWith("#")) continue;

    const sectionMatch = /^\[(.+)\]$/.exec(line);
    if (sectionMatch) {
      section = sectionMatch[1].trim();
      continue;
    }

    const eqIdx = line.indexOf("=");
    if (eqIdx <= 0) continue;
    const key = line.slice(0, eqIdx).trim();
    const value = line.slice(eqIdx + 1).trim();

    if (section === "application") {
      if (key === "config/name" && name === null) {
        name = unquote(value);
      } else if (key === "config/features" && featuresRaw === null) {
        featuresRaw = value;
      }
    } else if (section === "editor_plugins") {
      if (key === "enabled") {
        for (const p of parsePackedStringArray(value)) enabledPlugins.push(p);
      } else if (key === "disabled") {
        for (const p of parsePackedStringArray(value)) disabledPlugins.push(p);
      }
    } else if (section === "autoload") {
      // Autoload lines look like `Player="*res://player.gd"` (the leading `*`
      // marks a singleton; the path follows). The key is the autoload name.
      autoloads.push({ name: key, path: unquote(value).replace(/^\*/, "") });
    }
  }

  const features = featuresRaw ? parsePackedStringArray(featuresRaw) : [];
  // The Godot version is the `4.x` feature entry (Godot writes
  // `PackedStringArray("4.3", "Forward Plus", ...)`). Find the leading-numeric
  // entry; fall back to `unknown`.
  let version = "unknown";
  for (const f of features) {
    if (/^\d+\.\d+/.test(f)) {
      version = f;
      break;
    }
  }

  const plugins: PluginEntry[] = [
    ...enabledPlugins.map((path) => ({ path, enabled: true })),
    ...disabledPlugins.map((path) => ({ path, enabled: false })),
  ];

  return {
    name,
    version,
    features,
    plugins,
    autoloads,
  };
}

/** Strip surrounding double quotes (Godot quotes string values). */
function unquote(value: string): string {
  if (value.length >= 2 && value.startsWith('"') && value.endsWith('"')) {
    return value.slice(1, -1);
  }
  return value;
}

/**
 * Parse a Godot `PackedStringArray("a", "b")` value into a list of strings.
 * Handles commas inside quoted elements. Falls back to a lenient comma-split
 * for a bare list (e.g. `Double Precision, GL Compatibility`).
 */
function parsePackedStringArray(raw: string): string[] {
  const packedMatch = /^PackedStringArray\((.*)\)$/s.exec(raw);
  if (packedMatch) {
    const inner = packedMatch[1];
    const out: string[] = [];
    const re = /"((?:[^"\\]|\\.)*)"/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(inner)) !== null) {
      const unescaped = m[1].replace(/\\"/g, '"').replace(/\\\\/g, "\\");
      const trimmed = unescaped.trim();
      if (trimmed !== "") out.push(trimmed);
    }
    return out;
  }
  return raw
    .split(",")
    .map((f) => unquote(f.trim()))
    .filter((f) => f !== "");
}

// ---------------------------------------------------------------------------
// Type scanning (Godot `class_name` + `@tool` + Node/Resource subclasses)
// ---------------------------------------------------------------------------

async function scanProjectTypes(projectRoot: string): Promise<TypeEntry[]> {
  const files: string[] = [];
  await collectScriptFiles(projectRoot, files, 0);

  const types: TypeEntry[] = [];
  for (const filePath of files) {
    if (types.length >= MAX_TYPES_IN_SKILL * 4) break; // bound work before sort+slice
    try {
      const content = await readFile(filePath, "utf-8");
      extractTypes(content, filePath, projectRoot, types);
    } catch {
      // Skip unreadable files.
    }
  }

  types.sort((a, b) => {
    const byName = a.name.localeCompare(b.name);
    if (byName !== 0) return byName;
    return a.filePath.localeCompare(b.filePath);
  });
  return types.slice(0, MAX_TYPES_IN_SKILL);
}

async function collectScriptFiles(
  dir: string,
  results: string[],
  depth: number,
): Promise<void> {
  if (depth > MAX_WALK_DEPTH || results.length > MAX_FILES_SCAN) return;
  let entries: import("node:fs").Dirent[];
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    if (entry.name.startsWith(".") && SKIP_DIRS.has(entry.name)) continue;
    if (SKIP_DIRS.has(entry.name)) continue;
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      await collectScriptFiles(fullPath, results, depth + 1);
    } else if (
      entry.name.endsWith(".gd") ||
      entry.name.endsWith(".cs")
    ) {
      results.push(fullPath);
    }
  }
}

/**
 * `class_name X` GDScript declaration. Captures the name; the role (`tool` vs
 * `script`) is determined by an earlier `@tool` annotation in the same file.
 */
const GD_CLASS_RE = /^[ \t]*class_name[ \t]+(\w+)/m;
/** `@tool` editor-script annotation (GDScript). */
const GD_TOOL_RE = /^[ \t]*@tool\b/m;
/**
 * C# class deriving from a Godot Node or Resource subclass. Godot 4 ships
 * `Godot.Node` / `Godot.Resource`; the import may be `using Godot;` so we match
 * both the qualified and unqualified forms.
 */
const CS_CLASS_RE =
  /(?:public|internal|abstract|sealed|\s)+class\s+(\w+)\s*(?::\s*([A-Za-z_][\w.]*))?/g;

function extractTypes(
  content: string,
  filePath: string,
  projectRoot: string,
  out: TypeEntry[],
): void {
  const relPath = relative(projectRoot, filePath).split(sep).join("/");

  if (filePath.endsWith(".gd")) {
    const tool = GD_TOOL_RE.test(content);
    const m = GD_CLASS_RE.exec(content);
    if (m) {
      out.push({
        name: m[1],
        kind: "gdscript",
        role: tool ? "tool" : "script",
        filePath: relPath,
      });
    }
    return;
  }

  // .cs — surface a class only when it derives from a Godot type. A loose
  // `Godot.` prefix is the strongest signal; an unqualified `Node` / `Resource`
  // base also counts (the file presumably has `using Godot;`).
  for (const match of content.matchAll(CS_CLASS_RE)) {
    const className = match[1];
    const base = match[2];
    if (!base) continue;
    const isGodotType = isGodotBaseType(base);
    if (!isGodotType) continue;
    out.push({
      name: className,
      kind: "csharp",
      role: "script",
      filePath: relPath,
    });
  }
}

/** Recognize a Godot Node / Resource subclass by its base-name tail. */
function isGodotBaseType(base: string): boolean {
  const tail = base.split(".").pop() ?? base;
  // Node and Resource are the two Godot roots the generator cares about. A
  // user type deriving from e.g. `CharacterBody2D` (a Node) still has `Node`
  // transitively up the chain, but the generator scans the direct base only —
  // surfacing every Godot subclass would be too noisy. Keep this list lean.
  const recognized = new Set([
    "Node",
    "Resource",
    "Node2D",
    "Node3D",
    "Control",
    "CharacterBody2D",
    "CharacterBody3D",
    "RigidBody2D",
    "RigidBody3D",
    "AnimatableBody2D",
    "AnimatableBody3D",
    "StaticBody2D",
    "StaticBody3D",
    "Camera2D",
    "Camera3D",
  ]);
  return recognized.has(tail);
}

// ---------------------------------------------------------------------------
// 2. Skill markdown generator (pure, no I/O)
// ---------------------------------------------------------------------------

/**
 * Project-specific inventory blocks the template cannot know: Godot version,
 * bridge/verify install state, enabled plugins, autoloads, and the key types
 * discovered in the project. Used by both the standalone full builder and the
 * template+inventory composer so the two never drift on the project-section
 * shape.
 */
function buildProjectInventoryBlocks(state: ProjectState): string[] {
  const lines: string[] = [];

  // --- Project environment ---
  lines.push("## Project environment");
  lines.push("");
  lines.push(`- **Godot version:** ${state.godotVersion}`);
  if (state.features.length > 0) {
    lines.push(`- **Renderer features:** ${state.features.join(", ")}`);
  }
  lines.push(
    `- **Bridge addon:** ${state.bridgeInstalled ? "enabled" : "not enabled"}`,
  );
  lines.push(
    `- **Verify addon:** ${state.verifyInstalled ? "enabled" : "not enabled"}`,
  );
  if (state.autoloads.length > 0) {
    lines.push(
      `- **Autoloads:** ${state.autoloads.map((a) => `\`${a.name}\``).join(", ")}`,
    );
  }
  lines.push("");

  // --- Enabled plugins ---
  const enabled = state.plugins.filter((p) => p.enabled);
  const disabled = state.plugins.filter((p) => !p.enabled);
  if (enabled.length > 0 || disabled.length > 0) {
    lines.push("### Editor plugins");
    lines.push("");
    lines.push("| Plugin | State |");
    lines.push("|---|---|");
    for (const p of enabled) {
      lines.push(`| \`${shortPluginPath(p.path)}\` | enabled |`);
    }
    for (const p of disabled) {
      lines.push(`| \`${shortPluginPath(p.path)}\` | disabled |`);
    }
    lines.push("");
  }

  // --- Key project types ---
  if (state.types.length > 0) {
    lines.push("## Key project types");
    lines.push("");
    lines.push(
      "Use `godot_open_mcp_node_find` / `godot_open_mcp_resource_find` to " +
        "inspect any of these before editing. `@tool` scripts run in the " +
        "editor and mutate live editor state on load — be explicit in " +
        "`paths_hint` when mutating one.",
    );
    lines.push("");

    const gd = state.types.filter((t) => t.kind === "gdscript");
    const cs = state.types.filter((t) => t.kind === "csharp");
    if (gd.length > 0) {
      lines.push("### GDScript (`class_name`)");
      lines.push("");
      for (const t of gd) {
        const tag = t.role === "tool" ? " `@tool`" : "";
        lines.push(`- **${t.name}**${tag} — \`${t.filePath}\``);
      }
      lines.push("");
    }
    if (cs.length > 0) {
      lines.push("### C# (Godot Node / Resource subclasses)");
      lines.push("");
      for (const t of cs) {
        lines.push(`- **${t.name}** — \`${t.filePath}\``);
      }
      lines.push("");
    }

    if (state.types.length === MAX_TYPES_IN_SKILL) {
      lines.push(
        `_Showing the first ${MAX_TYPES_IN_SKILL}; more types exist in the project._`,
      );
      lines.push("");
    }
  }

  return lines;
}

/** Reduce a plugin path like `res://addons/godot_open_mcp/plugin.cfg` to `godot_open_mcp`. */
function shortPluginPath(path: string): string {
  const m = /addons\/([^/]+)/.exec(path);
  return m ? m[1] : path;
}

/**
 * Build the standalone full skill markdown (template + inventory merged, or —
 * when the template is missing / `includeWorkflow:false` — a self-contained
 * inventory with a short workflow summary). Pure: no I/O.
 */
export function generateSkillMarkdown(
  state: ProjectState,
  caps: CapabilitiesResult,
): string {
  const lines: string[] = [];
  const now = new Date().toISOString().slice(0, 10);

  lines.push(`# Godot Open MCP — agent skill (${state.projectName})`);
  lines.push("");
  lines.push(`> Auto-generated by \`godot_open_mcp_generate_skill\` on ${now}.`);
  lines.push(
    "> Regenerate after plugin or script changes to keep this file current.",
  );
  lines.push("");

  lines.push(...buildProjectInventoryBlocks(state));
  appendCapabilityBlocks(lines, caps);
  appendWorkflowSummary(lines);

  lines.push("---");
  lines.push("");
  lines.push(
    "This file was auto-generated. To refresh, call " +
      "`godot_open_mcp_generate_skill` with `\"write\": true`.",
  );
  return lines.join("\n") + "\n";
}

/**
 * Compose the final skill markdown from the template playbook + the project
 * inventory (the merge path). When `template` is non-null and
 * `includeWorkflow !== false`, the template is emitted verbatim followed by a
 * `---` separator and a `# Project inventory — <name>` section. When the
 * template is null or `includeWorkflow:false`, falls back to
 * {@link generateSkillMarkdown}.
 */
export function composeSkillMarkdown(
  state: ProjectState,
  caps: CapabilitiesResult,
  template: string | null,
  options: { includeWorkflow?: boolean } = {},
): string {
  const includeWorkflow = options.includeWorkflow !== false;
  if (!template || !includeWorkflow) {
    return generateSkillMarkdown(state, caps);
  }

  const lines: string[] = [];
  // Template playbook, verbatim — the authoritative workflow prose.
  lines.push(template.endsWith("\n") ? template : `${template}\n`);
  lines.push("---");
  lines.push("");
  // Project inventory section.
  lines.push(`# Project inventory — ${state.projectName}`);
  lines.push("");
  lines.push(
    "> Project-specific section generated by `godot_open_mcp_generate_skill`. " +
      "Regenerate after plugin or script changes.",
  );
  lines.push("");
  lines.push(...buildProjectInventoryBlocks(state));
  appendCapabilityBlocks(lines, caps);

  return lines.join("\n") + "\n";
}

/**
 * Append the available-tools / verify-rules / available-fixes blocks built
 * from the capability surface. Shared by both builders so the catalog section
 * is identical whether or not the template playbook was merged.
 */
function appendCapabilityBlocks(
  lines: string[],
  caps: CapabilitiesResult,
): void {
  // --- Available tools ---
  const implementedTools = caps.tools.filter((t) => t.implemented);
  if (implementedTools.length > 0) {
    lines.push("## Available tools");
    lines.push("");
    lines.push(
      "All tools are prefixed `godot_open_mcp_*`. Discover the live surface " +
        "via `godot_open_mcp_capabilities`.",
    );
    lines.push("");
    for (const tool of implementedTools) {
      lines.push(`- \`${tool.name}\` — ${firstSentence(tool.description)}`);
    }
    lines.push("");
  }

  // --- Verify rules ---
  const implementedRules = caps.rules.filter((r) => r.implemented);
  if (implementedRules.length > 0) {
    lines.push("## Verify rules (gate)");
    lines.push("");
    for (const rule of implementedRules) {
      lines.push(`### ${rule.title}`);
      lines.push("");
      lines.push(rule.description);
      lines.push("");
      if (rule.issues.length > 0) {
        lines.push("| Issue code | Severity | Auto-fix |");
        lines.push("|---|---|---|");
        for (const issue of rule.issues) {
          const fixLabel =
            issue.fixIds.length > 0 ? issue.fixIds.join(", ") : "—";
          lines.push(`| ${issue.code} | ${issue.severity} | ${fixLabel} |`);
        }
        lines.push("");
      }
    }
  }

  // --- Available fixes ---
  const implementedFixes = caps.fixes.filter((f) => f.implemented);
  if (implementedFixes.length > 0) {
    lines.push("## Available fixes");
    lines.push("");
    for (const fix of implementedFixes) {
      lines.push(
        `- \`${fix.id}\` — safe: ${fix.safe}, resolves: ${fix.issueCodes.join(", ")}`,
      );
    }
    lines.push("");
  }
}

/** Short workflow summary appended to the standalone builder only. */
function appendWorkflowSummary(lines: string[]): void {
  lines.push("## Core workflow: mutate → gate → fix");
  lines.push("");
  lines.push(
    "1. **Discover** — call `godot_open_mcp_capabilities` to confirm which " +
      "tools and rules are available.",
  );
  lines.push(
    "2. **Activate** — `godot_open_mcp_manage_tools` with " +
      "`action: \"activate\", group: \"typed-editor\"` before any typed tool.",
  );
  lines.push(
    "3. **Mutate** — call the typed tool with full `paths_hint` and default " +
      "`gate: \"enforce\"`.",
  );
  lines.push(
    "4. **Read the gate** — on `isError: true`, inspect `gate.delta` and " +
      "`agentNextSteps`.",
  );
  lines.push(
    "5. **Fix** — address the top error; use `godot_open_mcp_apply_fix` with " +
      "`dry_run: true` first when a fix is available.",
  );
  lines.push(
    "6. **Retry** — re-run the mutation; confirm `newErrors == 0`.",
  );
  lines.push("");
  lines.push(
    "**Principle: mutation success ≠ project safe.** A clean compile can still " +
      "break scene references.",
  );
  lines.push("");
}

function firstSentence(text: string): string {
  if (!text) return "";
  const period = text.indexOf(". ");
  if (period > 0 && period < 120) return text.slice(0, period + 1);
  return text.split("\n")[0] ?? text;
}

// ---------------------------------------------------------------------------
// 3. File writer (disk I/O)
// ---------------------------------------------------------------------------

/**
 * Write the generated skill to one or more client skill dirs. Per-client
 * failures are isolated: an unknown client key skips that write rather than
 * aborting the whole call. Paths come from `client-paths.json` via
 * `clientSkillRelativePath` — never hardcoded here.
 */
export async function writeSkillToClients(
  projectRoot: string,
  content: string,
  clients: string[],
): Promise<SkillWriteTarget[]> {
  const results: SkillWriteTarget[] = [];
  for (const client of clients) {
    let rel: string;
    try {
      rel = clientSkillRelativePath(client);
    } catch {
      // Unknown client key — skip rather than abort the whole write.
      continue;
    }
    const abs = join(projectRoot, ...rel.split("/"));
    let existed = false;
    try {
      await stat(abs);
      existed = true;
    } catch {
      // File does not exist yet — that is the normal first-write path.
    }
    await ensureParentDir(abs);
    await writeFile(abs, content, "utf-8");
    results.push({
      client,
      relativePath: rel,
      absolutePath: abs,
      written: true,
      existed,
    });
  }
  return results;
}

async function ensureParentDir(filePath: string): Promise<void> {
  const lastSep = filePath.lastIndexOf(sep);
  if (lastSep > 0) {
    await mkdir(filePath.slice(0, lastSep), { recursive: true });
  }
}

// ---------------------------------------------------------------------------
// Template reader + orchestrator
// ---------------------------------------------------------------------------

/**
 * Read the canonical playbook template from disk. Returns the template text
 * (normalized to end with a single trailing newline), or `null` when the
 * template cannot be located or read — the composer then degrades to the
 * standalone full-inventory output. Never throws.
 */
export async function readTemplateWorkflow(): Promise<string | null> {
  const templatePath = resolveTemplateSkillPath();
  if (!templatePath) return null;
  try {
    const raw = await readFile(templatePath, "utf-8");
    return raw.endsWith("\n") ? raw : `${raw}\n`;
  } catch {
    return null;
  }
}

/**
 * Generate a project-specific skill.
 *
 * Pipeline:
 *  1. Read the project state from disk.
 *  2. Read the template playbook (when `includeWorkflow` is not false).
 *  3. Compose the skill markdown (merge when the template was found).
 *  4. Optionally write the file to one or more client skill dirs.
 *
 * Never throws — the worst case (no project.godot, no template) still produces
 * a standalone inventory from a degraded state, so a preview call is always
 * useful.
 */
export async function generateSkill(
  projectRoot: string,
  caps: CapabilitiesResult,
  options: GenerateSkillOptions = {},
): Promise<GenerateSkillResult> {
  const state = await readProjectState(projectRoot);
  const includeWorkflow = options.includeWorkflow !== false;
  const template = includeWorkflow ? await readTemplateWorkflow() : null;
  const skill = composeSkillMarkdown(state, caps, template, {
    includeWorkflow,
  });

  let written: SkillWriteTarget[] = [];
  if (options.write) {
    const clients =
      options.clients && options.clients.length > 0
        ? options.clients
        : ["claude"];
    written = await writeSkillToClients(projectRoot, skill, clients);
  }

  return {
    skill,
    project: state,
    written,
    mergedWithTemplate: includeWorkflow && template !== null,
  };
}

/**
 * Truncate a skill preview for embedding in a JSON envelope (the tool
 * response's echoed copy). The full skill is always written to disk when
 * `write:true`; this only bounds the in-response copy.
 */
export function truncateForPreview(skill: string): string {
  if (skill.length <= MAX_INVENTORY_PREVIEW_CHARS) return skill;
  const cut = skill.slice(0, MAX_INVENTORY_PREVIEW_CHARS);
  const lastNewline = cut.lastIndexOf("\n");
  const head = lastNewline > 0 ? cut.slice(0, lastNewline) : cut;
  return `${head}\n\n… (${skill.length - head.length} more chars; full content written to disk)`;
}

// ---------------------------------------------------------------------------
// Re-exports for the tool definition / router.
// ---------------------------------------------------------------------------

export { knownClientKeys } from "./client-paths.js";
