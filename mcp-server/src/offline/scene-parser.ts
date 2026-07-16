// Bounded `.tscn` section parser (P7.2).
//
// Parses a Godot text scene (the INI-style `[gd_scene]` / `[ext_resource]` /
// `[sub_resource]` / `[node]` grammar) into a {@link ParsedTscn} WITHOUT
// loading Godot, instantiating resources, or evaluating any code. Read-only,
// per-request, no cache (per the MCP-server AGENTS.md offline-read policy).
//
// The parser is line-oriented and quote-aware: section headers carry
// `key="value"` attributes where the value may contain spaces, slashes, and
// escaped quotes, so a naive `split(" ")` is wrong. A small state-machine
// scanner tokenizes each header into attribute pairs.
//
// What is parsed:
//   - `[gd_scene load_steps=N format=M uid="uid://..."]` → scene metadata.
//     A `[gd_scene ... instance=ExtResource("...")]` marks an inherited scene
//     (we record the ref + warn; base-scene expansion is out of scope).
//   - `[ext_resource type="..." path="..." id="..."]` → keyed by id, used to
//     resolve `script = ExtResource("id")` on node bodies.
//   - `[sub_resource ...]` → recognized and skipped (P7.2 does not expand
//     sub-resources; materials/meshes are out of scope).
//   - `[node name="..." type="..." parent="..."]` → node headers; the body
//     lines up to the next header are scanned for `script = ExtResource("id")`.
//   - `[node ... instance=ExtResource("id")]` → instanced child scene; the
//     node takes its type from the instance (unknown offline → fallback type).
//
// What is deliberately NOT parsed (ignored, never evaluated):
//   - Property values other than `script = ExtResource(...)`. Godot Variant
//     evaluation is out of scope; we only need identity fields.
//   - `[connection]`, `[editable path=...]`, `[export]`, and other non-node
//     sections. They do not affect the node tree P7.2 reconstructs.
//   - `;` comment lines outside quoted strings.
//
// Greenfield fidelity: Unity's offline parser splits a YAML document stream
// into objects with fileID/classID/component-lists — none of that grammar
// applies to Godot's INI-style scene text, so the parser is written from
// scratch against the Godot `.tscn` grammar documented in the spec.

import type {
  ExternalResource,
  OfflineWarning,
  ParsedSceneNode,
  ParsedTscn,
} from "./types.js";

/** Structured parse failure. The router maps `code` to the matching offline
 *  error envelope. `line` is the 1-based line where parsing gave up. */
export interface SceneParseError {
  code: "scene_parse_error";
  message: string;
  line?: number;
}

/**
 * Parse a `.tscn` document body into a {@link ParsedTscn}. Throws
 * {@link SceneParseError} only for structural failures that make a tree
 * impossible (no `[gd_scene]` header, no nodes at all, a malformed header
 * that aborts the scan). Recoverable oddities produce warnings instead.
 *
 * The input must already be bounded (the caller enforces the byte cap before
 * reading); this function does no I/O.
 */
export function parseTscn(data: string): ParsedTscn {
  const lines = data.split("\n");
  const extResources = new Map<string, ExternalResource>();
  const nodes: ParsedSceneNode[] = [];
  const warnings: OfflineWarning[] = [];

  let format: number | null = null;
  let loadSteps: number | null = null;
  let uid: string | null = null;
  let instanceRef: string | null = null;
  let sawGdScene = false;

  // The current `[node]` body: lines accumulated between this header and the
  // next header, scanned for `script = ExtResource(...)` when the node closes.
  let currentNode: ParsedSceneNode | null = null;

  const flushNode = (): void => {
    if (currentNode === null) return;
    // The node body was already scanned line-by-line below (scriptRef set as
    // we went); nothing to do here but detach so the next header starts fresh.
    currentNode = null;
  };

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const lineNo = i + 1;
    const line = raw.replace(/\r$/, "");
    const trimmed = line.trim();

    // Blank lines and `;` comments (outside quoted strings — a `;` inside a
    // quoted attribute value is handled by the header scanner, not here, and
    // a `;` on a body line after a property is a comment Godot itself strips).
    if (trimmed === "" || trimmed.startsWith(";")) continue;

    const header = parseSectionHeader(line, lineNo, warnings);
    if (header === null) {
      // Not a section header. If we're inside a node body, scan for the one
      // property we care about: `script = ExtResource("id")`.
      if (currentNode !== null && currentNode.scriptRef === null) {
        const scriptRef = matchScriptExtResource(line);
        if (scriptRef !== null) currentNode.scriptRef = scriptRef;
      }
      // Body lines we don't care about (other properties, sub-resource
      // contents) are intentionally ignored.
      continue;
    }

    // A new section header closes the previous node body.
    flushNode();

    switch (header.section) {
      case "gd_scene": {
        if (sawGdScene) {
          warnings.push({
            code: "duplicate_gd_scene",
            message: "Duplicate [gd_scene] header; using the first.",
            line: lineNo,
          });
          break;
        }
        sawGdScene = true;
        format = numAttr(header.attrs, "format");
        loadSteps = numAttr(header.attrs, "load_steps");
        uid = header.attrs.get("uid") ?? null;
        // An inherited scene references its base via instance=ExtResource("...").
        const inst = header.attrs.get("instance");
        if (inst !== undefined) {
          instanceRef = resolveExtRefToken(inst);
          warnings.push({
            code: "unsupported_inherited_scene",
            message:
              "Scene inherits from another scene; base-scene expansion is not available offline, so only this scene's own nodes are reported.",
            line: lineNo,
          });
        }
        break;
      }
      case "ext_resource": {
        const id = header.attrs.get("id");
        const type = header.attrs.get("type") ?? "";
        const path = header.attrs.get("path") ?? "";
        if (id === undefined) {
          warnings.push({
            code: "ext_resource_missing_id",
            message: "[ext_resource] without an id attribute; skipped.",
            line: lineNo,
          });
          break;
        }
        if (extResources.has(id)) {
          // Godot does not emit duplicate ext_resource ids in a valid file;
          // first-wins is deterministic and surfaces the anomaly as a warning.
          warnings.push({
            code: "duplicate_ext_resource",
            message: `Duplicate [ext_resource] id '${id}'; keeping the first declaration.`,
            line: lineNo,
          });
          break;
        }
        extResources.set(id, { id, type, path, line: lineNo });
        break;
      }
      case "sub_resource": {
        // Recognized but skipped — P7.2 does not expand sub-resources.
        break;
      }
      case "node": {
        const name = header.attrs.get("name") ?? "";
        const typeAttr = header.attrs.get("type");
        const type = typeAttr === undefined ? null : typeAttr;
        const parentAttr = header.attrs.get("parent");
        const parent = parentAttr === undefined ? null : parentAttr;
        const ownerAttr = header.attrs.get("owner");
        const owner = ownerAttr === undefined ? null : ownerAttr;
        const inst = header.attrs.get("instance");
        const instanceRef = inst !== undefined ? resolveExtRefToken(inst) : null;
        currentNode = {
          name,
          type,
          parent,
          instanceRef,
          owner,
          scriptRef: null,
          order: nodes.length,
          line: lineNo,
        };
        nodes.push(currentNode);
        if (name === "") {
          warnings.push({
            code: "node_missing_name",
            message: `[node] at line ${lineNo} has no name attribute.`,
            line: lineNo,
          });
        }
        break;
      }
      default: {
        // `[connection]`, `[editable]`, `[export]`, and any future/unknown
        // section: recognized as a section (so it correctly closes a node
        // body) but otherwise ignored. No warning — Godot emits several
        // legitimate non-node sections.
        break;
      }
    }
  }
  flushNode();

  if (!sawGdScene) {
    throw {
      code: "scene_parse_error",
      message: "Not a Godot scene: missing [gd_scene] header.",
    } satisfies SceneParseError;
  }
  if (nodes.length === 0) {
    throw {
      code: "scene_parse_error",
      message: "Scene has no [node] declarations; cannot build a hierarchy.",
    } satisfies SceneParseError;
  }

  return {
    format,
    loadSteps,
    uid,
    instanceRef,
    extResources,
    nodes,
    warnings,
  };
}

// ---------------------------------------------------------------------------
// Header scanner — quote-aware tokenizer for `[section key="val" ...]` lines.
// ---------------------------------------------------------------------------

interface ParsedSection {
  section: string;
  attrs: Map<string, string>;
}

/**
 * If `line` is a section header (`[name attrs...]`), parse it into the section
 * name + a key→value attribute map. Returns `null` for any non-header line.
 *
 * Values are unescaped conservatively: `\"` → `"`, `\\` → `\`, `\/` → `/`.
 * Attributes without a value (bare flags) are recorded with the value `""`.
 * Malformed quotes push a warning and stop scanning that header at the break
 * (the rest of the line is left unparsed rather than guessing).
 */
function parseSectionHeader(
  line: string,
  lineNo: number,
  warnings: OfflineWarning[],
): ParsedSection | null {
  const trimmed = line.trimStart();
  if (!trimmed.startsWith("[")) return null;
  const close = trimmed.lastIndexOf("]");
  if (close < 0) {
    // A header that never closes is malformed. Report once and treat as a
    // non-header so the scanner keeps going line-by-line.
    warnings.push({
      code: "unclosed_section",
      message: `Section header is missing its closing ']' (line ${lineNo}); skipped.`,
      line: lineNo,
    });
    return null;
  }
  const body = trimmed.slice(1, close);
  // Section name = leading run of non-space chars.
  const firstSpace = body.search(/\s/);
  const section = firstSpace < 0 ? body : body.slice(0, firstSpace);
  const attrText = firstSpace < 0 ? "" : body.slice(firstSpace + 1);
  const attrs = scanAttributes(attrText, lineNo, warnings);
  return { section, attrs };
}

/**
 * Scan `key="value"` and bare `key` tokens from a header body. Quote-aware:
 * a value may contain spaces, slashes, and escaped quotes. A bare token with
 * no `=` is recorded as `key → ""` (Godot occasionally emits flags this way).
 */
function scanAttributes(
  text: string,
  lineNo: number,
  warnings: OfflineWarning[],
): Map<string, string> {
  const attrs = new Map<string, string>();
  let i = 0;
  const n = text.length;
  while (i < n) {
    // Skip whitespace between tokens.
    while (i < n && /\s/.test(text[i])) i++;
    if (i >= n) break;
    // Read the key (up to `=` or whitespace).
    const keyStart = i;
    while (i < n && text[i] !== "=" && !/\s/.test(text[i])) i++;
    const key = text.slice(keyStart, i);
    if (key === "") break;
    if (i >= n || text[i] !== "=") {
      // Bare flag token (no `=`). Record with empty value and continue.
      attrs.set(key, "");
      continue;
    }
    // Consume `=`.
    i++;
    if (text[i] === '"') {
      // Quoted value — scan to the matching close quote, honoring backslash
      // escapes so an escaped `\"` does not terminate the value prematurely.
      i++;
      let value = "";
      let closed = false;
      while (i < n) {
        const ch = text[i];
        if (ch === "\\" && i + 1 < n) {
          const next = text[i + 1];
          if (next === '"') value += '"';
          else if (next === "\\") value += "\\";
          else if (next === "/") value += "/";
          else value += next; // unknown escape: keep char literally
          i += 2;
          continue;
        }
        if (ch === '"') {
          closed = true;
          i++;
          break;
        }
        value += ch;
        i++;
      }
      if (!closed) {
        warnings.push({
          code: "unclosed_quote",
          message: `Header attribute '${key}' has an unterminated quoted value (line ${lineNo}); truncated.`,
          line: lineNo,
        });
      }
      attrs.set(key, value);
    } else {
      // Unquoted value — read to the next whitespace.
      const valStart = i;
      while (i < n && !/\s/.test(text[i])) i++;
      attrs.set(key, text.slice(valStart, i));
    }
  }
  return attrs;
}

/** Read a numeric attribute, or `null` when absent/non-numeric. */
function numAttr(attrs: Map<string, string>, key: string): number | null {
  const v = attrs.get(key);
  if (v === undefined) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

// ---------------------------------------------------------------------------
// `script = ExtResource("id")` matcher for node body lines.
// ---------------------------------------------------------------------------

/**
 * Match a `script = ExtResource("id")` line and return the id, or `null` when
 * the line is not a script-ext-resource assignment. Tolerates leading
 * whitespace and surrounding spaces around `=`. Godot writes this exact form;
 * we do not evaluate any other Variant on the line.
 */
function matchScriptExtResource(line: string): string | null {
  const m = /^\s*script\s*=\s*ExtResource\(\s*"([^"]*)"\s*\)/.exec(line);
  return m === null ? null : m[1];
}

/**
 * An `instance=` / `instance`-style attribute value can be the literal
 * `ExtResource("id")` form. Extract the id, or return the raw value when it
 * does not match (so a warning can name it). Returns `null` only when the
 * input is itself nullish — but the caller already guards that.
 */
function resolveExtRefToken(value: string): string | null {
  const m = /^ExtResource\(\s*"([^"]*)"\s*\)$/.exec(value);
  return m === null ? value : m[1];
}
