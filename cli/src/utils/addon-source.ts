// Resolve WHERE the `addons/godot_open_mcp/` files come from when
// `install-plugin` materializes them.
//
// P6.2 ships a single local source path: an explicit `--source <dir>` wins, else
// the monorepo-relative default (`<cli>/../packages/bridge`) when running from a
// checkout. There is intentionally NO release-zip download path in v1 — the
// plan marks it `adapt/skip` ("do not block v1 on release packaging"); a
// published npm CLI may later vendor a packed addon or download releases. Until
// then, a non-checkout install with no `--source` fails fast with a clear error.
//
// Adapted from the Godot-MCP behavior reference
// (`cli/src/utils/addon-source.ts`) — we drop the `addonDownloadUrl` /
// `assertTrustedDownloadUrl` host-trust rules entirely (those are tied to the
// reference project's GitHub org and zip download, which is out of scope here —
// see ADR-004 / P6.2 "Out of scope"). The local-source resolver shape is the
// part that carries over.
//
// Pure — reads the filesystem (existsSync) but takes no other side effects and
// never throws; a missing source surfaces as a structured errorLabel.

import * as fs from "fs";
import * as path from "path";

/**
 * The directory names an addon source root may be identified by. A `--source`
 * arg may point either at the addon root directly (contains `plugin.cfg`) or at
 * a parent that CONTAINS `addons/godot_open_mcp/`.
 */
export const ADDON_DIR_NAME = "godot_open_mcp";

export type ResolvedAddonSource =
  | { kind: "local"; path: string }
  | { kind: "missing"; errorLabel: "source_missing"; message: string };

/**
 * Resolve a `--source` directory (or the monorepo default) to the addon root.
 * Accepts either:
 *   - a path that IS `addons/godot_open_mcp` (contains `plugin.cfg` directly), or
 *   - a path that CONTAINS `addons/godot_open_mcp/plugin.cfg`.
 *
 * Returns `{ kind: "missing" }` when no plugin.cfg can be found at either
 * location — the caller turns it into a structured install failure.
 */
export function resolveAddonSource(source?: string): ResolvedAddonSource {
  const dir = source !== undefined && source !== "" ? source : defaultMonorepoSource();
  if (dir === null) {
    return {
      kind: "missing",
      errorLabel: "source_missing",
      message:
        "No addon source found. Run from the monorepo checkout, or pass --source <path> pointing at a directory that contains addons/godot_open_mcp/plugin.cfg (or the addon root directly).",
    };
  }

  const resolved = path.resolve(dir);
  // Direct addon root: contains plugin.cfg.
  if (fs.existsSync(path.join(resolved, "plugin.cfg"))) {
    return { kind: "local", path: resolved };
  }
  // Parent of an addon tree: contains addons/godot_open_mcp/plugin.cfg.
  const nested = path.join(resolved, "addons", ADDON_DIR_NAME);
  if (fs.existsSync(path.join(nested, "plugin.cfg"))) {
    return { kind: "local", path: nested };
  }

  return {
    kind: "missing",
    errorLabel: "source_missing",
    message: `--source ${resolved} does not contain a godot_open_mcp addon (no plugin.cfg found at it or at addons/${ADDON_DIR_NAME}/).`,
  };
}

/**
 * The monorepo default source when no `--source` is given: the `packages/bridge`
 * directory relative to this CLI package. Computed from this module's location
 * so it resolves correctly whether the CLI runs from `src/`, built `dist/`, or
 * the installed `dist/` (the relative layout is identical).
 *
 * Returns null when the default does not exist on disk (e.g. an npm-installed
 * CLI with no checkout nearby) so the caller can ask for `--source`.
 */
export function defaultMonorepoSource(): string | null {
  // cli/src/utils/addon-source.ts  →  ../../packages/bridge
  // (works from dist/utils/addon-source.js too — same relative depth).
  const candidate = path.resolve(__dirname, "..", "..", "..", "packages", "bridge");
  return fs.existsSync(path.join(candidate, "plugin.cfg")) ? candidate : null;
}
