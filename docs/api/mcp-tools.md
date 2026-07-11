# MCP tool catalog

This document covers the MCP tool catalog, the tool families, and the route policy each tool follows. For the bridge HTTP contract (`/ping`, `/tools/*`), see `bridge-http.md`.

## Tool inventory

Every tool is defined in `mcp-server/src/tools/{name}.ts` and registered in `ALL_TOOLS` (`mcp-server/src/tools/index.ts`). The bridge-side handlers live in `packages/bridge/Editor/Tools/` and `packages/bridge/Editor/MetaTools/`; the MCP-side `LiveClient` routes each CallTool to `POST /tools/{name}` on the resolved bridge port.

Tool names follow the `godot_open_mcp_*` convention (ADR-003).

### Tool families

| Family | Tools | Mutating | Notes |
|---|---|---|---|
| core | `ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`, `capabilities` | `apply_fix` only | Always visible in `ListTools`. |
| node | `node_find`, `node_create`, `node_modify`, `node_set_parent`, `node_duplicate`, `node_delete` | create/modify/set-parent/duplicate/delete | Scene-tree operations. |
| scene | `scene_open`, `scene_save`, `scene_list_opened`, `scene_get_data`, `scene_create` | open/save/create | Scene lifecycle + read. |
| resource | `resource_find`, `resource_get_data`, `resource_create`, `resource_modify` | create/modify | `.tres`/`.res` discovery + bounded property inspection (read-only) + gated create/modify (P4.2). |

## Route policy

| Route | Meaning | Tools |
|---|---|---|
| **live** | The CallTool handler POSTs to the bridge; the bridge handler runs on the editor main thread. | Most tools (node, scene, gate meta-tools, `apply_fix`). |
| **local** | The CallTool handler resolves the response in-process — no bridge hop. | `godot_open_mcp_capabilities`. |

`capabilities` is the one local-only tool: its response is built in-process from `ALL_TOOLS` + the rule/fix catalog via `buildCapabilities`. It never POSTs to the bridge (mirroring Unity Open MCP's tool-router `routeCapabilities`).

## `godot_open_mcp_capabilities`

Discover the full capability surface in one call.

**Input:**
- `kind` (optional) — `tools` | `rules` | `fixes`. Filter to a single surface; omit for all three.
- `include_planned` (optional, default `true`) — include planned-but-unbuilt capabilities (`status: "planned"`). The v1 catalog has no planned entries yet.

**Result:**

```json
{
  "tools": [{ "name": "godot_open_mcp_ping", "implemented": true, "status": "implemented", "description": "Bridge health check." }],
  "rules": [{
    "id": "missing_scripts",
    "title": "Missing scripts",
    "description": "...",
    "applicableAssetKinds": ["scene", "resource"],
    "applicableExtensions": [".tscn", ".tres"],
    "implemented": true,
    "status": "implemented",
    "issues": [{ "code": "missing_script", "severity": "Error", "fixIds": ["remove_missing_script"] }]
  }],
  "fixes": [{
    "id": "remove_missing_script",
    "implemented": true,
    "status": "implemented",
    "rules": ["missing_scripts"],
    "issueCodes": ["missing_script"],
    "safe": true
  }],
  "counts": { "toolsImplemented": 17, "toolsPlanned": 0, "rulesImplemented": 3, "rulesPlanned": 0, "fixesImplemented": 1, "fixesPlanned": 0 }
}
```

Each capability carries an `implemented` boolean; planned-but-unbuilt items return with `status: "planned"` and actionable `guidance` instead of failing.

### Rule + fix catalog

The `rules[]` and `fixes[]` arrays mirror the C# verify package and MUST stay in sync on every rule/fix change (per `packages/verify/AGENTS.md` §Capability catalog sync). The drift-detection tests in `mcp-server/src/capabilities/rule-catalog.test.ts` pin the issue codes, severities, and fix mappings against the C# constants:

| Rule id | Issue code | Severity | Fix |
|---|---|---|---|
| `broken_references` | `broken_scene_reference` | Error | — |
| `missing_scripts` | `missing_script` | Error | `remove_missing_script` |
| `import_health` | `orphan_import` | Warning | — |
| `import_health` | `duplicate_uid` | Error | — |

| Fix id | Resolves | Safe |
|---|---|---|
| `remove_missing_script` | `missing_scripts` / `missing_script` | true |

The catalog source of truth is `mcp-server/src/capabilities/rule-catalog.ts`; the builder is `mcp-server/src/capabilities/build-capabilities.ts`. There is no planned-rule surface yet — when a rule is stubbed but not built, add it with `implemented:false` + `guidance` so agents get a structured "not yet available" signal.

## `godot_open_mcp_resource_find`

Find Godot resources (`.tres`/`.res`) in the project's `res://` filesystem. Read-only (gate-free). Two modes: direct lookup (by `uid` or `resource_path`) resolves a single resource; indexed type search (by `type_filter`) recursively scans `EditorFileSystem` for files whose importer-assigned type equals or derives from the filter — without eagerly loading every candidate.

**Selector precedence:** `uid` > `resource_path` > `type_filter`. A direct selector resolves at most one resource and ignores search-only options. At least one selector is required.

**Input:**
- `uid` (optional) — priority 1: `uid://` identifier, resolved via `ResourceUid`.
- `resource_path` (optional) — priority 2: exact `res://` path (also accepts a `uid://`, mapped first).
- `type_filter` (optional) — Godot class/type name for the indexed scan (`ClassDB.IsParentClass`).
- `directory` (optional, default `res://`) — `res://` directory scope for the scan.
- `page_size` (optional, default 50, max 200) — search-mode result bound.
- `cursor` (optional) — opaque continuation cursor from a previous `pagination.nextCursor`.

**Result (direct lookup):**

```json
{
  "count": 1,
  "resources": [{ "resourcePath": "res://materials/wood.tres", "uid": "uid://abc", "type": "StandardMaterial3D" }],
  "pagination": { "nextCursor": null, "hasMore": false }
}
```

**Result (type search):** same shape, plus a top-level `total` (the full match count before paging).

**Errors:** `invalid_request` (no selector), `invalid_path` (bad scheme/extension/traversal/directory), `resource_not_found` (path/uid does not resolve), `filesystem_unavailable` (editor filesystem not ready).

## `godot_open_mcp_resource_get_data`

Load a Godot resource (`.tres`/`.res`) and return a bounded, cycle-safe property tree. Read-only (gate-free). Object references that would create a cycle or an unbounded graph are represented by a descriptive reference leaf (`res://` path + `uid` when available), never blindly traversed — process-local instance IDs are not exposed as durable identity.

**Input:**
- `resource_path` (required) — canonical `res://` path or `uid://` (mapped first). Must end in `.tres`/`.res`.
- `profile` (optional, default `compact`) — `compact` | `balanced` | `full`. Controls recursion depth (compact = top-level only; balanced = one level of nesting; full = whole bounded tree, hard cap depth 6).
- `property_path` (optional) — slash-separated drill-down (e.g. `albedo_color`); serializes just that subtree.
- `max_depth` (optional, [0, 6]) — overrides the profile default depth.
- `collection_page_size` (optional, default 50, max 200) — max items per Array/Dictionary before clipping.
- `cursor` (optional) — continuation cursor for large child collections (future use).

**Result:**

```json
{
  "identity": { "resourcePath": "res://materials/wood.tres", "uid": "uid://abc", "type": "StandardMaterial3D" },
  "profile": "compact",
  "maxDepth": 0,
  "properties": [
    { "name": "resource_name", "variantType": "String", "value": "Wood", "children": null, "referenceDescription": null, "truncationReason": null },
    { "name": "albedo_color", "variantType": "Color", "value": "[1,0,0,1]", "children": null, "referenceDescription": null, "truncationReason": null }
  ],
  "truncation": { "truncated": false, "truncationReasons": [] }
}
```

Each property node carries `name`, `variantType`, `value` (scalar/JSON-raw/`null`), `children` (nested resources, arrays, dictionaries — `null` for leaves), `referenceDescription` (non-traversed object reference), and `truncationReason` (per-node clip reason). `truncation.truncated` is `true` when any node, collection, or string was clipped.

**Errors:** `missing_parameter` (no `resource_path`), `invalid_path` (bad scheme/extension), `resource_not_found` (path/uid does not resolve), `resource_load_failed` (`ResourceLoader.Load` fails), `serialization_failed` (property access/serialization fails safely).

**Tip:** prefer `compact` first, then drill in with `property_path` or a higher `max_depth`. `full` can be expensive on large resources.

## `godot_open_mcp_resource_create`

Create a new Godot resource (`.tres`/`.res`) by instantiating a `Resource` subclass via `ClassDB` and persisting it through `ResourceSaver`. Mutating (default gate `enforce`). The destination must not already exist — no overwrite. Optional initial properties are validated and applied before the first save (all-or-nothing; a single bad patch aborts the create with no file written).

**Input:**
- `resource_path` (required) — new `res://` destination, must end in `.tres`/`.res`, must not exist.
- `type_class_name` (optional, default `Resource`) — instantiable `Resource` subclass (e.g. `StandardMaterial3D`, `FastNoiseLite`, `Gradient`). Validated via `ClassDB`: must exist, be instantiable, and inherit `Resource`.
- `properties` (optional) — array of `{ path, value }` initial property assignments (see patch grammar below).
- `paths_hint` (required) — mutation scope; must contain `resource_path`. Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Property-path grammar** (shared with `resource_modify`):
- `property` — set a top-level property.
- `nested/property` — traverse a sub-resource, then set.
- `array/[0]` — set an array element by index (the array grows to fit).
- `dictionary/[key]` — set a dictionary value by string key.

The first segment must be a property name. Values are raw JSON converted to the property's `Variant.Type`: `bool`, `int`, `float`, `string`, vectors as `[x,y,...]`, colors as `[r,g,b,a]`, resource refs as `{"resource_path":"res://..."}` , or `null` to clear.

**Result:**

```json
{
  "resource": { "resourcePath": "res://materials/wood.tres", "uid": "uid://abc", "type": "StandardMaterial3D" },
  "changed": ["albedo_color"],
  "unchanged": [],
  "saved": true
}
```

`changed` / `unchanged` list property paths that actually changed vs matched the default. The standard `gate` block (mode, outcome, ran, failed, delta, agentNextSteps) is prepended into `result`.

**Errors:** `missing_parameter` (no `resource_path`), `paths_hint_required` (missing/empty/incomplete `paths_hint`), `invalid_path` (bad scheme/extension/traversal), `resource_exists` (destination already exists), `resource_type_invalid` (class missing/abstract/non-instantiable/not a `Resource`), `patch_invalid` (bad path, duplicate path, unknown property, bad index/key), `value_type_mismatch` (value cannot convert to the target type), `no_changes` (all initial properties already match), `resource_save_failed` (`ResourceSaver.Save` returns non-OK), `filesystem_unavailable`.

## `godot_open_mcp_resource_modify`

Modify writable properties of an existing Godot resource (`.tres`/`.res`) through explicit property-path assignments, then persist via `ResourceSaver`. Mutating (default gate `enforce`). All patches are validated atomically before any mutation — a single bad path or value aborts with the resource untouched. No-op detection reports `no_changes` (save skipped) when all patches already match. Imported/generated resources (those with a `.import` sidecar) are rejected with `resource_not_writable`.

**Input:**
- `resource_path` (required) — `res://` path or `uid://` (mapped first).
- `patches` (required, non-empty) — array of `{ path, value }` property patches (see grammar above).
- `paths_hint` (required) — mutation scope; must contain `resource_path`. Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:**

```json
{
  "resource": { "resourcePath": "res://materials/wood.tres", "uid": "uid://abc", "type": "StandardMaterial3D" },
  "changed": ["albedo_color", "roughness"],
  "unchanged": ["metallic"],
  "saved": true
}
```

`changed` lists patches whose value actually changed; `unchanged` lists no-op patches. The standard `gate` block is prepended into `result`.

**Errors:** `missing_parameter` (no `resource_path` or empty `patches`), `paths_hint_required`, `invalid_path`, `resource_not_found`, `resource_load_failed`, `resource_not_writable` (imported/generated resource), `patch_invalid`, `value_type_mismatch`, `no_changes` (all patches already match), `resource_save_failed`, `filesystem_unavailable`.

## Source of truth

- Tool definitions: `mcp-server/src/tools/`
- Capabilities builder + catalog: `mcp-server/src/capabilities/`
- CallTool routing (local vs live): `mcp-server/src/index.ts`
- MCP-side client (live POST + envelope unwrap): `mcp-server/src/live-client.ts`
