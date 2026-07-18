#!/usr/bin/env node
// check-skill.mjs — drift + safety audit for the Godot Open MCP agent skill.
//
// Enforces the P9.1 acceptance criteria for skills/godot-open-mcp/SKILL.md and
// skills/client-paths.json. Pure Node 18+ (only node: builtins + JSON.parse —
// no ajv / no TS compiler). The audit reads sources straight so a stale skill
// or a stale client-paths manifest fails CI before it ships into game projects.
//
// What it checks:
//   - skill file exists and is within the 130–180 line budget (target ~150)
//   - every backticked `godot_open_mcp_*` token in the skill is a name listed
//     in mcp-server/src/tools/index.ts (the ALL_TOOLS registry)
//   - no unprefixed `*_tool` / `_edit` token is presented as an MCP tool id
//   - no `batch` route claim and no headless-editor fallback
//   - only `list_groups | activate | deactivate | reset` manage_tools actions
//     are named (the four actions manage-tools.ts declares)
//   - client-paths.json validates against client-paths.schema.json (shape only —
//     we hand-roll the subset we care about; the schema file is the contract
//     for external consumers)
//   - every mcpClientMapping key matches an id in cli/src/utils/agents.ts
//     agentRegistry (no orphan MCP-client ids)
//   - every mcpClientMapping value names a key declared in `clients`
//   - every `clients.<id>.relativePath` is a relative forward-slash .md path
//     with no parent traversal; no two clients share a relativePath
//   - templateRelativePath exists on disk
//   - no internal phase/spec references (specs/, P3.6, M22, roadmap ids) leak
//     into the skill
//   - no forbidden reference-project names leak (only Unity Open MCP is allowed)
//   - no concrete bearer token / home path / maintainer-machine path leaks
//
// Run locally:
//   node scripts/check-skill.mjs
//
// Exits 1 on any violation. Wired into .github/workflows/ci.yml (skill-audit job).

import { readFileSync, existsSync, statSync, readdirSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SKILL_PATH = "skills/godot-open-mcp/SKILL.md";
const MANIFEST_PATH = "skills/client-paths.json";
const SCHEMA_PATH = "skills/client-paths.schema.json";
const TOOLS_INDEX_PATH = "mcp-server/src/tools/index.ts";
const MANAGE_TOOLS_PATH = "mcp-server/src/tools/manage-tools.ts";
const AGENTS_REGISTRY_PATH = "cli/src/utils/agents.ts";

const LINE_BUDGET_MIN = 130;
const LINE_BUDGET_MAX = 180;

const errors = [];
const warnings = [];

function fail(message) {
  errors.push(message);
}

function readText(rel) {
  return readFileSync(join(REPO_ROOT, rel), "utf8");
}

// ---------------------------------------------------------------------------
// 1. Extract every registered MCP tool name from mcp-server/src/tools/index.ts.
//    The registry uses `export const X: Tool = { name: "godot_open_mcp_..." }`
//    in the per-tool files AND an `ALL_TOOLS: Tool[]` array of references in
//    index.ts. The authoritative surface is the array of `name:` literals that
//    appear in src/tools/*.ts (every registered tool ships a definition there).
// ---------------------------------------------------------------------------

function collectRegisteredToolNames() {
  // Read every .ts under mcp-server/src/tools/ and pull `name: "godot_open_mcp_*"`.
  // This catches tool definitions regardless of how index.ts imports them.
  const toolsDir = join(REPO_ROOT, "mcp-server", "src", "tools");
  let files = [];
  try {
    files = readdirRecursive(toolsDir, ".ts").filter((f) => !f.endsWith(".test.ts"));
  } catch (err) {
    fail(`Could not read ${toolsDir}: ${err.message}`);
    return new Set();
  }
  const names = new Set();
  for (const file of files) {
    const body = readFileSync(file, "utf8");
    // Match: name: "godot_open_mcp_..."  (the Tool catalog metadata)
    const re = /\bname\s*:\s*"(godot_open_mcp_[a-z0-9_]+)"/g;
    let m;
    while ((m = re.exec(body)) !== null) {
      names.add(m[1]);
    }
  }
  return names;
}

function readdirRecursive(dir, ext) {
  const out = [];
  for (const ent of readdirSync(dir)) {
    const full = join(dir, ent);
    const st = statSync(full);
    if (st.isDirectory()) {
      out.push(...readdirRecursive(full, ext));
    } else if (ent.endsWith(ext)) {
      out.push(full);
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// 2. Extract the accepted manage_tools action enum from manage-tools.ts.
// ---------------------------------------------------------------------------

function collectManageToolsActions() {
  const body = readText(MANAGE_TOOLS_PATH);
  // enum: ["list_groups", "activate", "deactivate", "reset"]
  const m = body.match(/enum:\s*\[([^\]]+)\]/);
  if (!m) {
    fail(`Could not find action enum in ${MANAGE_TOOLS_PATH}`);
    return new Set();
  }
  const actions = new Set();
  for (const raw of m[1].split(",")) {
    const s = raw.match(/"([^"]+)"/);
    if (s) actions.add(s[1]);
  }
  if (actions.size === 0) fail(`Action enum in ${MANAGE_TOOLS_PATH} parsed empty`);
  return actions;
}

// ---------------------------------------------------------------------------
// 3. Extract the agentRegistry id vocabulary from cli/src/utils/agents.ts.
//    Each entry is `{ id: "cursor", ... }`. These ids are the MCP-client ids
//    that mcpClientMapping must cover.
// ---------------------------------------------------------------------------

function collectAgentRegistryIds() {
  const body = readText(AGENTS_REGISTRY_PATH);
  const ids = new Set();
  const re = /\bid\s*:\s*"([a-z0-9-]+)"/g;
  let m;
  while ((m = re.exec(body)) !== null) {
    ids.add(m[1]);
  }
  if (ids.size === 0) {
    fail(`Could not parse agent ids from ${AGENTS_REGISTRY_PATH}`);
  }
  return ids;
}

// ---------------------------------------------------------------------------
// Audit stages
// ---------------------------------------------------------------------------

function auditSkillExists() {
  if (!existsSync(join(REPO_ROOT, SKILL_PATH))) {
    fail(`Skill missing: ${SKILL_PATH}`);
    return null;
  }
  return readText(SKILL_PATH);
}

function auditLineBudget(skillBody) {
  if (!skillBody) return;
  const lineCount = skillBody.split(/\r?\n/).length;
  if (lineCount < LINE_BUDGET_MIN || lineCount > LINE_BUDGET_MAX) {
    fail(
      `Skill line budget exceeded: ${lineCount} lines (target ${LINE_BUDGET_MIN}–${LINE_BUDGET_MAX}). ` +
        `Trim the skill or document an exception in the commit message.`,
    );
  }
}

function auditSkillToolReferences(skillBody, registeredTools) {
  if (!skillBody) return;
  // Build the set of "short names" (suffix after godot_open_mcp_) so we can
  // flag any backticked bare suffix that actually matches a real tool — that
  // is always a stale / lazy reference an agent might mistake for an MCP id.
  const shortNames = new Set();
  for (const full of registeredTools) {
    shortNames.add(full.replace(/^godot_open_mcp_/, ""));
  }
  // Backticked tool tokens: `godot_open_mcp_*`. These are the names the skill
  // teaches an agent to call — every one must be a real registered tool.
  const re = /`(godot_open_mcp_[a-z0-9_]+)`/g;
  const referenced = new Set();
  let m;
  while ((m = re.exec(skillBody)) !== null) {
    referenced.add(m[1]);
  }
  for (const name of referenced) {
    if (!registeredTools.has(name)) {
      fail(`Skill references unknown tool \`${name}\` (not in ${TOOLS_INDEX_PATH})`);
    }
  }
  // Flag bare backticked suffixes that match a real tool's short name — the
  // canonical form is the prefixed id; a bare suffix invites an agent to call
  // the wrong (un-prefixed) name.
  const bareRe = /`([a-z][a-z0-9_]+)`/g;
  while ((m = bareRe.exec(skillBody)) !== null) {
    if (shortNames.has(m[1])) {
      fail(
        `Skill uses bare tool suffix \`${m[1]}\` — use the full id \`godot_open_mcp_${m[1]}\` so agents call the right name.`,
      );
    }
  }
}

function auditSkillManageActions(skillBody, acceptedActions) {
  if (!skillBody) return;
  // Look for `action: "..."` or `action=...` references; every named action
  // must be in the shipped enum.
  const re = /action[=:]\s*"?([a-z_]+)"?/g;
  let m;
  while ((m = re.exec(skillBody)) !== null) {
    const action = m[1];
    // Skip free-text words that are not actually action ids.
    if (!["list_groups", "activate", "deactivate", "reset", "suggest", "activate_for", "intent"].includes(action)) {
      continue;
    }
    if (!acceptedActions.has(action)) {
      fail(
        `Skill references manage_tools action \`${action}\` which is not in the shipped enum (${[...acceptedActions].join(", ")}).`,
      );
    }
  }
  // Explicitly forbid the Unity-only actions even when not in an `action=` form.
  for (const forbidden of ["suggest", "activate_for", "intent"]) {
    const f = new RegExp(`\\b${forbidden}\\b`);
    if (f.test(skillBody)) {
      fail(`Skill mentions unshipped manage_tools concept \`${forbidden}\` (Unity-only).`);
    }
  }
}

function auditSkillRoutingAndRecovery(skillBody) {
  if (!skillBody) return;
  // Every "batch" mention must be paired with a nearby no-batch disclaimer.
  // A stray positive claim ("batch route is fast") fails; canonical phrasings
  // like "no `batch` route", "no headless editor batch mode", or "has no batch"
  // all pass. The window is generous (80 chars on each side) to cover the
  // common "no headless editor batch mode" wording.
  const batchMatches = Array.from(skillBody.matchAll(/\bbatch\b/gi));
  const negativePattern =
    /no\s+`?batch|has\s+no\s+`?batch|not?\s+`?batch`?\s+route|no\s+headless[^.]*\bbatch\b|\bbatch\b[^.]*\b(headless|fallback)\s+mode/i;
  for (const m of batchMatches) {
    const start = Math.max(0, m.index - 80);
    const end = Math.min(skillBody.length, m.index + 80);
    const window = skillBody.slice(start, end);
    if (!negativePattern.test(window)) {
      fail(
        `Skill mentions "batch" without a nearby no-batch disclaimer. Godot has no batch route (near: "...${window.replace(/\s+/g, " ").trim()}...").`,
      );
    }
  }
}

const FORBIDDEN_REFERENCE_PROJECTS = [
  // Source-only Godot behavior references that must never appear in user-facing
  // skill text. Keep the list alphabetized. (Unity Open MCP is allowed.)
  "godot-mcp",
];

const INTERNAL_LEAKAGE_PATTERNS = [
  /\bspecs\//, // specs/ paths
  /\bP\d+\.\d+\b/, // phase ids (P3.6, P9.1, M22)
  /\bM\d+\b/, // milestone ids
  /\broadmap\b/i, // roadmap references
  /\bexecution-plan\b/i,
  /\bporting-map\b/i,
];

const SECRET_PATTERNS = [
  /[Bb]earer\s+[A-Za-z0-9._-]{16,}/, // concrete bearer tokens
  /\/Users\/[a-z][a-z0-9._-]*\//i, // maintainer macOS home paths
  /\/home\/[a-z][a-z0-9._-]*\//i, // maintainer Linux home paths
  /[Cc]:\\[Uu]sers\\/, // maintainer Windows home paths
];

function auditSkillForbiddenContent(skillBody) {
  if (!skillBody) return;
  for (const project of FORBIDDEN_REFERENCE_PROJECTS) {
    const re = new RegExp(project.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i");
    if (re.test(skillBody)) {
      fail(`Skill leaks forbidden reference-project name: ${project}`);
    }
  }
  for (const re of INTERNAL_LEAKAGE_PATTERNS) {
    if (re.test(skillBody)) {
      fail(`Skill leaks internal reference matching ${re}`);
    }
  }
  for (const re of SECRET_PATTERNS) {
    if (re.test(skillBody)) {
      fail(`Skill contains a concrete secret/home/maintainer path matching ${re}`);
    }
  }
}

// ---------------------------------------------------------------------------
// client-paths.json audit (shape + cross-field rules)
// ---------------------------------------------------------------------------

function auditManifest(registeredAgentIds) {
  if (!existsSync(join(REPO_ROOT, MANIFEST_PATH))) {
    fail(`Client-paths manifest missing: ${MANIFEST_PATH}`);
    return null;
  }
  if (!existsSync(join(REPO_ROOT, SCHEMA_PATH))) {
    fail(`Client-paths schema missing: ${SCHEMA_PATH}`);
  }
  let manifest;
  try {
    manifest = JSON.parse(readText(MANIFEST_PATH));
  } catch (err) {
    fail(`Manifest JSON parse failed: ${err.message}`);
    return null;
  }

  // --- Shape: required root keys -------------------------------------------
  const requiredRoot = ["skillId", "templateRelativePath", "clients", "mcpClientMapping"];
  for (const key of requiredRoot) {
    if (!(key in manifest)) {
      fail(`Manifest missing required root key: ${key}`);
    }
  }
  if (typeof manifest.skillId !== "string" || manifest.skillId.length === 0) {
    fail(`Manifest skillId must be a non-empty string`);
  }
  if (typeof manifest.templateRelativePath !== "string") {
    fail(`Manifest templateRelativePath must be a string`);
  }
  if (typeof manifest.clients !== "object" || manifest.clients === null || Array.isArray(manifest.clients)) {
    fail(`Manifest clients must be an object`);
    return manifest;
  }
  if (
    typeof manifest.mcpClientMapping !== "object" ||
    manifest.mcpClientMapping === null ||
    Array.isArray(manifest.mcpClientMapping)
  ) {
    fail(`Manifest mcpClientMapping must be an object`);
    return manifest;
  }

  // --- clients.* shape + relative path constraints -------------------------
  const clientKeys = new Set(Object.keys(manifest.clients));
  const relativePaths = new Map(); // path -> client id (for duplicate detection)
  for (const [id, entry] of Object.entries(manifest.clients)) {
    if (typeof entry !== "object" || entry === null || Array.isArray(entry)) {
      fail(`clients.${id} must be an object`);
      continue;
    }
    if (!("relativePath" in entry)) {
      fail(`clients.${id} missing relativePath`);
      continue;
    }
    const rp = entry.relativePath;
    if (typeof rp !== "string" || rp.length === 0) {
      fail(`clients.${id}.relativePath must be a non-empty string`);
      continue;
    }
    if (rp.startsWith("/")) {
      fail(`clients.${id}.relativePath must be project-relative (no leading slash): ${rp}`);
    }
    if (rp.includes("..") || rp.includes("\\")) {
      fail(`clients.${id}.relativePath must use forward slashes and stay inside the project: ${rp}`);
    }
    if (!rp.endsWith(".md")) {
      fail(`clients.${id}.relativePath must end with .md: ${rp}`);
    }
    if (relativePaths.has(rp)) {
      fail(`Duplicate clients.${id}.relativePath — also used by clients.${relativePaths.get(rp)}`);
    }
    relativePaths.set(rp, id);
  }

  // --- templateRelativePath must exist on disk -----------------------------
  if (typeof manifest.templateRelativePath === "string") {
    const templateAbs = join(REPO_ROOT, manifest.templateRelativePath);
    if (!existsSync(templateAbs) || !statSync(templateAbs).isFile()) {
      fail(`templateRelativePath does not exist: ${manifest.templateRelativePath}`);
    }
  }

  // --- mcpClientMapping values must name declared clients ------------------
  for (const [mcpClientId, targets] of Object.entries(manifest.mcpClientMapping)) {
    if (!Array.isArray(targets)) {
      fail(`mcpClientMapping.${mcpClientId} must be an array`);
      continue;
    }
    if (new Set(targets).size !== targets.length) {
      fail(`mcpClientMapping.${mcpClientId} contains duplicate targets`);
    }
    for (const t of targets) {
      if (typeof t !== "string") {
        fail(`mcpClientMapping.${mcpClientId} has a non-string target: ${String(t)}`);
        continue;
      }
      if (!clientKeys.has(t)) {
        fail(
          `mcpClientMapping.${mcpClientId} -> "${t}" is not a key in clients (known: ${[...clientKeys].join(", ")})`,
        );
      }
    }
  }

  // --- mcpClientMapping keys must match the CLI agentRegistry --------------
  // Every shipped CLI agent id must appear (empty array allowed for clients
  // with no skill target). An unknown key here means the manifest drifted from
  // the registry.
  const manifestKeys = new Set(Object.keys(manifest.mcpClientMapping));
  for (const id of registeredAgentIds) {
    if (!manifestKeys.has(id)) {
      fail(
        `CLI agent id "${id}" (from ${AGENTS_REGISTRY_PATH}) is missing from mcpClientMapping. ` +
          `Add it (empty array if no skill target).`,
      );
    }
  }
  for (const id of manifestKeys) {
    if (!registeredAgentIds.has(id)) {
      fail(
        `mcpClientMapping key "${id}" is not in the CLI agentRegistry (${AGENTS_REGISTRY_PATH}). ` +
          `Remove it or add the agent to the registry.`,
      );
    }
  }

  return manifest;
}

// ---------------------------------------------------------------------------
// Driver
// ---------------------------------------------------------------------------

function main() {
  if (!existsSync(join(REPO_ROOT, TOOLS_INDEX_PATH))) {
    fail(`Tool registry missing: ${TOOLS_INDEX_PATH}`);
  }
  const registeredTools = collectRegisteredToolNames();
  if (registeredTools.size === 0) {
    fail(`No tools parsed from ${TOOLS_INDEX_PATH} / src/tools/*.ts`);
  }
  const acceptedActions = collectManageToolsActions();
  const agentIds = collectAgentRegistryIds();

  const skillBody = auditSkillExists();
  auditLineBudget(skillBody);
  auditSkillToolReferences(skillBody, registeredTools);
  auditSkillManageActions(skillBody, acceptedActions);
  auditSkillRoutingAndRecovery(skillBody);
  auditSkillForbiddenContent(skillBody);
  auditManifest(agentIds);

  // ---- Summary ------------------------------------------------------------
  const pad = (s) => String(s).padStart(3, " ");
  console.log("skill-audit:");
  console.log(`  tools registered:    ${pad(registeredTools.size)}`);
  console.log(`  manage_tools actions:${pad(acceptedActions.size)} (${[...acceptedActions].join(", ")})`);
  console.log(`  CLI agent ids:       ${pad(agentIds.size)}`);

  if (warnings.length > 0) {
    console.log(`\nwarnings (${warnings.length}):`);
    for (const w of warnings) console.log(`  - ${w}`);
  }
  if (errors.length > 0) {
    console.error(`\nFAIL — ${errors.length} check(s) failed:`);
    for (const e of errors) console.error(`  - ${e}`);
    process.exit(1);
  }
  console.log("\nOK — skill + client-paths manifest pass audit.");
}

main();
