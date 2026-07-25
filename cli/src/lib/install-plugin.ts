// Idempotent addon installer — materialize `res://addons/godot_open_mcp/` from
// a local `--source` (or the monorepo default) and enable the plugin in
// `project.godot`.
//
// Adapted from the Godot-MCP behavior reference (`cli/src/lib/install-plugin.ts`)
// with the NuGet/csproj patch, the release-zip download, and the unzip/zip-slip
// machinery stripped (ADR-004 no NuGet pins; P6.2 release packaging out of
// scope). The staging-then-swap materialize, the `project.godot` toggle, and the
// success/failure union shape are the parts that carry over. The bridge addon's
// `Tests/` subtree is excluded so consumer projects never receive test code.
//
// P9.3 packaging audit: the bridge editor code references
// `GodotOpenMcp.Verify.*` namespaces, which the verify package owns
// (`packages/verify/Editor/**`). The installer therefore bundles the verify
// package's `Editor/` source into the addon tree at `addons/godot_open_mcp/Verify/`
// so the consumer's single Godot C# assembly resolves both the bridge and the
// verify namespaces. One plugin, one assembly, one install step.
//
// Library-safe: no stdout noise, no `process.exit`, no throws past the public
// boundary; returns a `{ kind: "success" | "failure" }` union. Idempotent: a
// re-run that finds the addon present and the plugin already enabled reports
// `changed: false` and makes no writes. On a copy failure the project is left
// as found: the materialize stages into a temp sibling and only swaps it into
// the addon dir after a fully successful copy, so a partial materialization is
// rolled back.

import * as fs from "fs";
import * as path from "path";

import { resolveAddonSource } from "../utils/addon-source.js";
import {
  GODOT_OPEN_MCP_PLUGIN_PATH,
  isGodotProjectRoot,
  projectGodotPath,
  togglePluginInText,
} from "../utils/project-godot.js";
import type {
  AddonMaterializeOutcome,
  InstallPluginFailure,
  InstallPluginOptions,
  InstallPluginResult,
} from "./types.js";

/** Relative path of the addon dir inside a Godot project. */
const ADDON_REL_DIR = path.join("addons", "godot_open_mcp");

/**
 * Relative path inside the addon tree where the bundled verify source lives.
 * The bridge editor code references `GodotOpenMcp.Verify.*` namespaces, and the
 * verify package owns those namespaces (`packages/verify/Editor/**`). Godot's
 * `Godot.NET.Sdk` compiles every `.cs` under the project root into one C#
 * assembly, so bundling verify's `Editor/**` source here lets the bridge's
 * `using GodotOpenMcp.Verify.*` resolve without a second addon or assembly
 * reference. This is the "bundled into the bridge addon" layout option from
 * the P9.3 packaging audit: one plugin, one assembly, one install step.
 */
const VERIFY_REL_DIR = path.join("Verify");

/**
 * Directories at the addon root that are never copied into a user project. The
 * bridge ships a `Tests/` subtree (xUnit fixtures) under `packages/bridge/Tests`
 * and build artifacts land under `obj/` / `bin/`; none belong in a consumer's
 * `addons/godot_open_mcp/`.
 */
const COPY_EXCLUDE_DIRS = new Set(["Tests", "obj", "bin", ".godot"]);

/**
 * Files at the addon root that are never copied. `.gitkeep` is a placeholder
 * for the empty dir in the monorepo; `AGENTS.md` is repo-development guidance
 * for agents working in this checkout — neither is part of the shipped addon
 * tree. (Excluding `AGENTS.md` here is a repo-hygiene choice, not a doc rule —
 * it simply isn't an addon runtime file.)
 */
const COPY_EXCLUDE_FILES = new Set([".gitkeep", "AGENTS.md"]);

/**
 * Install the `godot_open_mcp` addon into a Godot project:
 *
 *  1. Materialize `res://addons/godot_open_mcp/` by copying from a local
 *     `--source` directory or the monorepo default. No network call.
 *  2. Flip the `[editor_plugins] enabled` flag in `project.godot`.
 *
 * Idempotent. On a copy failure the existing addon is left untouched (staging is
 * discarded).
 */
export async function installPlugin(
  opts: InstallPluginOptions,
): Promise<InstallPluginResult> {
  const warnings: string[] = [];

  // 1. Validate the target is a Godot project.
  if (typeof opts?.godotProjectPath !== "string" || opts.godotProjectPath.length === 0) {
    return fail("not_godot_project", "godotProjectPath is required and must be a non-empty string.", warnings);
  }
  const projectPath = path.resolve(opts.godotProjectPath);
  const manifestPath = projectGodotPath(projectPath);
  if (!isGodotProjectRoot(projectPath)) {
    return fail(
      "not_godot_project",
      `Not a valid Godot project (missing project.godot): ${projectPath}`,
      warnings,
      { projectGodotPath: manifestPath },
    );
  }

  try {
    // 2. Materialize the addon files (local copy / skip).
    const materialize = materializeAddon(projectPath, opts, warnings);

    // 3. Enable the plugin in project.godot.
    let enabledPlugins: string[] = [];
    let manifestChanged = false;
    try {
      const text = fs.readFileSync(manifestPath, "utf-8");
      const toggled = togglePluginInText(
        text,
        GODOT_OPEN_MCP_PLUGIN_PATH,
        /* enable */ true,
      );
      enabledPlugins = toggled.enabled;
      if (toggled.kind === "changed") {
        fs.writeFileSync(manifestPath, toggled.text);
        manifestChanged = true;
      }
    } catch (err) {
      return fail(
        "project_godot_write_failed",
        `Could not read or write ${manifestPath}: ${errMsg(err)}`,
        warnings,
        { projectGodotPath: manifestPath },
      );
    }

    const changed = materialize.changed || manifestChanged;

    return {
      kind: "success",
      success: true,
      changed,
      projectPath,
      projectGodotPath: manifestPath,
      addonDir: materialize.addonDir,
      pluginPath: GODOT_OPEN_MCP_PLUGIN_PATH,
      enabledPlugins,
      materialize,
      warnings,
    };
  } catch (err) {
    // A materialize failure (source missing / copy error) lands here as a
    // structured errorLabel via the thrown { errorLabel, message } shape.
    if (isStructuredFailure(err)) {
      return fail(err.errorLabel, err.message, warnings, { projectGodotPath: manifestPath });
    }
    return fail(
      "materialize_failed",
      `Install failed: ${errMsg(err)}`,
      warnings,
      { projectGodotPath: manifestPath },
    );
  }
}

/**
 * Materialize `addons/godot_open_mcp/` into the project from a local source.
 * Idempotent: the target dir is replaced atomically-ish (copy into a temp
 * sibling, then swap), so a failure leaves the existing addon untouched.
 */
function materializeAddon(
  projectPath: string,
  opts: InstallPluginOptions,
  warnings: string[],
): AddonMaterializeOutcome {
  const addonDir = path.join(projectPath, ADDON_REL_DIR);

  if (opts.skipMaterialize === true) {
    if (!fs.existsSync(path.join(addonDir, "plugin.cfg"))) {
      warnings.push(
        `skipMaterialize was set but ${path.join(addonDir, "plugin.cfg")} is absent — the editor cannot load the plugin until the addon files are present.`,
      );
    }
    return { source: "skipped", addonDir, changed: false };
  }

  const resolved = resolveAddonSource(opts.source);
  if (resolved.kind === "missing") {
    // Throw a structured failure so the caller's catch maps errorLabel cleanly.
    throw structuredFailure(resolved.errorLabel, resolved.message);
  }

  // Surface a warning up-front when the sibling verify package cannot be
  // located — the install still proceeds but the bridge's verify-coupled code
  // (GatePolicy, VerifyGateAdapter, the MetaTools) will not compile until the
  // verify source is bundled into the addon tree.
  if (resolveBundledVerifyEditor(resolved.path) === null) {
    warnings.push(
      `Could not locate the sibling verify package (looked for packages/verify/Editor next to ${resolved.path}). The bridge addon references GodotOpenMcp.Verify.* and will not compile until verify source is bundled under addons/godot_open_mcp/Verify/.`,
    );
  }

  const changed = copyAddonFromLocal(resolved.path, projectPath, addonDir);
  return { source: "local", addonDir, sourceDir: resolved.path, changed };
}

/**
 * Materialize the addon into a fresh staging sibling via `fill`, then atomically
 * swap it into `addonDir` — UNLESS the existing addon dir already matches the
 * staged tree byte-for-byte, in which case the swap is skipped (idempotent
 * re-run). A mid-fill failure leaves the existing addon untouched (staging is
 * discarded), honoring the install's documented rollback guarantee.
 *
 * Returns true when the addon dir was actually written, false when the staged
 * tree matched the existing one and the swap was skipped.
 */
function stageThenSwapAddon(
  projectPath: string,
  addonDir: string,
  fill: (staging: string) => void,
): boolean {
  const addonReal = path.resolve(addonDir);
  const staging = path.resolve(
    projectPath,
    `.godot-open-mcp-addon-staging-${process.pid}-${Date.now()}`,
  );
  fs.rmSync(staging, { recursive: true, force: true });
  fs.mkdirSync(staging, { recursive: true });
  // Set once the rename consumed `staging`, so the finally does not try to delete the tree that is
  // now the live addon.
  let swapped = false;
  // Non-null once the previous addon has been moved aside and still needs cleanup or restoring.
  let backup: string | null = null;
  try {
    fill(staging);
    // Idempotent short-circuit: if the installed tree already matches, skip the
    // swap so a re-run reports changed:false and avoids touching mtimes.
    if (fs.existsSync(addonReal) && dirsEqual(staging, addonReal)) {
      return false;
    }

    fs.mkdirSync(path.dirname(addonReal), { recursive: true });

    // Move the existing addon aside rather than deleting it up front.
    //
    // The previous order was `rmSync(addonReal)` then `renameSync(staging, addonReal)`. If the
    // rename failed — on Windows a directory rename fails with EPERM/EBUSY whenever any process
    // holds a handle inside the tree (the Godot editor having the project open, an AV scanner), and
    // rmSync itself can fail part-way with maxRetries defaulting to 0 — the user's addon was
    // already gone, and the finally then deleted the staged replacement too. That left the project
    // with no addon at all and nothing to recover from, contradicting this module's documented
    // guarantee that a failure leaves the project as found.
    if (fs.existsSync(addonReal)) {
      backup = `${addonReal}.old-${process.pid}-${Date.now()}`;
      fs.renameSync(addonReal, backup);
    }

    try {
      fs.renameSync(staging, addonReal);
      swapped = true;
    } catch (err) {
      // Put the original back before surfacing the failure.
      if (backup !== null) {
        try {
          fs.rmSync(addonReal, { recursive: true, force: true });
          fs.renameSync(backup, addonReal);
          backup = null;
        } catch {
          // Restoring failed too — keep the backup directory on disk and name it in the error so
          // the user can recover manually rather than silently losing the tree.
          const hint = backup;
          backup = null;
          throw new Error(
            `${(err as Error).message} (the previous addon was preserved at '${hint}' — ` +
              `rename it back to '${addonReal}' to restore it)`,
          );
        }
      }
      throw err;
    }

    return true;
  } finally {
    // Best-effort cleanup: only remove staging when the swap did NOT consume it.
    if (!swapped) {
      fs.rmSync(staging, { recursive: true, force: true });
    }
    // Drop the superseded backup once the new tree is in place.
    if (backup !== null) {
      fs.rmSync(backup, { recursive: true, force: true });
    }
  }
}

/**
 * Recursively copy a local addon dir into the project's
 * `addons/godot_open_mcp/`. Stages into a temp sibling and swaps, so a mid-copy
 * failure leaves the existing addon intact. The `Tests/`, `obj/`, `bin/`, and
 * `.godot/` subtrees are excluded. Idempotent: when the staged tree matches the
 * existing addon, no write happens.
 *
 * Returns true when the addon dir was written, false on a no-op.
 */
function copyAddonFromLocal(
  sourceDir: string,
  projectPath: string,
  addonDir: string,
): boolean {
  return stageThenSwapAddon(projectPath, addonDir, (staging) => {
    copyDirFiltered(sourceDir, staging);
    if (!fs.existsSync(path.join(staging, "plugin.cfg"))) {
      throw structuredFailure(
        "materialize_failed",
        `Copy completed but ${path.join(sourceDir, "plugin.cfg")} did not yield a plugin.cfg in the staging dir.`,
      );
    }
    // Bundle the verify package's Editor/ source into the addon tree at
    // `Verify/`. The bridge editor code (GatePolicy, VerifyGateAdapter, the
    // MetaTools) references `GodotOpenMcp.Verify.*` namespaces, which the verify
    // package owns; without this copy the addon does not compile standalone
    // (confirmed by the P9.3 clean-project build audit).
    const verifyEditor = resolveBundledVerifyEditor(sourceDir);
    if (verifyEditor !== null) {
      copyDirFiltered(verifyEditor, path.join(staging, VERIFY_REL_DIR));
    }
  });
}

/**
 * Resolve the verify package's `Editor/` directory relative to a bridge addon
 * source. The canonical layout is `<monorepo>/packages/bridge` (the addon
 * source) sitting next to `<monorepo>/packages/verify` (the verify package), so
 * the resolver walks up from the source root looking for a sibling
 * `packages/verify/Editor/` directory. Returns null when no verify source is
 * found — the install still succeeds (the bridge addon is shipped) but the
 * bridge's verify-coupled code paths will not compile, which the caller surfaces
 * via a warning.
 *
 * Also handles the nested-source form where `--source` points at a parent of
 * `addons/godot_open_mcp/` (e.g. the bridge package dir from a checkout), so
 * `../verify/Editor` resolves the same way.
 */
function resolveBundledVerifyEditor(addonSourceDir: string): string | null {
  const candidates: string[] = [];
  // 1. `<source>/../verify/Editor` — sibling verify package next to the bridge
  //    addon source (canonical checkout layout: packages/bridge + packages/verify).
  candidates.push(path.resolve(addonSourceDir, "..", "verify", "Editor"));
  // 2. `<source>/../../packages/verify/Editor` — when the source is the nested
  //    addon root under `addons/godot_open_mcp/` (the install layout), walk one
  //    more level up to find the sibling packages tree.
  candidates.push(
    path.resolve(addonSourceDir, "..", "..", "packages", "verify", "Editor"),
  );
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

/**
 * Recursive copy that skips the excluded subtrees. Mirrors `fs.cpSync` with a
 * `filter`, implemented by hand so the exclude set is explicit and testable.
 */
function copyDirFiltered(src: string, dest: string): void {
  fs.mkdirSync(dest, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (COPY_EXCLUDE_DIRS.has(entry.name)) continue;
      copyDirFiltered(path.join(src, entry.name), path.join(dest, entry.name));
    } else if (entry.isFile()) {
      if (COPY_EXCLUDE_FILES.has(entry.name)) continue;
      fs.copyFileSync(path.join(src, entry.name), path.join(dest, entry.name));
    }
    // Symlinks / other types are skipped — the addon tree is plain files only.
  }
}

/**
 * Structural + content equality of two directory trees. Used by the idempotent
 * short-circuit so a re-run whose source matches the installed addon skips the
 * swap (and reports `changed: false`). Compares file names and byte contents —
 * not mtimes or modes — so only real content changes count.
 */
function dirsEqual(a: string, b: string): boolean {
  const entriesA = fs.readdirSync(a, { withFileTypes: true });
  const entriesB = fs.readdirSync(b, { withFileTypes: true });
  if (entriesA.length !== entriesB.length) return false;

  // Index B by name for O(n) lookup.
  const byNameB = new Map(entriesB.map((e) => [e.name, e]));
  for (const ea of entriesA) {
    const eb = byNameB.get(ea.name);
    if (!eb) return false;
    if (ea.isDirectory() !== eb.isDirectory()) return false;
    if (ea.isFile() !== eb.isFile()) return false;
    if (ea.isDirectory()) {
      if (!dirsEqual(path.join(a, ea.name), path.join(b, eb.name))) return false;
    } else if (ea.isFile()) {
      const bufA = fs.readFileSync(path.join(a, ea.name));
      const bufB = fs.readFileSync(path.join(b, eb.name));
      if (Buffer.compare(bufA, bufB) !== 0) return false;
    } else {
      // Non-file/non-dir entry types differ from a plain-file tree.
      return false;
    }
  }
  return true;
}

// ---------------------------------------------------------------------------
// structured-failure plumbing
// ---------------------------------------------------------------------------

/**
 * A thrown object carrying a stable errorLabel + message, so the installer's
 * catch can map an inner failure to the public failure union without string
 * parsing. Recognized via `isStructuredFailure`.
 */
interface StructuredFailure {
  __structuredFailure: true;
  errorLabel: "source_missing" | "materialize_failed";
  message: string;
}

function structuredFailure(
  errorLabel: StructuredFailure["errorLabel"],
  message: string,
): StructuredFailure {
  return { __structuredFailure: true, errorLabel, message };
}

function isStructuredFailure(err: unknown): err is StructuredFailure {
  return (
    typeof err === "object" &&
    err !== null &&
    (err as StructuredFailure).__structuredFailure === true
  );
}

function errMsg(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/** Build a failure result, threading the warnings + optional manifest path. */
function fail(
  errorLabel: InstallPluginFailure["errorLabel"],
  message: string,
  warnings: string[],
  extra?: { projectGodotPath?: string },
): InstallPluginFailure {
  return {
    kind: "failure",
    success: false,
    errorLabel,
    projectGodotPath: extra?.projectGodotPath,
    warnings,
    error: new Error(message),
  };
}
