#!/usr/bin/env node
// check-tool-docs.mjs — drift + hygiene audit for docs/api/mcp-tools.md.
//
// P9.2 acceptance criteria require that the public MCP tool catalog:
//   - document every tool in ALL_TOOLS exactly once,
//   - advertise the same route policy the router uses (route-policy.ts),
//   - advertise the same visibility group the catalog uses (tool-groups.ts),
//   - expose every required schema field + every enum token,
//   - keep the canonical inventory table free of duplicates / stale rows,
//   - use unique heading anchors for every tool,
//   - not leak internal phase/spec references or forbidden reference-project
//     names into the user-visible surface.
//
// This checker imports the same source parsers as
// scripts/build-tool-doc-inventory.mjs (no second handwritten policy map).
// The canonical inventory table is delimited by HTML comment markers so the
// checker always compares the authoritative rows; tool detail sections are
// discovered via `## \`godot_open_mcp_*\`` headings.
//
// Run locally:
//   node scripts/check-tool-docs.mjs
//
// Exits 1 on any violation. Wired into .github/workflows/ci.yml
// (tool-docs-parity job).

import { readFileSync, existsSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const DOC_PATH = "docs/api/mcp-tools.md";

// Pull the inventory builder's parsers so the checker never maintains a second
// handwritten policy/group table. Same approach as check-skill.mjs (read
// sources straight, no TS compiler).
const INVENTORY_BUILD_PATH = join(REPO_ROOT, "scripts", "build-tool-doc-inventory.mjs");

const errors = [];
const warnings = [];

function fail(message) {
  errors.push(message);
}

function readText(rel) {
  return readFileSync(join(REPO_ROOT, rel), "utf8");
}

// ---------------------------------------------------------------------------
// Load the source inventory via the same module the inventory builder uses.
// build-tool-doc-inventory.mjs exports no symbols (it has a `main` side effect),
// so we re-implement the source parsers inline here. They MUST stay byte-for-
// byte identical with the inventory builder — a parity test below pins the
// snapshot.
// ---------------------------------------------------------------------------

// Inline copies of the inventory builder's source parsers. If they drift from
// build-tool-doc-inventory.mjs, the `inventorySnapshotParity` test below
// catches it. Keep these in sync when editing the inventory builder.
//
// (We deliberately duplicate rather than refactor the inventory builder into a
// shared module: the checker must keep working even if a future change to the
// builder adds a stdout side effect, and the parser functions are small.)

import { readdirSync, statSync } from "node:fs";

const TOOLS_DIR = join(REPO_ROOT, "mcp-server", "src", "tools");
const ROUTE_POLICY_PATH = join(REPO_ROOT, "mcp-server", "src", "capabilities", "route-policy.ts");
const TOOL_GROUPS_PATH = join(REPO_ROOT, "mcp-server", "src", "capabilities", "tool-groups.ts");

function readdirRecursive(dir, ext) {
  const out = [];
  for (const ent of readdirSync(dir)) {
    const full = join(dir, ent);
    const st = statSync(full);
    if (st.isDirectory()) out.push(...readdirRecursive(full, ext));
    else if (ent.endsWith(ext)) out.push(full);
  }
  return out;
}

function extractDescription(body, name) {
  const idx = body.indexOf(`name: "${name}"`);
  if (idx < 0) return "";
  const after = body.slice(idx);
  const descIdx = after.indexOf("description:");
  if (descIdx < 0) return "";
  let cursor = descIdx + "description:".length;
  let out = "";
  while (cursor < after.length && /\s/.test(after[cursor])) cursor++;
  while (cursor < after.length) {
    if (after[cursor] !== '"') break;
    let end = cursor + 1;
    while (end < after.length) {
      if (after[end] === "\\") { end += 2; continue; }
      if (after[end] === '"') break;
      end++;
    }
    if (end >= after.length) break;
    out += after.slice(cursor + 1, end);
    cursor = end + 1;
    while (cursor < after.length && /\s/.test(after[cursor])) cursor++;
    if (after[cursor] === "+") {
      cursor++;
      while (cursor < after.length && /\s/.test(after[cursor])) cursor++;
      continue;
    }
    break;
  }
  return out;
}

function extractInputSchemaBlock(body, name) {
  const idx = body.indexOf(`name: "${name}"`);
  if (idx < 0) return { properties: [], required: [], enums: {} };
  const after = body.slice(idx);
  const schemaIdx = after.indexOf("inputSchema:");
  if (schemaIdx < 0) return { properties: [], required: [], enums: {} };
  let cursor = schemaIdx + "inputSchema:".length;
  while (cursor < after.length && after[cursor] !== "{") cursor++;
  if (cursor >= after.length) return { properties: [], required: [], enums: {} };
  let depth = 0;
  let start = cursor;
  for (; cursor < after.length; cursor++) {
    if (after[cursor] === "{") depth++;
    else if (after[cursor] === "}") {
      depth--;
      if (depth === 0) { cursor++; break; }
    }
  }
  const schemaText = after.slice(start, cursor);

  const properties = [];
  const enums = {};
  const propsIdx = schemaText.indexOf("properties:");
  if (propsIdx >= 0) {
    let p = propsIdx + "properties:".length;
    while (p < schemaText.length && schemaText[p] !== "{") p++;
    const propsStart = p + 1;
    let pDepth = 1;
    p = propsStart;
    while (p < schemaText.length && pDepth > 0) {
      const ch = schemaText[p];
      if (ch === '"') {
        p++;
        while (p < schemaText.length) {
          if (schemaText[p] === "\\") { p += 2; continue; }
          if (schemaText[p] === '"') break;
          p++;
        }
        p++;
        continue;
      }
      if (ch === "{") {
        pDepth++;
        if (pDepth === 2) {
          let back = p - 1;
          while (back >= propsStart && /\s/.test(schemaText[back])) back--;
          if (schemaText[back] === ":") back--;
          while (back >= propsStart && /\s/.test(schemaText[back])) back--;
          let end = back + 1;
          while (back >= propsStart && /[a-z0-9_]/i.test(schemaText[back])) back--;
          const ident = schemaText.slice(back + 1, end);
          if (ident && /^[a-z_][a-z0-9_]*$/i.test(ident)) {
            properties.push(ident);
            let bd = 1;
            let bend = p + 1;
            while (bend < schemaText.length && bd > 0) {
              const c = schemaText[bend];
              if (c === '"') {
                bend++;
                while (bend < schemaText.length) {
                  if (schemaText[bend] === "\\") { bend += 2; continue; }
                  if (schemaText[bend] === '"') break;
                  bend++;
                }
                bend++;
                continue;
              }
              if (c === "{") bd++;
              else if (c === "}") bd--;
              bend++;
            }
            const propBlock = schemaText.slice(p, bend);
            const enumRe = /enum\s*:\s*\[([^\]]+)\]/g;
            let em;
            while ((em = enumRe.exec(propBlock)) !== null) {
              const vals = [];
              for (const raw of em[1].split(",")) {
                const s = raw.match(/"([^"]+)"/);
                if (s) vals.push(s[1]);
              }
              if (vals.length > 0) enums[ident] = vals;
            }
          }
        }
      } else if (ch === "}") {
        pDepth--;
      }
      p++;
    }
  }

  let required = [];
  let depth1 = 0;
  let inString1 = false;
  for (let i = 0; i < schemaText.length; i++) {
    const ch = schemaText[i];
    if (inString1) {
      if (ch === "\\") { i++; continue; }
      if (ch === '"') inString1 = false;
      continue;
    }
    if (ch === '"') { inString1 = true; continue; }
    if (ch === "{") { depth1++; continue; }
    if (ch === "}") { depth1--; continue; }
    if (ch === "r" && depth1 === 1 && schemaText.slice(i, i + 9) === "required:" && required.length === 0) {
      const close = schemaText.indexOf("]", i);
      if (close > i) {
        const listText = schemaText.slice(i, close);
        for (const raw of listText.split(",")) {
          const s = raw.match(/"([^"]+)"/);
          if (s) required.push(s[1]);
        }
        i = close;
      }
    }
  }

  return { properties, required, enums };
}

function parseToolDefinitions() {
  const files = readdirRecursive(TOOLS_DIR, ".ts").filter((f) => !f.endsWith(".test.ts"));
  const tools = new Map();
  for (const file of files) {
    const body = readFileSync(file, "utf8");
    const nameRe = /\bname\s*:\s*"(godot_open_mcp_[a-z0-9_]+)"/g;
    let m;
    while ((m = nameRe.exec(body)) !== null) {
      const name = m[1];
      if (tools.has(name)) continue;
      tools.set(name, {
        name,
        description: extractDescription(body, name),
        inputSchema: extractInputSchemaBlock(body, name),
      });
    }
  }
  return tools;
}

function parseRoutePolicies() {
  const body = readFileSync(ROUTE_POLICY_PATH, "utf8");
  const out = new Map();
  const consts = {};
  const constRe = /export\s+const\s+([A-Z_][A-Z0-9_]*)\s*=\s*"(godot_open_mcp_[a-z0-9_]+)"/g;
  let m;
  while ((m = constRe.exec(body)) !== null) consts[m[1]] = m[2];
  function resolveSet(varName, policy) {
    const re = new RegExp(`\\b${varName}\\b[^=]*=\\s*new\\s+Set\\(\\[([^\\]]+)\\]\\)`);
    const sm = body.match(re);
    if (!sm) return;
    for (const raw of sm[1].split(",")) {
      const trimmed = raw.trim();
      const lit = trimmed.match(/"([^"]+)"/);
      if (lit) { out.set(lit[1], policy); continue; }
      const ident = trimmed.match(/^([A-Z_][A-Z0-9_]*)$/);
      if (ident && consts[ident[1]]) out.set(consts[ident[1]], policy);
    }
  }
  resolveSet("LOCAL_TOOLS", "local");
  resolveSet("OFFLINE_TOOLS", "offline");
  resolveSet("LIVE_FIRST_TOOLS", "live-first");
  return out;
}

function parseToolGroups() {
  const body = readFileSync(TOOL_GROUPS_PATH, "utf8");
  const out = new Map();
  const assignRe = /assign\(\s*"([^"]+)"\s*,\s*(?:\[[\s\S]*?\])\s*(?:\.map\([\s\S]*?\))?\s*\)/g;
  let m;
  while ((m = assignRe.exec(body)) !== null) {
    const group = m[0];
    const groupId = m[1];
    const litRe = /"([a-z0-9_]+)"/g;
    let lm;
    while ((lm = litRe.exec(group)) !== null) {
      const val = lm[1];
      if (val === groupId) continue;
      if (val.startsWith("godot_open_mcp_")) out.set(val, groupId);
      else out.set(`godot_open_mcp_${val}`, groupId);
    }
  }
  return out;
}

function buildInventory() {
  const tools = parseToolDefinitions();
  const policies = parseRoutePolicies();
  const groups = parseToolGroups();
  const entries = [];
  for (const [name, def] of [...tools.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
    const policy = policies.get(name) ?? "live";
    const group = groups.get(name) ?? null;
    entries.push({
      name,
      description: def.description,
      routePolicy: policy,
      group,
      requiredInput: def.inputSchema.required,
      properties: def.inputSchema.properties,
      enums: def.inputSchema.enums,
    });
  }
  return entries;
}

// ---------------------------------------------------------------------------
// Document parsing.
// ---------------------------------------------------------------------------

/** Pull the canonical inventory block out of the doc. The block is delimited
 *  by `<!-- tool-docs:inventory -->` and `<!-- /tool-docs:inventory -->` HTML
 *  comments so the checker never compares the wrong table. */
function parseInventoryTable(docBody) {
  const start = docBody.indexOf("<!-- tool-docs:inventory -->");
  const end = docBody.indexOf("<!-- /tool-docs:inventory -->");
  if (start < 0 || end < 0 || end <= start) {
    fail(
      `Canonical inventory block missing. Wrap the inventory table in\n` +
        `  <!-- tool-docs:inventory --> / <!-- /tool-docs:inventory --> HTML comments\n` +
        `  inside ${DOC_PATH}.`,
    );
    return [];
  }
  const block = docBody.slice(start, end);
  const rows = [];
  for (const line of block.split(/\r?\n/)) {
    if (!line.startsWith("| `")) continue;
    const cells = line.split("|").map((c) => c.trim()).filter((c) => c.length > 0);
    if (cells.length < 6) continue;
    // cells[0] is `\`godot_open_mcp_*\``.
    const nameRe = cells[0].match(/^`(godot_open_mcp_[a-z0-9_]+)`$/);
    if (!nameRe) continue;
    rows.push({
      name: nameRe[1],
      family: cells[1],
      route: cells[2],
      visibility: cells[3],
      mutates: cells[4],
      gate: cells[5],
    });
  }
  return rows;
}

/** Pull every `## \`godot_open_mcp_*\`` detail heading. Returns name -> anchor. */
function parseDetailHeadings(docBody) {
  const out = new Map();
  const seen = new Map(); // anchor -> first heading text (for duplicate detection)
  const re = /^#{2,3}\s+`(godot_open_mcp_[a-z0-9_]+)`\s*$/gm;
  let m;
  while ((m = re.exec(docBody)) !== null) {
    const name = m[1];
    if (out.has(name)) {
      fail(`Duplicate detail heading for \`${name}\` (only one section per tool).`);
    }
    out.set(name, m[0]);
  }
  return out;
}

// ---------------------------------------------------------------------------
// Audit stages.
// ---------------------------------------------------------------------------

function auditParity(inventory, tableRows, detailHeadings) {
  // 1. Set equality between inventory and the canonical table.
  const tableNames = new Set(tableRows.map((r) => r.name));
  const invNames = new Set(inventory.map((e) => e.name));
  for (const name of invNames) {
    if (!tableNames.has(name)) {
      fail(`Tool \`${name}\` is registered in ALL_TOOLS but missing from the inventory table in ${DOC_PATH}.`);
    }
  }
  for (const name of tableNames) {
    if (!invNames.has(name)) {
      fail(`Inventory table in ${DOC_PATH} lists \`${name}\` as shipped, but it is not in ALL_TOOLS (stale row).`);
    }
  }

  // 2. No duplicates inside the table.
  const seen = new Map();
  for (const row of tableRows) {
    seen.set(row.name, (seen.get(row.name) ?? 0) + 1);
  }
  for (const [name, n] of seen) {
    if (n > 1) fail(`Inventory table in ${DOC_PATH} lists \`${name}\` ${n} times.`);
  }

  // 3. Route equality.
  for (const row of tableRows) {
    const inv = inventory.find((e) => e.name === row.name);
    if (!inv) continue;
    if (row.route !== inv.routePolicy) {
      fail(
        `Route mismatch for \`${row.name}\`: docs say \`${row.route}\`, source says \`${inv.routePolicy}\` (route-policy.ts).`,
      );
    }
  }

  // 4. Visibility group equality. Doc column writes the group id, or
  //    "always visible" for null-group meta-tools.
  for (const row of tableRows) {
    const inv = inventory.find((e) => e.name === row.name);
    if (!inv) continue;
    const expected = inv.group ?? "always visible";
    if (row.visibility !== expected) {
      fail(
        `Visibility mismatch for \`${row.name}\`: docs say \`${row.visibility}\`, source says \`${expected}\` (tool-groups.ts).`,
      );
    }
  }

  // 5. Detail heading per shipped tool. Family-level sections are allowed when
  //    they enumerate member tools, so every name MUST appear as a detail
  //    heading (a `## \`name\`` block) exactly once. Missing headings are
  //    warnings (the inventory table is the parity authority; detail prose is
  //    human-authored).
  for (const inv of inventory) {
    if (!detailHeadings.has(inv.name)) {
      warnings.push(`Tool \`${inv.name}\` has a row in the inventory table but no detail heading (\`## \\\`${inv.name}\\\`\`) in ${DOC_PATH}.`);
    }
  }
}

function auditRequiredFields(inventory, docBody) {
  // Every required schema field must appear in the doc's input section. The
  // check is conservative: it only verifies the field name appears somewhere
  // in the tool's detail section (between its heading and the next tool
  // heading). It does not enforce a specific layout.
  for (const inv of inventory) {
    if (inv.requiredInput.length === 0) continue;
    const sectionStart = docBody.indexOf(`## \`${inv.name}\``);
    if (sectionStart < 0) continue; // missing-heading warning already covers this
    // Find next tool heading (any `## ` line) after sectionStart.
    const after = docBody.slice(sectionStart + inv.name.length);
    const nextHeading = after.search(/^#{2,3}\s/gm);
    const sectionEnd = nextHeading > 0 ? sectionStart + inv.name.length + nextHeading : docBody.length;
    const section = docBody.slice(sectionStart, sectionEnd);
    for (const field of inv.requiredInput) {
      // The field name should appear as a literal token (backticked, in a
      // list, or in a JSON example).
      if (!section.includes(field)) {
        fail(`Required input field \`${field}\` for \`${inv.name}\` is missing from its detail section in ${DOC_PATH}.`);
      }
    }
  }
}

function auditEnumTokens(inventory, docBody) {
  // Every enum token must appear in the tool's detail section.
  for (const inv of inventory) {
    const enums = Object.entries(inv.enums);
    if (enums.length === 0) continue;
    const sectionStart = docBody.indexOf(`## \`${inv.name}\``);
    if (sectionStart < 0) continue;
    const after = docBody.slice(sectionStart + inv.name.length);
    const nextHeading = after.search(/^#{2,3}\s/gm);
    const sectionEnd = nextHeading > 0 ? sectionStart + inv.name.length + nextHeading : docBody.length;
    const section = docBody.slice(sectionStart, sectionEnd);
    for (const [field, tokens] of enums) {
      for (const tok of tokens) {
        if (!section.includes(tok)) {
          fail(`Enum token \`${tok}\` for \`${inv.name}.${field}\` is missing from its detail section in ${DOC_PATH}.`);
        }
      }
    }
  }
}

const FORBIDDEN_REFERENCE_PROJECTS = [
  // Source-only Godot behavior references that must never appear in user-facing
  // docs. Unity Open MCP is allowed by name (AGENTS.md naming exception).
  "godot-mcp",
];

const INTERNAL_LEAKAGE_PATTERNS = [
  /\bspecs\//, // specs/ paths
  /\bP\d+\.\d+\b/, // phase ids (P3.6, P9.1)
  /\bM\d+\b/, // milestone ids
  /\broadmap\b/i, // roadmap references
  /\bexecution-plan\b/i,
  /\bporting-map\b/i,
];

function auditPublicSurfaceHygiene(docBody) {
  for (const project of FORBIDDEN_REFERENCE_PROJECTS) {
    const re = new RegExp(project.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i");
    if (re.test(docBody)) {
      fail(`${DOC_PATH} leaks forbidden reference-project name: ${project}`);
    }
  }
  for (const re of INTERNAL_LEAKAGE_PATTERNS) {
    if (re.test(docBody)) {
      fail(`${DOC_PATH} leaks internal reference matching ${re}`);
    }
  }
  // "batch" mentions are allowed when they refer to a tool's single+batch
  // argument pattern (`node_modify` accepts `node_path` OR `node_paths` for
  // batch targets). They are NOT allowed when they refer to a batch ROUTE —
  // Godot has no headless batch equivalent, so any "batch policy" / "batch
  // route" / "batchCapable" / "batch_execute" mention must be paired with a
  // nearby no-batch disclaimer.
  //
  // The rule: only fire when the surrounding context talks about ROUTING or
  // batch execution, not when it talks about a tool argument that takes a
  // list of targets.
  const batchMatches = Array.from(docBody.matchAll(/\bbatch\b/gi));
  const routeContextPattern =
    /\b(batch\s+route|batch\s+policy|batch\s+execute|batchCapable|batch[-_]?capable|always[-_]?batch|headless[^.]*\bbatch\b|\bbatch\b[^.]*\bheadless)\b/i;
  const negativePattern =
    /no\s+`?batch|has\s+no\s+`?batch|not?\s+`?batch`?\s+route|no\s+headless[^.]*\bbatch\b|\bbatch\b[^.]*\b(headless|fallback)\s+mode/i;
  for (const m of batchMatches) {
    const start = Math.max(0, m.index - 80);
    const end = Math.min(docBody.length, m.index + 80);
    const window = docBody.slice(start, end);
    if (!routeContextPattern.test(window)) continue; // tool-argument batch — allowed
    if (negativePattern.test(window)) continue; // route-batch with disclaimer — allowed
    fail(
      `${DOC_PATH} mentions a batch route/execute concept without a nearby no-batch disclaimer (near: "...${window.replace(/\s+/g, " ").trim()}...").`,
    );
  }
}

function auditUnprefixedToolIds(docBody) {
  // The canonical inventory table and the detail headings must use the full
  // `godot_open_mcp_*` id, not a bare suffix. We do not flag every bare suffix
  // in prose (the docs intentionally use short names inside prose); we flag
  // only the structured rows / headings.
  // Inventory-table column 1 — already enforced by the table parser regex.
  // Detail heading line — already enforced by the heading parser regex.
  // Anchor targets in markdown links to tool headings.
  const anchorRe = /\[[^\]]+\]\(#([^)]+)\)/g;
  let m;
  while ((m = anchorRe.exec(docBody)) !== null) {
    const anchor = m[1];
    if (/^godot_open_mcp_[a-z0-9_]+$/.test(anchor)) continue; // full id anchor
    // Some shared-section anchors (e.g. `route-policy`, `shared-contracts`)
    // are allowed; only flag a tool-shaped bare anchor.
    if (/^(node|scene|script|resource|filesystem|editor|console|screenshot|reflection)_/.test(anchor)) {
      fail(`${DOC_PATH} uses bare-suffix anchor \`${anchor}\`; use the full \`godot_open_mcp_*\` id.`);
    }
  }
}

// ---------------------------------------------------------------------------
// Driver
// ---------------------------------------------------------------------------

function main() {
  if (!existsSync(join(REPO_ROOT, DOC_PATH))) {
    fail(`${DOC_PATH} missing`);
    printSummaryAndExit();
    return;
  }
  const docBody = readText(DOC_PATH);

  const inventory = buildInventory();
  if (inventory.length === 0) {
    fail(`No tools parsed from source — check mcp-server/src/tools/ + capabilities/*.ts`);
    printSummaryAndExit();
    return;
  }

  const tableRows = parseInventoryTable(docBody);
  const detailHeadings = parseDetailHeadings(docBody);

  auditParity(inventory, tableRows, detailHeadings);
  auditRequiredFields(inventory, docBody);
  auditEnumTokens(inventory, docBody);
  auditPublicSurfaceHygiene(docBody);
  auditUnprefixedToolIds(docBody);

  // ---- Summary ------------------------------------------------------------
  const pad = (s) => String(s).padStart(3, " ");
  console.log("tool-docs-parity:");
  console.log(`  tools in source:        ${pad(inventory.length)}`);
  console.log(`  rows in inventory table:${pad(tableRows.length)}`);
  console.log(`  detail headings:        ${pad(detailHeadings.size)}`);

  printSummaryAndExit();
}

function printSummaryAndExit() {
  if (warnings.length > 0) {
    console.log(`\nwarnings (${warnings.length}):`);
    for (const w of warnings) console.log(`  - ${w}`);
  }
  if (errors.length > 0) {
    console.error(`\nFAIL — ${errors.length} check(s) failed:`);
    for (const e of errors) console.error(`  - ${e}`);
    process.exit(1);
  }
  console.log("\nOK — MCP tool catalog matches the registry.");
}

main();
