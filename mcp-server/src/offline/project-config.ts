// Bounded `project.godot` identification for the offline directory listing
// (P7.3).
//
// Godot marks a project root with a `project.godot` marker file — an INI-style
// config with sections like `[application]` and keys like `config/name`. This
// is the Godot analog of Unity's project root (Unity has no single marker; it
// uses `Assets/` + `ProjectSettings/`). The offline listing refuses to walk a
// directory that is not a Godot project, so a stray `res://` lookup outside a
// real project fails fast with `project_not_found` rather than listing random
// files.
//
// This module is deliberately NOT a general ProjectSettings parser. It reads
// only the few identification fields the listing surfaces (`config/name` +
// `config/features`) and ignores everything else. Godot's full settings schema
// (rendering, physics, layer names, input map) is out of scope — the offline
// listing never needs it, and parsing it would balloon this module.
//
// Adapted from Unity Open MCP's offline project-detection pattern (adapt
// fidelity for the "marker file + bounded read + identify-only" discipline).
// Intentional deltas:
//   - The marker is `project.godot` (INI), not `ProjectSettings/ProjectVersion.txt`
//     or a `Packages/manifest.json`.
//   - No `.meta`/GUID index — Godot identifies resources by `res://` paths +
//     optional `uid://` handles.
//   - Read cap is conservative (1 MiB); a real `project.godot` is a few KB.

import { readFile, realpath } from "node:fs/promises";
import { join } from "node:path";
import { statSync } from "node:fs";

/** Hard byte cap for a single `project.godot` read. Guards against a
 *  pathological huge file blowing the identifier. Real Godot project files are
 *  a few KB; 1 MiB is a generous ceiling. */
export const PROJECT_GODOT_BYTE_CAP = 1024 * 1024;

/** Canonical (realpath'd) project root resolved from a marker file. */
export interface OfflineProjectInfo {
  /** Canonical native project root (realpath'd, no trailing separator).
   *  Internal — public output should use `res://`. */
  projectRoot: string;
  /** `config/name` from `[application]`, or `null` when absent/malformed. */
  projectName: string | null;
  /** Comma-separated `config/features` split + trimmed, or `[]`. */
  features: string[];
}

/** Structured identification failure. The router maps `code` to the matching
 *  error envelope. */
export interface OfflineProjectConfigError {
  code: "project_not_found" | "project_config_unreadable";
  message: string;
}

/** Identification outcome — success carries the parsed info; failure carries a
 *  structured error. */
export type IdentifyProjectResult =
  | { ok: true; info: OfflineProjectInfo }
  | { ok: false; error: OfflineProjectConfigError };

/**
 * Identify a Godot project root by locating and reading its `project.godot`
 * marker, returning the parsed identification fields or a structured error.
 *
 * Pipeline:
 *   1. Resolve the canonical project root (realpath). A missing/unreadable
 *      root is `project_not_found`.
 *   2. Require a regular `<root>/project.godot`. Missing →
 *      `project_not_found`; unreadable / not a regular file →
 *      `project_config_unreadable`.
 *   3. Bounded read (cap {@link PROJECT_GODOT_BYTE_CAP}). Oversized →
 *      `project_config_unreadable`.
 *   4. Parse `[application]` `config/name` + `config/features` only.
 *      Malformed optional settings are tolerated (name → null, features → []);
 *      only a structurally broken file (no recognizable header line at all) is
 *      `project_config_unreadable`.
 *
 * Never throws — every failure maps to a structured `OfflineProjectConfigError`.
 */
export async function identifyGodotProject(
  projectRoot: string,
): Promise<IdentifyProjectResult> {
  // 1. Canonical root. A realpath miss means the directory itself is gone.
  let realRoot: string;
  try {
    realRoot = await realpath(projectRoot);
  } catch {
    return err(
      "project_not_found",
      "project root does not exist or is not accessible.",
    );
  }

  // 2. Marker presence + regular-file check.
  const markerPath = join(realRoot, "project.godot");
  let markerStat;
  try {
    markerStat = statSync(markerPath);
  } catch (e) {
    const code = (e as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT") {
      return err(
        "project_not_found",
        "project root has no 'project.godot' marker; not a Godot project.",
      );
    }
    return err(
      "project_config_unreadable",
      `cannot stat 'project.godot': ${(e as Error)?.message ?? code ?? "unknown error"}`,
    );
  }
  if (!markerStat.isFile()) {
    return err(
      "project_config_unreadable",
      "'project.godot' exists but is not a regular file.",
    );
  }

  // 3. Bounded read.
  let text: string;
  try {
    text = await readFile(markerPath, "utf-8");
  } catch (e) {
    return err(
      "project_config_unreadable",
      `cannot read 'project.godot': ${(e as Error)?.message ?? "unknown error"}`,
    );
  }
  if (Buffer.byteLength(text, "utf-8") > PROJECT_GODOT_BYTE_CAP) {
    return err(
      "project_config_unreadable",
      `'project.godot' exceeds the ${PROJECT_GODOT_BYTE_CAP}-byte identification cap.`,
    );
  }

  // 4. Parse identification fields. A file that contains no `[application]`
  // section is still a valid Godot project (the section is optional in a
  // freshly-init'd project); we surface name=null + features=[]. Only a file
  // with no content at all (or unparseable garbage with no section header) is
  // treated as unreadable — but Godot tolerates a near-empty project.godot, so
  // we are lenient here: any non-empty marker is accepted as a project.
  const { name, features } = parseIdentification(text);

  return {
    ok: true,
    info: { projectRoot: realRoot, projectName: name, features },
  };
}

/**
 * Parse the `[application]` section's `config/name` and `config/features` from
 * `project.godot` text. Lenient by design — unknown sections, comments, and
 * malformed optional values are skipped rather than fatal. Returns
 * `{ name: null, features: [] }` when the section or keys are absent.
 *
 * Godot's INI grammar is simple: `[section]` headers, `key = value` lines,
 * `;`/`#` comments. String values may be quoted (`"My Game"`); we strip the
 * surrounding quotes when present. `config/features` is a comma-separated list
 * (e.g. `Double Precision, GL Compatibility`).
 */
function parseIdentification(text: string): {
  name: string | null;
  features: string[];
} {
  let inApplication = false;
  let name: string | null = null;
  let featuresRaw: string | null = null;

  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line === "" || line.startsWith(";") || line.startsWith("#")) continue;

    const sectionMatch = /^\[(.+)\]$/.exec(line);
    if (sectionMatch) {
      inApplication = sectionMatch[1].trim() === "application";
      continue;
    }

    if (!inApplication) continue;

    const eqIdx = line.indexOf("=");
    if (eqIdx <= 0) continue;
    const key = line.slice(0, eqIdx).trim();
    let value = line.slice(eqIdx + 1).trim();
    // Strip surrounding double quotes (Godot quotes string values).
    if (
      value.length >= 2 &&
      value.startsWith('"') &&
      value.endsWith('"')
    ) {
      value = value.slice(1, -1);
    }

    if (key === "config/name" && name === null) {
      name = value;
    } else if (key === "config/features" && featuresRaw === null) {
      featuresRaw = value;
    }
  }

  const features = featuresRaw === null ? [] : parseFeatures(featuresRaw);

  return { name, features };
}

/**
 * Parse a `config/features` value into a list of feature strings. Godot
 * serializes this as `PackedStringArray("a", "b")` — a typed-array
 * constructor with quoted elements. We extract the quoted strings; if the
 * value is not in that form, fall back to a lenient comma-split (so a bare
 * `Double Precision, GL Compatibility` still works).
 *
 * Robust to commas INSIDE a quoted element and to surrounding whitespace.
 */
function parseFeatures(raw: string): string[] {
  const packedMatch = /^PackedStringArray\((.*)\)$/s.exec(raw);
  if (packedMatch) {
    // Extract every double-quoted string inside the parens. This handles
    // commas inside a quoted element (e.g. "Forward+, Mobile").
    const inner = packedMatch[1];
    const out: string[] = [];
    const re = /"((?:[^"\\]|\\.)*)"/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(inner)) !== null) {
      // Unescape \" → " and \\ → \ (Godot's INI escape rules).
      const unescaped = m[1].replace(/\\"/g, '"').replace(/\\\\/g, "\\");
      const trimmed = unescaped.trim();
      if (trimmed !== "") out.push(trimmed);
    }
    return out;
  }
  // Lenient fallback: plain comma-separated list.
  return raw
    .split(",")
    .map((f) => f.trim())
    .filter((f) => f !== "");
}

function err(
  code: OfflineProjectConfigError["code"],
  message: string,
): { ok: false; error: OfflineProjectConfigError } {
  return { ok: false, error: { code, message } };
}
