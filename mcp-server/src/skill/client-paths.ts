// TypeScript consumer of `skills/client-paths.json` (P15.5).
//
// The manifest at `skills/client-paths.json` is the single source of truth for
// project-relative agent-skill install paths and the MCP-client → skill-target
// mapping. Until P15.5 it had only non-TypeScript consumers
// (`scripts/check-skill.mjs` validates it; `skills/AGENTS.md` documents it).
// `godot_open_mcp_generate_skill` is the first runtime TS consumer — it must
// resolve the template path and per-client write targets WITHOUT duplicating
// the constants, so a future edit to the manifest flows through without a code
// change here.
//
// Resolution strategy (synchronous so the tool-definition enum can read the
// client keys at module-load time):
//   1. Walk up from this module's location looking for
//      `skills/client-paths.json` (covers `mcp-server/src/skill/` in dev and
//      `mcp-server/dist/skill/` in a built checkout).
//   2. Bundled fallback constant (kept in sync with the manifest by a unit
//      test) so the skill generator still works in a standalone `mcp-server/`
//      install that lacks the toolkit tree.
//
// Adapted from Unity Open MCP's `mcp-server/src/skill/client-paths.ts` (copy
// for the synchronous read-once + walk-up + bundled-fallback +
// `clientSkillRelativePath` / `knownClientKeys` / `resolveTemplateSkillPath`
// vocabulary). Intentional deltas:
//   - The Godot manifest carries a different client-keys roster and skill id
//     (`godot-open-mcp`) — the bundled fallback mirrors Godot's manifest, not
//     Unity's.
//   - No `UNITY_OPEN_MCP_TOOLKIT_ROOT` env override. The Godot package is
//     always shipped from a repo root that contains `skills/`; the walk-up is
//     sufficient. An env override can be added later if a launcher needs it.

import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

// ---------------------------------------------------------------------------
// Manifest shape (mirror of skills/client-paths.schema.json — structural only).
// ---------------------------------------------------------------------------

export interface ClientPathEntry {
  relativePath: string;
}

export interface ClientPathsManifest {
  skillId: string;
  templateRelativePath: string;
  clients: Record<string, ClientPathEntry>;
  mcpClientMapping: Record<string, string[]>;
}

const MANIFEST_REL = "skills/client-paths.json";

/**
 * Bundled fallback mirroring `skills/client-paths.json`. A unit test asserts
 * this stays in sync with the on-disk manifest so the two never drift silently.
 * Used only when the on-disk manifest cannot be located (a standalone
 * `mcp-server/` install without the repo tree).
 */
export const BUNDLED_MANIFEST: ClientPathsManifest = {
  skillId: "godot-open-mcp",
  templateRelativePath: "skills/godot-open-mcp/SKILL.md",
  clients: {
    cursor: { relativePath: ".cursor/skills/godot-open-mcp/SKILL.md" },
    claude: { relativePath: ".claude/skills/godot-open-mcp/SKILL.md" },
    vscode: { relativePath: ".vscode/skills/godot-open-mcp/SKILL.md" },
    vs: { relativePath: ".vs/skills/godot-open-mcp/SKILL.md" },
    opencode: { relativePath: ".opencode/skills/godot-open-mcp/SKILL.md" },
    gemini: { relativePath: ".gemini/skills/godot-open-mcp/SKILL.md" },
    cline: { relativePath: ".cline/skills/godot-open-mcp/SKILL.md" },
    kilocode: { relativePath: ".kilocode/skills/godot-open-mcp/SKILL.md" },
    agents: { relativePath: ".agents/skills/godot-open-mcp/SKILL.md" },
  },
  mcpClientMapping: {
    cursor: ["cursor"],
    "claude-code": ["claude"],
    "claude-desktop": ["claude"],
    "vscode-copilot": ["vscode"],
    "vs-copilot": ["vs"],
    opencode: ["opencode"],
    gemini: ["gemini"],
    cline: ["cline"],
    "kilo-code": ["kilocode"],
    "github-copilot-cli": [],
    custom: ["cursor", "claude", "opencode", "agents"],
  },
};

interface ResolvedManifest {
  manifest: ClientPathsManifest;
  /**
   * Absolute repo-root directory the manifest was loaded from, when discovered
   * from disk. `null` for the bundled fallback (no on-disk repo tree to resolve
   * template paths against).
   */
  repoRoot: string | null;
}

function hereDir(): string {
  // Works under both `node --experimental-strip-types` (src) and the
  // compiled `dist/` output.
  if (typeof __dirname !== "undefined") return __dirname;
  return dirname(fileURLToPath(import.meta.url));
}

function tryReadManifest(path: string): ClientPathsManifest | null {
  try {
    const raw = readFileSync(path, "utf-8");
    const parsed = JSON.parse(raw) as Partial<ClientPathsManifest>;
    if (
      typeof parsed.skillId === "string" &&
      typeof parsed.templateRelativePath === "string" &&
      parsed.clients &&
      typeof parsed.clients === "object" &&
      parsed.mcpClientMapping &&
      typeof parsed.mcpClientMapping === "object"
    ) {
      return parsed as ClientPathsManifest;
    }
    return null;
  } catch {
    return null;
  }
}

function resolveManifestWithRoot(): ResolvedManifest {
  // Walk up from this module's directory looking for the repo root (i.e. a
  // parent dir containing `skills/client-paths.json`). From
  // `mcp-server/{src|dist}/skill/` the repo root is three hops up.
  let dir = hereDir();
  for (let i = 0; i < 8; i++) {
    const candidate = join(dir, MANIFEST_REL);
    const m = tryReadManifest(candidate);
    if (m) return { manifest: m, repoRoot: dir };
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  // Bundled fallback (validated against the manifest by tests).
  return { manifest: BUNDLED_MANIFEST, repoRoot: null };
}

let cached: ResolvedManifest | null = null;
let cachedClientKeys: ReadonlySet<string> | null = null;

function getResolvedManifest(): ResolvedManifest {
  if (cached) return cached;
  cached = resolveManifestWithRoot();
  return cached;
}

/**
 * Load the client-paths manifest. Resolution is synchronous and cached after
 * the first call (the manifest is immutable for the lifetime of the process).
 */
export function loadManifest(): ClientPathsManifest {
  return getResolvedManifest().manifest;
}

/**
 * @internal Test-only cache reset. The manifest resolution is cached for
 * process lifetime. Tests that need to observe a fresh resolution (e.g. after
 * swapping the manifest file) call this. Do not call from runtime code.
 */
export function _clearClientPathsCacheForTests(): void {
  cached = null;
  cachedClientKeys = null;
}

/**
 * Absolute path to the template skill file
 * (`<repoRoot>/<manifest.templateRelativePath>`), resolved from the same root
 * discovery that loads `client-paths.json`. Returns `null` when the repo root
 * cannot be found (bundled-fallback / standalone `mcp-server/` install) so
 * callers can degrade gracefully instead of guessing a path. Never throws.
 *
 * Used by the skill generator to merge the template workflow prose with the
 * project-specific inventory (the template is the source of truth for the
 * workflow playbook).
 */
export function resolveTemplateSkillPath(): string | null {
  const { manifest, repoRoot } = getResolvedManifest();
  if (!repoRoot) return null;
  return join(repoRoot, manifest.templateRelativePath);
}

/**
 * Canonical path to the manifest, when discovered from disk. `null` for the
 * bundled-fallback path. Exposed for diagnostics + tests.
 */
export function manifestDiskPath(): string | null {
  const { repoRoot } = getResolvedManifest();
  if (!repoRoot) return null;
  return join(repoRoot, MANIFEST_REL);
}

/**
 * Project-relative skill destination path for one client key (e.g.
 * `.claude/skills/godot-open-mcp/SKILL.md`). The path is forward-slash and
 * stays inside the target project (the schema enforces no `..` segments).
 *
 * Throws when the client key is unknown — the generator catches per-client so
 * one bad key skips that write rather than aborting the whole call.
 */
export function clientSkillRelativePath(client: string): string {
  const manifest = loadManifest();
  const entry = manifest.clients[client];
  if (!entry) {
    throw new Error(
      `Unknown client key '${client}'. Known keys: ${knownClientKeys().join(", ")}. ` +
        "Edit skills/client-paths.json to add a client.",
    );
  }
  return entry.relativePath;
}

/**
 * All known client keys from the manifest — used to derive the
 * `godot_open_mcp_generate_skill` `clients[]` enum without hand-maintaining a
 * literal array. Synchronous (the manifest is read on first access, then
 * cached) so the tool definition can build its enum at module-load time.
 * Sorted for stable schema output.
 */
export function knownClientKeys(): string[] {
  return Array.from(getKnownClientKeys()).sort();
}

/**
 * The set of known client keys from the resolved skill manifest. Lazy on first
 * access (the manifest itself is lazy); re-derived if the manifest cache is
 * reset for tests. Returns the same Set reference on every call.
 */
export function getKnownClientKeys(): ReadonlySet<string> {
  if (cachedClientKeys === null) {
    cachedClientKeys = new Set(Object.keys(loadManifest().clients));
  }
  return cachedClientKeys;
}

/**
 * Resolve the MCP-client → skill-target mapping (e.g. `claude-code` →
 * `["claude"]`, `custom` → `["cursor", "claude", "opencode", "agents"]`).
 * Used by the Hub wizard (future); the generator itself writes per
 * skill-target key. Synchronous — returns from the cache.
 */
export function loadMcpClientMapping(): Record<string, string[]> {
  return loadManifest().mcpClientMapping;
}
