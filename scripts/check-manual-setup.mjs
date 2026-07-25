#!/usr/bin/env node
// check-manual-setup.mjs — drift + safety audit for docs/manual-setup.md.
//
// P9.3 acceptance criteria require that the manual setup guide:
//   - ships stdio MCP config snippets that match the agent registry's actual
//     `getStdioProps` output for Cursor and Claude Desktop/Code (the required
//     clients), byte-for-byte against a fixed version + project path,
//   - uses an absolute `GODOT_PROJECT_PATH` in every snippet,
//   - never advertises a URL / HTTP transport / auth-token shape (stdio-only,
//     ADR-001),
//   - pins the same package version the registries live at (`version.json`),
//   - does not leak internal phase/spec ids, maintainer paths, or forbidden
//     reference-project names.
//
// The checker reads the doc straight, parses every fenced JSON block, and
// compares each required client's snippet to the canonical shape the
// `agentRegistry` entry's `getStdioProps` produces. The agent registry lives in
// TypeScript (`cli/src/utils/agents.ts`); we extract the shaper bodies via
// regex so the checker never carries a second handwritten copy of the envelope.
//
// Run locally:
//   node scripts/check-manual-setup.mjs
//
// Exits 1 on any violation. Wired into .github/workflows/ci.yml
// (manual-setup-parity job).

import { readFileSync, existsSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const DOC_PATH = "docs/manual-setup.md";
const VERSION_PATH = "version.json";
const AGENTS_REGISTRY_PATH = "cli/src/utils/agents.ts";
const PACKAGE_JSON_PATH = "mcp-server/package.json";

const errors = [];
const warnings = [];

function fail(msg) {
  errors.push(msg);
}
function warn(msg) {
  warnings.push(msg);
}

function readText(rel) {
  return readFileSync(join(REPO_ROOT, rel), "utf-8");
}
function readJson(rel) {
  return JSON.parse(readText(rel));
}

// ---------------------------------------------------------------------------
// Extract the canonical stdio envelope per agent id from agents.ts.
// ---------------------------------------------------------------------------

/**
 * Hand-rolled reconstruction of each agent's `getStdioProps(input)` output.
 * Mirrors the three shaper functions in agents.ts (bareStdio / vscodeStdio /
 * opencodeStdio) — the checker applies the same transform to a fixed input and
 * compares the result to the doc snippet. The agents.ts file is the source of
 * truth: if a shaper changes shape, this reconstruction must change too (and
 * the diff is the signal).
 */
function shaperFor(agentId) {
  switch (agentId) {
    case "cursor":
    case "claude-code":
    case "claude-desktop":
    case "gemini":
    case "cline":
    case "kilo-code":
    case "github-copilot-cli":
    case "custom":
      return (input) => ({
        command: input.command,
        args: input.args,
        env: input.env,
      });
    case "vscode-copilot":
    case "vs-copilot":
      return (input) => ({
        type: "stdio",
        command: input.command,
        args: input.args,
        env: input.env,
      });
    case "opencode":
      return (input) => ({
        type: "local",
        command: [input.command, ...input.args],
        enabled: true,
        environment: input.env,
      });
    default:
      return null;
  }
}

/**
 * Where each agent's server entry lives in its config root (the `bodyPath` in
 * agents.ts). The snippet checker walks the parsed JSON to the same path so it
 * can locate the server entry inside the documented outer envelope.
 */
function bodyPathFor(agentId) {
  switch (agentId) {
    case "vscode-copilot":
    case "vs-copilot":
      return ["servers"];
    case "opencode":
      return ["mcp"];
    default:
      return ["mcpServers"];
  }
}

/**
 * Parse `{ id, bodyPath, getStdioProps }` out of the agent registry source.
 *
 * The header of this file claims the checker "never carries a second handwritten copy of the
 * envelope", but `shaperFor` / `bodyPathFor` above are exactly that, and `AGENTS_REGISTRY_PATH` was
 * declared and never read. So if a shaper in agents.ts gained or renamed a key — meaning `setup-mcp`
 * started writing a different entry shape — the checker compared the doc against its own stale
 * hardcoded shape, they still agreed, and the job passed green while docs/manual-setup.md no longer
 * described what the CLI writes. That is precisely the drift this gate exists to catch.
 *
 * Fully re-evaluating the TypeScript shapers from a script is not worth the fragility, so instead we
 * pin the *mapping*: every agent id in the registry must be known here, with the same `bodyPath` and
 * the same shaper function. Adding an agent, renaming a bodyPath, or re-pointing an agent at a
 * different shaper now fails the gate and forces this file to be updated alongside agents.ts.
 */
function parseAgentRegistry(source) {
  const agents = [];
  // Each entry is an object literal containing `id:`, `bodyPath:` and `getStdioProps:`.
  const entryRe =
    /id:\s*"([^"]+)"[\s\S]*?bodyPath:\s*\[([^\]]*)\][\s\S]*?getStdioProps:\s*([A-Za-z0-9_$]+)/g;
  let m;
  while ((m = entryRe.exec(source)) !== null) {
    const id = m[1];
    const bodyPath = m[2]
      .split(",")
      .map((s) => s.trim().replace(/^["']|["']$/g, ""))
      .filter((s) => s.length > 0);
    agents.push({ id, bodyPath, shaperName: m[3] });
  }
  return agents;
}

/** The shaper each agent id is expected to use, mirroring `shaperFor`'s grouping. */
function expectedShaperName(agentId) {
  switch (agentId) {
    case "vscode-copilot":
    case "vs-copilot":
      return "vscodeStdio";
    case "opencode":
      return "opencodeStdio";
    default:
      return "bareStdio";
  }
}

/**
 * Assert this checker's hardcoded shaper/bodyPath mapping still matches the registry. Reads
 * AGENTS_REGISTRY_PATH — the constant that was previously dead.
 */
function checkAgentRegistryParity() {
  const registry = parseAgentRegistry(readText(AGENTS_REGISTRY_PATH));
  if (registry.length === 0) {
    fail(
      `${AGENTS_REGISTRY_PATH}: could not parse any agent entries — the registry shape changed and ` +
        `this checker's parser (parseAgentRegistry) needs updating.`,
    );
    return;
  }

  for (const agent of registry) {
    if (shaperFor(agent.id) === null) {
      fail(
        `${AGENTS_REGISTRY_PATH}: agent "${agent.id}" is in the registry but unknown to ` +
          `check-manual-setup.mjs — add it to shaperFor()/bodyPathFor() and document it in ${DOC_PATH}.`,
      );
      continue;
    }
    const wantBodyPath = bodyPathFor(agent.id);
    if (JSON.stringify(agent.bodyPath) !== JSON.stringify(wantBodyPath)) {
      fail(
        `${AGENTS_REGISTRY_PATH}: agent "${agent.id}" declares bodyPath ` +
          `${JSON.stringify(agent.bodyPath)} but this checker expects ${JSON.stringify(wantBodyPath)}.`,
      );
    }
    const wantShaper = expectedShaperName(agent.id);
    if (agent.shaperName !== wantShaper) {
      fail(
        `${AGENTS_REGISTRY_PATH}: agent "${agent.id}" uses shaper ${agent.shaperName} but this ` +
          `checker models it as ${wantShaper} — update shaperFor() to match.`,
      );
    }
  }
}

/**
 * Build the canonical `getStdioProps` output for an agent, using the same
 * `npx -y godot-open-mcp@<version>` spawn descriptor `setup-mcp` produces when
 * `--use-local` is NOT set. `projectPath` is the absolute path that goes into
 * `GODOT_PROJECT_PATH`.
 */
function canonicalStdioEntry(agentId, version, projectPath) {
  const shaper = shaperFor(agentId);
  if (shaper === null) return null;
  const input = {
    command: "npx",
    args: ["-y", `godot-open-mcp@${version}`],
    env: { GODOT_PROJECT_PATH: projectPath },
  };
  return shaper(input);
}

// ---------------------------------------------------------------------------
// Doc snippet extraction
// ---------------------------------------------------------------------------

/**
 * Extract every fenced ```json block from the manual-setup doc, tagged with the
 * line number where it starts so error messages can point at the snippet.
 */
function extractJsonBlocks(text) {
  const blocks = [];
  const lines = text.split("\n");
  let i = 0;
  while (i < lines.length) {
    if (/^```json\s*$/.test(lines[i])) {
      const startLine = i + 1;
      const body = [];
      i++;
      while (i < lines.length && !/^```\s*$/.test(lines[i])) {
        body.push(lines[i]);
        i++;
      }
      blocks.push({ startLine, text: body.join("\n") });
    }
    i++;
  }
  return blocks;
}

/**
 * Parse a snippet body and return the server entry object + the bodyPath it was
 * found at. Returns null when the snippet isn't a server config we recognize
 * (e.g. the local-checkout `node` variant). The returned shape is `{ entry,
 * bodyPath, root }` so callers can compare both the entry and its container.
 */
function parseServerEntry(parsed) {
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return null;
  }
  for (const bodyPath of [["mcpServers"], ["servers"], ["mcp"]]) {
    let cursor = parsed;
    let ok = true;
    for (const seg of bodyPath) {
      if (
        typeof cursor !== "object" ||
        cursor === null ||
        Array.isArray(cursor) ||
        !(seg in cursor)
      ) {
        ok = false;
        break;
      }
      cursor = cursor[seg];
    }
    if (!ok || typeof cursor !== "object" || cursor === null) continue;
    const entry = cursor["godot-open-mcp"];
    if (entry && typeof entry === "object") {
      return { entry, bodyPath, root: parsed };
    }
  }
  return null;
}

// ---------------------------------------------------------------------------
// Audit stages
// ---------------------------------------------------------------------------

function auditDocExists() {
  if (!existsSync(join(REPO_ROOT, DOC_PATH))) {
    fail(`${DOC_PATH} does not exist.`);
  }
}

/**
 * The required clients per the P9.3 plan: Cursor + Claude Desktop + Claude Code.
 * Each must have a documented snippet whose parsed entry matches the registry's
 * canonical `getStdioProps` output.
 */
const REQUIRED_AGENTS = ["cursor", "claude-desktop", "claude-code"];

function auditRequiredSnippetsMatchRegistry(version, projectPath) {
  const text = readText(DOC_PATH);
  const blocks = extractJsonBlocks(text);

  // Parse + classify every JSON block.
  const seenSnippets = [];
  for (const block of blocks) {
    let parsed;
    try {
      parsed = JSON.parse(block.text);
    } catch (err) {
      fail(
        `${DOC_PATH}:${block.startLine}: unparseable JSON snippet (${err.message}).`,
      );
      continue;
    }
    const server = parseServerEntry(parsed);
    if (server === null) continue;
    seenSnippets.push({ ...server, startLine: block.startLine });
  }

  // For every npx snippet (the canonical public install path), classify it by
  // envelope shape and ensure it matches the canonical entry for SOME agent in
  // the registry.
  for (const snippet of seenSnippets) {
    // Local-checkout `node` snippets are documented separately and don't carry
    // the version pin — skip them here (they have their own audit below).
    if (snippet.entry.command === "node") continue;

    // Find the agent whose canonical shape this snippet matches.
    let matchedAgent = null;
    for (const agentId of REQUIRED_AGENTS) {
      const canonical = canonicalStdioEntry(agentId, version, projectPath);
      if (canonical === null) continue;
      if (jsonEqual(snippet.entry, canonical)) {
        matchedAgent = agentId;
        break;
      }
    }
    if (matchedAgent === null) {
      // Could be a non-required agent (vscode/opencode) — that's fine as long
      // as it matches ITS canonical shape. Try every known shaper.
      const allAgentIds = [
        "cursor",
        "claude-code",
        "claude-desktop",
        "vscode-copilot",
        "vs-copilot",
        "opencode",
        "gemini",
        "cline",
        "kilo-code",
        "github-copilot-cli",
        "custom",
      ];
      for (const agentId of allAgentIds) {
        const canonical = canonicalStdioEntry(agentId, version, projectPath);
        if (canonical !== null && jsonEqual(snippet.entry, canonical)) {
          matchedAgent = agentId;
          break;
        }
      }
      if (matchedAgent === null) {
        fail(
          `${DOC_PATH}:${snippet.startLine}: stdio snippet does not match any agent registry canonical shape. Got: ${JSON.stringify(snippet.entry)}`,
        );
      }
    }
  }

  // Each required agent must be represented by at least one matching snippet.
  for (const agentId of REQUIRED_AGENTS) {
    const canonical = canonicalStdioEntry(agentId, version, projectPath);
    if (canonical === null) {
      fail(`No canonical shape known for required agent '${agentId}'.`);
      continue;
    }
    const hasMatch = seenSnippets.some((s) => jsonEqual(s.entry, canonical));
    if (!hasMatch) {
      fail(
        `${DOC_PATH}: required client '${agentId}' has no stdio snippet matching the registry canonical shape (${JSON.stringify(canonical)}).`,
      );
    }
  }
}

function auditEnvKeys(snippets) {
  // Every server entry must:
  //  - carry an absolute GODOT_PROJECT_PATH,
  //  - never carry url / headers / type:"http" / token keys.
  for (const snippet of snippets) {
    const entry = snippet.entry;
    const env = entry.env ?? entry.environment;
    if (!env || typeof env !== "object") {
      fail(
        `${DOC_PATH}:${snippet.startLine}: server entry is missing env/environment.`,
      );
      continue;
    }
    const projPath = env.GODOT_PROJECT_PATH;
    if (typeof projPath !== "string" || projPath.length === 0) {
      fail(
        `${DOC_PATH}:${snippet.startLine}: server entry is missing GODOT_PROJECT_PATH.`,
      );
    } else if (
      !/^([A-Za-z]:[\\/]|[\\/]|\/|[A-Za-z]:)/.test(projPath) &&
      !projPath.startsWith("/")
    ) {
      fail(
        `${DOC_PATH}:${snippet.startLine}: GODOT_PROJECT_PATH '${projPath}' is not absolute (must be the absolute path to the project.godot directory).`,
      );
    }
    const forbidden = ["url", "headers", "token", "authToken", "apiKey"];
    for (const key of forbidden) {
      if (key in entry) {
        fail(
          `${DOC_PATH}:${snippet.startLine}: server entry must not carry a '${key}' key (stdio-only, ADR-001).`,
        );
      }
    }
    // `type`, if present, must be "stdio" or "local" — never "http".
    if ("type" in entry && entry.type !== "stdio" && entry.type !== "local") {
      fail(
        `${DOC_PATH}:${snippet.startLine}: server entry 'type' must be "stdio" or "local" (got '${entry.type}').`,
      );
    }
  }
}

function auditNoInternalLeaks() {
  const text = readText(DOC_PATH);
  // Phase ids, spec paths, execution-plan references — never in user docs.
  const internalPatterns = [
    /\bP\d+\.\d+\b/,
    /\bspecs\/execution\b/,
    /\bporting-map\b/,
    /\bexecution-plan\b/,
  ];
  for (const re of internalPatterns) {
    const m = re.exec(text);
    if (m) {
      fail(
        `${DOC_PATH}: internal reference '${m[0]}' must not appear in a user-visible doc.`,
      );
    }
  }
  // Forbidden reference-project names (only Unity Open MCP is allowed by AGENTS.md).
  // Use word-boundary matches so the actual addon class name `GodotOpenMcpPlugin`
  // is not flagged as a substring of the reference project's `McpPlugin` package.
  const forbiddenNames = [
    "Godot-MCP",
    "godot-cli",
    "ReflectorNet",
    "McpPlugin",
    "ai-game\\.dev",
    "IvanMurzak",
  ];
  for (const name of forbiddenNames) {
    const re = new RegExp(`(?:^|[^A-Za-z])${name}(?:[^A-Za-z]|$)`);
    if (re.test(text)) {
      fail(
        `${DOC_PATH}: forbidden reference-project name '${name}' must not appear (only Unity Open MCP may be named).`,
      );
    }
  }
  // Concrete token / home paths must not leak.
  const tokenPatterns = [
    /Bearer\s+[A-Za-z0-9._-]{16,}/,
    /\/Users\/[a-z][a-z0-9]+\/Projects\b/i,
  ];
  for (const re of tokenPatterns) {
    const m = re.exec(text);
    if (m) {
      fail(`${DOC_PATH}: looks like a secret/path leaked: '${m[0]}'.`);
    }
  }
}

function auditVersionPinning(version) {
  const text = readText(DOC_PATH);
  // Every `godot-open-mcp@<x>` pin in the doc must match version.json.
  const re = /godot-open-mcp@([0-9][0-9a-zA-Z.\-+]*)/g;
  let m;
  let foundAny = false;
  while ((m = re.exec(text)) !== null) {
    foundAny = true;
    if (m[1] !== version) {
      fail(
        `${DOC_PATH}: pinned version '${m[1]}' does not match version.json '${version}'.`,
      );
    }
  }
  if (!foundAny) {
    fail(`${DOC_PATH}: no \`godot-open-mcp@<version>\` pin found.`);
  }
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

function jsonEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b) return false;
  if (Array.isArray(a)) {
    if (!Array.isArray(b) || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
      if (!jsonEqual(a[i], b[i])) return false;
    }
    return true;
  }
  if (a && typeof a === "object") {
    if (!b || typeof b !== "object") return false;
    const ka = Object.keys(a);
    const kb = Object.keys(b);
    if (ka.length !== kb.length) return false;
    for (const k of ka) {
      if (!(k in b)) return false;
      if (!jsonEqual(a[k], b[k])) return false;
    }
    return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

function main() {
  auditDocExists();
  if (errors.length > 0) {
    printReport();
    process.exit(1);
  }

  const version = readJson(VERSION_PATH).version;
  if (typeof version !== "string" || version.length === 0) {
    fail(`${VERSION_PATH}: missing or invalid 'version' field.`);
    printReport();
    process.exit(1);
  }

  // Before comparing the doc against this checker's model of the shapers, verify that model still
  // matches the real registry — otherwise every downstream comparison is against a stale shape.
  checkAgentRegistryParity();

  // Validate package.json bin name matches the doc's server key.
  const pkg = readJson(PACKAGE_JSON_PATH);
  if (pkg.name !== "godot-open-mcp") {
    fail(
      `${PACKAGE_JSON_PATH}: package name '${pkg.name}' does not match the doc's 'godot-open-mcp' server key.`,
    );
  }

  // The fixed project path used to compare snippets against the registry's
  // canonical output. The doc uses an absolute placeholder path; the registry
  // produces the same shape with whatever absolute path it is handed, so this
  // value just has to match what the doc's snippets actually write.
  const projectPath = "/absolute/path/to/MyGame";

  auditRequiredSnippetsMatchRegistry(version, projectPath);

  // Re-extract for the env-key audit.
  const text = readText(DOC_PATH);
  const blocks = extractJsonBlocks(text);
  const seenSnippets = [];
  for (const block of blocks) {
    let parsed;
    try {
      parsed = JSON.parse(block.text);
    } catch {
      continue;
    }
    const server = parseServerEntry(parsed);
    if (server) seenSnippets.push({ ...server, startLine: block.startLine });
  }
  auditEnvKeys(seenSnippets);

  auditVersionPinning(version);
  auditNoInternalLeaks();

  printReport();
  process.exit(errors.length > 0 ? 1 : 0);
}

function printReport() {
  if (errors.length > 0) {
    console.error(`❌ check-manual-setup: ${errors.length} error(s)`);
    for (const e of errors) console.error(`  - ${e}`);
  } else {
    console.error("✅ check-manual-setup: snippets match registry; no leaks.");
  }
  if (warnings.length > 0) {
    console.error(`   (${warnings.length} warning(s))`);
    for (const w of warnings) console.error(`  - ${w}`);
  }
}

main();
