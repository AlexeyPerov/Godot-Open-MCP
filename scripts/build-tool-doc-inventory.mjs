#!/usr/bin/env node
// build-tool-doc-inventory.mjs — deterministic MCP tool inventory emitter.
//
// Emits a canonical JSON snapshot of every registered MCP tool joined with its
// route policy and visibility group, parsed straight from the source modules
// that the catalog and the router consult:
//
//   - mcp-server/src/tools/*.ts           (tool name + description + inputSchema)
//   - mcp-server/src/capabilities/route-policy.ts  (per-tool route policy)
//   - mcp-server/src/capabilities/tool-groups.ts   (per-tool visibility group)
//
// The output is consumed by scripts/check-tool-docs.mjs (the parity checker)
// and by humans who want a one-shot view of the catalog. Pure Node 18+
// (only node: builtins + regex parsing — no TS compiler, no runtime deps).
//
// Run:
//   node scripts/build-tool-doc-inventory.mjs             # pretty JSON to stdout
//   node scripts/build-tool-doc-inventory.mjs --format md # markdown table
//
// The script never edits hand-authored prose; it is read-only.

import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const TOOLS_DIR = join(REPO_ROOT, "mcp-server", "src", "tools");
const ROUTE_POLICY_PATH = join(REPO_ROOT, "mcp-server", "src", "capabilities", "route-policy.ts");
const TOOL_GROUPS_PATH = join(REPO_ROOT, "mcp-server", "src", "capabilities", "tool-groups.ts");

// ---------------------------------------------------------------------------
// Source parsers. Same approach as scripts/check-skill.mjs: read the actual
// source modules so the snapshot cannot drift from the registry.
// ---------------------------------------------------------------------------

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

function readText(abs) {
  return readFileSync(abs, "utf8");
}

/** Parse every `export const X: Tool = { name: "...", description: "...", inputSchema: {...} }`. */
function parseToolDefinitions() {
  const files = readdirRecursive(TOOLS_DIR, ".ts").filter((f) => !f.endsWith(".test.ts"));
  const tools = new Map(); // name -> { name, description, required, properties, enumFields }
  for (const file of files) {
    const body = readText(file);
    const nameRe = /\bname\s*:\s*"(godot_open_mcp_[a-z0-9_]+)"/g;
    let m;
    while ((m = nameRe.exec(body)) !== null) {
      const name = m[1];
      if (tools.has(name)) continue;
      const description = extractDescription(body, name);
      const inputSchema = extractInputSchemaBlock(body, name);
      tools.set(name, { name, description, inputSchema });
    }
  }
  return tools;
}

function extractDescription(body, name) {
  // description: "..." possibly with concatenated string literals.
  const idx = body.indexOf(`name: "${name}"`);
  if (idx < 0) return "";
  // Search forward for `description:` then concatenate adjacent string literals.
  const after = body.slice(idx);
  const descIdx = after.indexOf("description:");
  if (descIdx < 0) return "";
  let cursor = descIdx + "description:".length;
  let out = "";
  // Skip whitespace.
  while (cursor < after.length && /\s/.test(after[cursor])) cursor++;
  // Read concatenated "..." literals separated by +.
  while (cursor < after.length) {
    if (after[cursor] !== '"') break;
    // Find closing unescaped quote.
    let end = cursor + 1;
    while (end < after.length) {
      if (after[end] === "\\") { end += 2; continue; }
      if (after[end] === '"') break;
      end++;
    }
    if (end >= after.length) break;
    out += after.slice(cursor + 1, end);
    cursor = end + 1;
    // Skip whitespace, then optional +.
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

/** Best-effort extraction of inputSchema properties + required list + enum values.
 *  The catalog doc only needs property names, required fields, and enum tokens. */
function extractInputSchemaBlock(body, name) {
  const idx = body.indexOf(`name: "${name}"`);
  if (idx < 0) return { properties: [], required: [], enums: {} };
  const after = body.slice(idx);
  const schemaIdx = after.indexOf("inputSchema:");
  if (schemaIdx < 0) return { properties: [], required: [], enums: {} };
  // Locate the matching closing brace for inputSchema: { ... }.
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

  // Properties: a `properties:` block. Each entry has `<key>: { ... }`. Walk the
  // block character-by-character tracking brace depth + whether we are inside a
  // string literal so nested schema keys (`items: { type: "string" }` inside
  // `paths_hint`, etc.) and prose in description strings are not mistaken for
  // top-level property names.
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
        // Skip the string body (honor backslash escapes).
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
        // If we just entered depth 2 from depth 1, the identifier immediately
        // before the `{` (skipping `:` + whitespace) is a top-level property name.
        if (pDepth === 2) {
          let back = p - 1;
          while (back >= propsStart && /\s/.test(schemaText[back])) back--;
          if (schemaText[back] === ":") back--;
          while (back >= propsStart && /\s/.test(schemaText[back])) back--;
          // Skip the identifier backwards.
          let end = back + 1;
          while (back >= propsStart && /[a-z0-9_]/i.test(schemaText[back])) back--;
          const ident = schemaText.slice(back + 1, end);
          if (ident && /^[a-z_][a-z0-9_]*$/i.test(ident)) {
            properties.push(ident);
            // Capture the body to look for an enum.
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

  // Top-level `required: [ "a", "b" ]` — the schema's outermost required list.
  // Walk depth-tracked so a nested `required` inside `items: { ... }` (e.g. the
  // patch-element schema inside resource_create/modify.patches[]) is not pulled
  // into the top-level contract.
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
      // Find the closing `]`.
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

/** Parse the route-policy override sets straight from route-policy.ts.
 *  Returns a name→policy map; defaults to "live" for unlisted names. */
function parseRoutePolicies() {
  const body = readText(ROUTE_POLICY_PATH);
  const out = new Map();
  // Local set: `new Set([ "x", "y" ])` — and any CONST that is referenced.
  // The module declares named consts then assembles them into LOCAL_TOOLS /
  // OFFLINE_TOOLS / LIVE_FIRST_TOOLS. Easiest: scan the assignment to each
  // `const X = new Set([ ... ])` and to the named consts that fold into them.
  // We track ALL `new Set([ ... ])` arrays, then map each name to whichever
  // named set it sits in by reading the LOCAL_TOOLS / OFFLINE_TOOLS /
  // LIVE_FIRST_TOOLS declarations (including their referenced consts).

  // Step 1: collect every `const NAME = "literal";` constant reference.
  const consts = {};
  const constRe = /export\s+const\s+([A-Z_][A-Z0-9_]*)\s*=\s*"(godot_open_mcp_[a-z0-9_]+)"/g;
  let m;
  while ((m = constRe.exec(body)) !== null) {
    consts[m[1]] = m[2];
  }

  // Step 2: resolve the three override set literals.
  function resolveSet(varName, policy) {
    // Pattern: `const X = new Set([ <items> ]);` — items may be CONST names or
    // string literals.
    const re = new RegExp(`\\b${varName}\\b[^=]*=\\s*new\\s+Set\\(\\[([^\\]]+)\\]\\)`);
    const sm = body.match(re);
    if (!sm) return;
    for (const raw of sm[1].split(",")) {
      const trimmed = raw.trim();
      const lit = trimmed.match(/"([^"]+)"/);
      if (lit) {
        out.set(lit[1], policy);
        continue;
      }
      // Bare identifier — resolve through the consts map.
      const ident = trimmed.match(/^([A-Z_][A-Z0-9_]*)$/);
      if (ident && consts[ident[1]]) {
        out.set(consts[ident[1]], policy);
      }
    }
  }
  resolveSet("LOCAL_TOOLS", "local");
  resolveSet("OFFLINE_TOOLS", "offline");
  resolveSet("LIVE_FIRST_TOOLS", "live-first");
  return out;
}

/** Parse tool-groups.ts assignment tables. */
function parseToolGroups() {
  const body = readText(TOOL_GROUPS_PATH);
  const out = new Map();
  // Split on `assign(` call boundaries so backtracking can never cross into the
  // next block. A single regex over the whole file suffers catastrophic
  // backtracking across the `.map((suffix) => ...)` arrow when a second assign
  // call follows (the non-greedy `\)` expands past the assign close) — splitting
  // first sidesteps that entirely (caught when P12.1 added the tilemap assign
  // after typed-editor). Only chunks that start (after optional whitespace) with
  // a quoted group-id literal are real assign calls; this skips `assign(`
  // mentions in comments/prose. The group may be assigned via full `godot_open_mcp_*`
  // ids OR via `.map((suffix) => ...)` over short suffixes — both surface as quoted
  // literals inside the call body.
  const chunks = body.split(/\bassign\(/).slice(1);
  for (const chunk of chunks) {
    if (!/^\s*"/.test(chunk)) continue;
    const idMatch = chunk.match(/"([^"]+)"/);
    if (!idMatch) continue;
    const groupId = idMatch[1];
    // Take the chunk up to the first `);` that closes the call.
    const callEnd = chunk.indexOf(");");
    const callBody = callEnd > 0 ? chunk.slice(0, callEnd) : chunk;
    // Pull every "..." literal inside this assign() call.
    const litRe = /"([a-z0-9_]+)"/g;
    let lm;
    while ((lm = litRe.exec(callBody)) !== null) {
      const val = lm[1];
      if (val === groupId) continue;
      // If the literal is already a full tool id, register it directly;
      // otherwise treat it as a suffix (the typed-editor table uses suffixes).
      if (val.startsWith("godot_open_mcp_")) {
        out.set(val, groupId);
      } else {
        out.set(`godot_open_mcp_${val}`, groupId);
      }
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// Inventory assembly.
// ---------------------------------------------------------------------------

function buildInventory() {
  const tools = parseToolDefinitions();
  const policies = parseRoutePolicies();
  const groups = parseToolGroups();

  const entries = [];
  for (const [name, def] of [...tools.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
    const policy = policies.get(name) ?? "live";
    const group = groups.get(name) ?? null; // null = always visible meta-tool
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

function emitJson(entries) {
  const out = {
    schemaVersion: 1,
    generatedAt: new Date().toISOString().split("T")[0], // date-only for determinism
    count: entries.length,
    entries,
  };
  console.log(JSON.stringify(out, null, 2));
}

function emitMarkdown(entries) {
  console.log("| Tool | Family | Route | Visibility | Required input | Notes |");
  console.log("|---|---|---|---|---|---|");
  for (const e of entries) {
    const family = e.group ?? "core (meta)";
    const visibility = e.group ?? "always visible";
    const req = e.requiredInput.length > 0 ? e.requiredInput.join(", ") : "—";
    console.log(
      `| \`${e.name}\` | ${family} | ${e.routePolicy} | ${visibility} | ${req} | |`,
    );
  }
}

const format = process.argv.includes("--format") ? process.argv[process.argv.indexOf("--format") + 1] : "json";
const entries = buildInventory();
if (format === "md") emitMarkdown(entries);
else emitJson(entries);
