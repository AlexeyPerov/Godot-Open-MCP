// Safe `res://` path resolution + project containment for the offline reader (P7.2).
//
// The offline `.tscn` parser must never read outside the Godot project root —
// a malicious or malformed scene path like `res://../../etc/passwd` or a
// symlink that escapes the project must be rejected before any file is opened.
// This module is the single chokepoint: every offline read routes its path
// through `resolveResPath`, which validates, joins, resolves realpaths, and
// asserts the final location stays beneath the canonical project root.
//
// Adapted from Unity Open MCP's offline path discipline (adapt fidelity —
// Unity checks project-root containment against the `Assets/` folder and
// rejects `..` traversal; Godot uses `res://` semantics instead of asset
// paths). Intentional deltas:
//   - Godot `res://` is the canonical resource root; we require that prefix
//     (no bare relative paths, no `user://`, no `file://`).
//   - Realpath containment handles symlinks; a symlink whose target escapes
//     the project is refused (`path_outside_project`).
//   - Extension gating is the caller's responsibility (`.tscn` for the scene
//     reader); this module only rejects NUL/control chars + traversal +
//     non-`res://` schemes.

import { lstatSync, realpathSync, statSync } from "node:fs";
import { isAbsolute, join, normalize, sep } from "node:path";

/** Hard byte cap for a single offline scene read (8 MiB). Guards against a
 *  pathological or hostile huge `.tscn` blowing the parse. */
export const SCENE_BYTE_CAP = 8 * 1024 * 1024;

const RES_PREFIX = "res://";

/** Structured resolution outcome. The `kind` discriminates success from each
 *  failure class so the caller maps straight to the matching offline error
 *  code without re-parsing. */
export type ResolveResResult =
  | { kind: "ok"; nativePath: string }
  | { kind: "invalid_path"; message: string }
  | { kind: "path_outside_project"; message: string };

/** Pure-shape validation shared by the file + directory resolvers (steps that
 *  do not touch the project root): `res://` prefix, URL-authority rejection,
 *  NUL / control / backslash rejection, and `..` traversal rejection. Returns
 *  the validated `afterScheme` tail on success, or the matching failure
 *  result. Extracted so {@link resolveResPath} and {@link resolveResDir} apply
 *  identical character/scheme discipline. */
function validateResShape(
  resPath: string,
): { ok: true; afterScheme: string } | { ok: false; result: ResolveResResult } {
  if (typeof resPath !== "string" || resPath === "") {
    return {
      ok: false,
      result: { kind: "invalid_path", message: "path is empty." },
    };
  }
  if (!resPath.startsWith(RES_PREFIX)) {
    return {
      ok: false,
      result: {
        kind: "invalid_path",
        message: `path must start with 'res://' (got '${truncate(resPath)}').`,
      },
    };
  }

  // Godot `res://` is an opaque project-relative scheme with no authority
  // component. A `res://host/...` form is not produced by Godot and would
  // confuse a naive join; reject it.
  const afterScheme = resPath.slice(RES_PREFIX.length);
  if (/^[A-Za-z0-9._-]+\.[A-Za-z]{2,}\//.test(afterScheme)) {
    return {
      ok: false,
      result: {
        kind: "invalid_path",
        message: `path has a URL-like authority component, which Godot 'res://' never carries ('${truncate(resPath)}').`,
      },
    };
  }

  // Reject NUL (string-termination attack), backslashes (Windows separator
  // ambiguity on POSIX — Godot writes forward slashes), and control chars.
  if (/\0/.test(resPath)) {
    return {
      ok: false,
      result: { kind: "invalid_path", message: "path contains a NUL byte." },
    };
  }
  if (resPath.includes("\\")) {
    return {
      ok: false,
      result: {
        kind: "invalid_path",
        message: `path contains a backslash; Godot 'res://' paths use forward slashes ('${truncate(resPath)}').`,
      },
    };
  }
  if (/[\x00-\x1f]/.test(resPath)) {
    return {
      ok: false,
      result: { kind: "invalid_path", message: "path contains a control character." },
    };
  }

  // Reject traversal segments outright. normalize() would collapse them, but
  // an explicit refusal surfaces the intent (and refuses `..` that would stay
  // inside the project — the offline reader has no reason to use it).
  const segments = afterScheme.split("/");
  if (segments.some((s) => s === "..")) {
    return {
      ok: false,
      result: {
        kind: "invalid_path",
        message: `path contains a '..' traversal segment, which is not permitted ('${truncate(resPath)}').`,
      },
    };
  }
  return { ok: true, afterScheme };
}

/**
 * Resolve a `res://...` path to a native filesystem path safely beneath the
 * canonical project root.
 *
 * Validation pipeline (first failure wins):
 *   1. Must be a non-empty string starting with `res://`.
 *   2. Reject NUL bytes, backslashes (path-separator ambiguity), and any
 *      control characters.
 *   3. Reject URL-like alternate schemes that could fool a naive prefix check
 *      (`res://evil.com/x` is fine by the prefix but we still sanity-check the
 *      host segment is empty — Godot's `res://` has no authority).
 *   4. Reject any `..` path segment (traversal) — even one that would
 *      technically stay inside the project; the offline reader never needs it.
 *   5. Join under the project root and normalize.
 *   6. Resolve the parent dir's realpath (the file itself may not exist yet
 *      at probe time, but its containing dir must) and assert the canonical
 *      real path stays inside the canonical project root realpath.
 *
 * Returns `{ kind: "ok", nativePath }` on success. The path is NOT verified to
 * exist here — the caller does that (it may want `scene_not_found` vs
 * `scene_unreadable`). What is guaranteed: IF the file exists, its real path
 * is inside the project.
 */
export function resolveResPath(
  resPath: string,
  projectRoot: string,
): ResolveResResult {
  const shape = validateResShape(resPath);
  if (!shape.ok) return shape.result;
  const afterScheme = shape.afterScheme;

  // Join + normalize under the project root. normalize() also folds redundant
  // `//` and `.` segments Godot occasionally emits.
  const projectReal = canonicalRoot(projectRoot);
  if (projectReal === null) {
    return {
      kind: "path_outside_project",
      message: "project root does not exist or is not accessible.",
    };
  }
  const joined = normalize(join(projectReal, afterScheme));
  if (!joined.startsWith(projectReal + sep) && joined !== projectReal) {
    // Should be unreachable after the `..` refusal + normalize, but defense in
    // depth: if a join somehow escapes, refuse before touching the filesystem.
    return {
      kind: "path_outside_project",
      message: "resolved path escapes the project root after normalization.",
    };
  }

  // Containment via the parent dir's realpath. The file itself may not exist
  // (the caller distinguishes scene_not_found), but its containing directory
  // must resolve to something inside the project — otherwise a symlinked dir
  // could point outside.
  const parentReal = canonicalRoot(dirOf(joined));
  if (parentReal === null || !isInside(parentReal, projectReal)) {
    return {
      kind: "path_outside_project",
      message: `resolved path escapes the project root (symlink or non-existent parent).`,
    };
  }

  // Re-base the file under the parent's real path so the final nativePath
  // reflects the symlink-resolved location.
  const fileName = baseName(joined);
  const nativePath = fileName === "" ? parentReal : join(parentReal, fileName);

  // File-realpath containment: if the path itself exists (possibly as a
  // symlink), resolve ITS realpath and verify it stays inside the project.
  // The parent-dir check above does not catch a symlink FILE inside a legit
  // directory that points outside (e.g. res://scenes/link.tscn → /etc/x.tscn).
  // realpathSync follows the link to the true target. A missing file is fine
  // here — the caller distinguishes scene_not_found; we only refuse when an
  // EXISTING entry resolves outside.
  const fileReal = canonicalRoot(nativePath);
  if (fileReal !== null && !isInside(fileReal, projectReal)) {
    return {
      kind: "path_outside_project",
      message: "resolved path escapes the project root (symlink target is outside the project).",
    };
  }

  return { kind: "ok", nativePath };
}

/**
 * Resolve a `res://` DIRECTORY path (or bare `res://`/`res://`/empty for the
 * project root) to a native filesystem path safely beneath the canonical
 * project root. Backs the offline directory listing (P7.3).
 *
 * Differs from {@link resolveResPath}:
 *   - Bare/empty/`res://` input resolves to the project root itself (a
 *     directory listing root). The file resolver treats bare input as
 *     `invalid_path`.
 *   - A trailing slash on the input is allowed and normalized away (the
 *     caller decides the canonical `res://` output form).
 *   - If the resolved native path EXISTS, its own realpath must stay inside
 *     the project — this catches a symlinked directory that points outside
 *     (e.g. `res://link/` → `/etc`), which the parent-dir check alone misses
 *     for the root edge case.
 *
 * Does NOT verify the path actually exists or is a directory — the caller does
 * that (it may want `directory_not_found` vs `invalid_path`). What is
 * guaranteed: IF a directory exists at the resolved location, its real path is
 * inside the project. Returns the same structured outcome as
 * {@link resolveResPath}.
 */
export function resolveResDir(
  resDir: string,
  projectRoot: string,
): ResolveResResult {
  // The directory resolver accepts a bare `res://` or empty input as the
  // project root. Anything else must still pass the shared `res://` shape gate.
  const input = typeof resDir === "string" ? resDir.trim() : "";
  const isRootForm = input === "" || input === RES_PREFIX;

  let afterScheme: string;
  if (isRootForm) {
    afterScheme = "";
  } else {
    const shape = validateResShape(input);
    if (!shape.ok) return shape.result;
    afterScheme = shape.afterScheme;
  }

  // A trailing slash is cosmetic for a directory; normalize it away so the
  // join produces a clean native path. Redundant `//` and `.` segments fold
  // via normalize() below.
  if (afterScheme.endsWith("/")) afterScheme = afterScheme.slice(0, -1);

  const projectReal = canonicalRoot(projectRoot);
  if (projectReal === null) {
    return {
      kind: "path_outside_project",
      message: "project root does not exist or is not accessible.",
    };
  }
  const joined = afterScheme === "" ? projectReal : normalize(join(projectReal, afterScheme));
  if (!joined.startsWith(projectReal + sep) && joined !== projectReal) {
    return {
      kind: "path_outside_project",
      message: "resolved directory escapes the project root after normalization.",
    };
  }

  // Directory-realpath containment: if the dir itself exists (possibly a
  // symlink), follow it and refuse any target outside the project. The file
  // resolver's parent-dir check collapses to the same idea; for the root form
  // the parent IS the project, so this realpath check is the only symlink
  // defense for the project root itself.
  const dirReal = canonicalRoot(joined);
  if (dirReal !== null && !isInside(dirReal, projectReal)) {
    return {
      kind: "path_outside_project",
      message: "resolved directory escapes the project root (symlink target is outside the project).",
    };
  }

  // Use the realpath when available (matches on-disk identity); otherwise fall
  // back to the joined path so the caller can distinguish not-found from
  // escape on a missing directory.
  return { kind: "ok", nativePath: dirReal ?? joined };
}

/**
 * Verify the resolved native path points at a regular file and report its
 * size. Used by the scene reader to refuse non-regular files (directories,
 * devices, pipes) and to enforce {@link SCENE_BYTE_CAP} before reading.
 *
 *   - missing → `{ kind: "not_found" }`
 *   - not a regular file → `{ kind: "not_regular" }`
 *   - too large → `{ kind: "too_large", size, cap }`
 *   - permission/stat failure → `{ kind: "unreadable", message }`
 *   - otherwise → `{ kind: "ok", size }`
 */
export type ProbeFileResult =
  | { kind: "ok"; size: number }
  | { kind: "not_found" }
  | { kind: "not_regular" }
  | { kind: "too_large"; size: number; cap: number }
  | { kind: "unreadable"; message: string };

export function probeFile(nativePath: string, cap = SCENE_BYTE_CAP): ProbeFileResult {
  let st;
  try {
    st = lstatSync(nativePath);
  } catch (err) {
    const code = (err as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT") return { kind: "not_found" };
    if (code === "EACCES") return { kind: "unreadable", message: "permission denied" };
    return {
      kind: "unreadable",
      message: (err as Error)?.message ?? "stat failed",
    };
  }
  if (!st.isFile()) return { kind: "not_regular" };
  // Follow a final symlink to size the real target (lstat above sized the
  // link itself, which is meaningless for a byte cap). A symlink target that
  // escaped the project was already refused by resolveResPath's parent-realpath
  // containment check; here we only need the real size.
  let real;
  try {
    real = statSync(nativePath);
  } catch (err) {
    const code = (err as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT") return { kind: "not_found" };
    return {
      kind: "unreadable",
      message: (err as Error)?.message ?? "stat (follow) failed",
    };
  }
  if (real.size > cap) return { kind: "too_large", size: real.size, cap };
  return { kind: "ok", size: real.size };
}

/** Canonical (realpath'd, no trailing sep) path, or `null` when it cannot be
 *  resolved (missing / permission denied). Exported so the directory listing
 *  can classify symlinks against the same canonical identity the resolvers
 *  use. */
export function canonicalRoot(dir: string): string | null {
  try {
    const real = realpathSync(dir);
    // Strip a trailing separator so the `real + sep` containment prefix check
    // is unambiguous (otherwise `/proj` would falsely contain `/project-x`).
    return real.endsWith(sep) && real !== sep ? real.slice(0, -1) : real;
  } catch {
    return null;
  }
}

/** Is `child` equal to or beneath `parent` (both canonicalized)? Exported for
 *  the directory listing's symlink-target containment check. */
export function isInside(child: string, parent: string): boolean {
  return child === parent || child.startsWith(parent + sep);
}

function dirOf(p: string): string {
  const i = p.lastIndexOf(sep);
  return i <= 0 ? (isAbsolute(p) ? sep : ".") : p.slice(0, i);
}

function baseName(p: string): string {
  const i = p.lastIndexOf(sep);
  return i < 0 ? p : p.slice(i + 1);
}

function truncate(s: string, n = 60): string {
  return s.length > n ? s.slice(0, n) + "…" : s;
}

/** Require a `.tscn` extension (case-insensitive). Godot writes lowercase
 *  `.tscn`; the offline reader accepts any case but rejects other extensions
 *  (binary `.scn` is out of scope). */
export function requireTscn(resPath: string): boolean {
  return /\.tscn$/i.test(resPath);
}
