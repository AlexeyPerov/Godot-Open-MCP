# MCP tool catalog

This document covers the MCP tool catalog, the tool families, and the route policy each tool follows. For the bridge HTTP contract (`/ping`, `/tools/*`), see `bridge-http.md`.

## Tool inventory

Every tool is defined in `mcp-server/src/tools/{name}.ts` and registered in `ALL_TOOLS` (`mcp-server/src/tools/index.ts`). The bridge-side handlers live in `packages/bridge/Editor/Tools/` and `packages/bridge/Editor/MetaTools/`; the MCP-side `LiveClient` routes each CallTool to `POST /tools/{name}` on the resolved bridge port.

Tool names follow the `godot_open_mcp_*` convention (ADR-003).

### Tool families

| Family | Tools | Mutating | Notes |
|---|---|---|---|
| core | `ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`, `capabilities`, `bridge_status`, `pull_events` | `apply_fix` only | Always visible in `ListTools`. |
| node | `node_find`, `node_create`, `node_modify`, `node_set_parent`, `node_duplicate`, `node_delete` | create/modify/set-parent/duplicate/delete | Scene-tree operations. |
| scene | `scene_open`, `scene_save`, `scene_list_opened`, `scene_get_data`, `scene_create` | open/save/create | Scene lifecycle + read. |
| resource | `resource_find`, `resource_get_data`, `resource_create`, `resource_modify`, `resource_move`, `resource_delete` | create/modify/move/delete | `.tres`/`.res` discovery + bounded property inspection (read-only) + gated create/modify (P4.2) + file lifecycle move/delete (P4.3). |
| filesystem | `filesystem_list`, `filesystem_reimport` | reimport | Indexed `res://` directory listing (read-only) + exact-file reimport or full scan with a bounded, truthful settle status (P4.4). |
| editor | `editor_application_get_state`, `editor_application_set_state`, `editor_selection_get`, `editor_selection_set`, `console_get_logs`, `console_clear_logs` | set_state, selection_set | Play-process state read + start/stop with a bounded observation window (P4.5); node selection read + replace/clear (P4.6); bounded log collector get/clear (P4.7). Godot launches the game as a separate OS process — no pause/compile fields. Selection is node-only; the log collector is addon-owned (not the native Output panel). |
| screenshot | `screenshot_viewport`, `screenshot_camera`, `screenshot_isolated` | (none — all read-only) | Editor viewport capture, off-screen `Camera2D`/`Camera3D` capture, and isolated `Node3D` capture (P4.8). All three return MCP image content blocks (`image/png`); transient render nodes are freed on every path and no files are written. |
| reflection | `reflection_method_find`, `reflection_method_call` | `reflection_method_call` | First-party C# member discovery across loaded Godot/.NET assemblies (read-only, P5.1) + gated method invoke (mutating). Find returns bounded, structured member entries (returnType/parameters[]/isStatic/isGeneric/genericParameters[]) so an agent can plan an invoke without hallucinating signatures; call resolves a method by type+name with overload/generic disambiguation, targets an instance via `node_path` from the edited scene (Godot-native; replaces Unity's `object_id`-first targeting), and serializes the return value with depth/cycle guards. `Activator` is used only for pure POCOs — Godot.Object subclasses require an explicit `node_path`. |

## Route policy

| Route | Meaning | Tools |
|---|---|---|
| **live** | The CallTool handler POSTs to the bridge; the bridge handler runs on the editor main thread. | Most tools (node, scene, gate meta-tools, `apply_fix`). |
| **live-first / offline fallback** | The CallTool handler probes the bridge once; if reachable it forwards to the live handler (reflecting unsaved editor state), otherwise it parses the asset from disk with no editor required. A live semantic error (e.g. `scene_not_edited`) is authoritative and does NOT trigger the fallback — only an unreachable bridge does. | `godot_open_mcp_scene_get_data`. |
| **local** | The CallTool handler resolves the response in-process — no bridge hop. | `godot_open_mcp_capabilities`. |
| **local/live hybrid** | The CallTool handler composes a response in-process but runs one `/ping` probe through the live client (auth header + 503 fallback match `ping`). No `POST /tools/{name}` endpoint on the bridge. | `godot_open_mcp_bridge_status`. |
| **local-drains-live-stream** | The CallTool handler drains a per-process SSE subscription the MCP server keeps open to the bridge's `GET /events`. No `POST /tools/{name}` endpoint on the bridge. | `godot_open_mcp_pull_events`. |

`capabilities` is the one local-only tool: its response is built in-process from `ALL_TOOLS` + the rule/fix catalog via `buildCapabilities`. It never POSTs to the bridge (mirroring Unity Open MCP's tool-router `routeCapabilities`).

`bridge_status` is a local/live hybrid: it reads the instance lock from disk, classifies it (`classifyInstance`), runs one `/ping` probe via the live client, and derives a coarse `status` token server-side. The bridge has no `POST /tools/bridge_status` endpoint — the CallTool handler special-cases the name and calls `LiveClient.routeBridgeStatus` directly.

`pull_events` is a local-drains-live-stream tool: the MCP server holds one `BridgeEventStream` (a single SSE reader against `GET /events`) per process, and the CallTool handler drains its buffered queue via `pull()`. The bridge has no `POST /tools/pull_events` endpoint — the handler special-cases the name. Read-only, gate-free. When the bridge is offline the tool returns `connected:false` + `lastError` rather than throwing.

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

## `godot_open_mcp_bridge_status`

Operator-oriented bridge health snapshot. Composes the instance-lock classifier (`instance-discovery.ts#classifyInstance`) with a single `/ping` probe and returns a coarse `status` token so an operator (or the future Validation Suite) can branch recovery in one call. **Local/live hybrid** route — read-only, gate-free, never spawns Godot. The `/ping` fetch uses the bridge's standard 5 s timeout; the tool takes no arguments.

This is **not** a general agent health check — use `godot_open_mcp_ping` for a lightweight probe. `bridge_status` is the richer, operator-facing surface that distinguishes a clean stop from a transient reload window from a dead bridge.

**Input:** empty object.

**Status vocabulary** (the `status` field an operator/agent branches on):

| `status` | Meaning | When |
|---|---|---|
| `running` | Bridge connected and idle. | `/ping` reachable + `connected:true` + not compiling. Live-only tools are usable. |
| `compiling` | Bridge connected but Godot is compiling/reloading. | `/ping` reachable + `compiling:true`. Retry shortly. |
| `stopped` | No live listener. | `/ping` unreachable + no live lock PID. Folds two indistinguishable cases: Godot is not running, OR the addon is disabled. Inspect `instance.lock` to disambiguate (`null` → Godot likely down). |
| `unreachable` | Godot process alive but the listener did not respond. | `/ping` unreachable + lock PID alive. Usually a transient editor-reload window — retry shortly. |
| `dead_bridge` | Godot process alive but the bridge heartbeat is stale. | `classification === "dead_bridge"`. The addon is not running its HTTP listener (failed to load / disabled mid-session); `/ping` will not recover on its own. See `recoveryHint`. |

**Result:**

```json
{
  "status": "running",
  "ready": true,
  "classification": "healthy",
  "recoveryHint": null,
  "projectPath": "/home/u/MyGame",
  "instance": {
    "lockPath": "/home/u/.godot-open-mcp/instances/<sha256>.json",
    "classification": "healthy",
    "lock": {
      "pid": 4242, "port": 22028, "state": "idle",
      "isCompiling": false, "isPlaying": false,
      "heartbeatAt": "2026-07-12T00:00:02.000Z",
      "bridgeVersion": "0.0.1", "godotVersion": "4.3.1.stable.mono"
    }
  },
  "ping": {
    "reachable": true, "connected": true, "compiling": false, "isPlaying": false,
    "godotVersion": "4.3.1.stable.mono", "bridgeVersion": "0.0.1", "mode": "live"
  },
  "nextStep": "Bridge is ready. Proceed with live-only MCP tools.",
  "_source": "local"
}
```

**Field notes:**

- `ready` — coarse boolean for clients that want a single flag: `true` only when `status === "running"` (the rule a future wait-for-ready CLI poll would terminate on).
- `classification` — top-level mirror of the instance-lock classification (`healthy | reloading | dead_bridge | gone`) so agents can branch on one field without digging into `instance`.
- `recoveryHint` — `{ tool, reason, note? } | null`. Non-null **only** for `dead_bridge`. Today it names `godot_open_mcp_console_get_logs` (the closest registered diagnostic) and carries a `note` that it is the bridge-fed addon collector (which stops accumulating once the bridge is dead) and that a dedicated offline `godot_open_mcp_read_compile_errors` is planned for a later phase. The hint tool is always a registered tool — it never points at an unregistered name.
- `instance.lock` — `null` when no lock was read (no live instance known). The compact summary mirrors the operator-relevant fields (`pid`/`port`/`state`/`isCompiling`/`isPlaying`/`heartbeatAt`/versions); sensitive fields (`authToken`, `projectPath`) are not leaked.
- `ping` — `{ reachable: false }` when the probe failed (offline/timeout/http error); otherwise the parsed `/ping` body fields.
- `_source: "local"` — the response is synthesized in the MCP server (no bridge tool endpoint).

**Never errors on an offline bridge.** `stopped` / `unreachable` / `dead_bridge` are successful status reads — the tool returns `isError:false` with the `status` token set. A hard MCP error is reserved for programmer mistakes (e.g. malformed project root).

**Status mapping** (authoritative; `classification` from `classifyInstance`, `ping` from the `/ping` probe):

| classification | ping | lock PID alive | `status` |
|---|---|---|---|
| `dead_bridge` | any | any | `dead_bridge` (wins outright) |
| any | reachable + compiling | any | `compiling` |
| any | reachable + connected | any | `running` |
| any | unreachable | true | `unreachable` (transient reload window) |
| any | unreachable | false | `stopped` |

## `godot_open_mcp_pull_events`

Drain incremental bridge events (console logs + editor-state transitions) since the previous call. **Local-drains-live-stream** route — read-only, gate-free, live (requires a connected bridge). The MCP server holds one `BridgeEventStream` (a single SSE reader against the bridge's `GET /events`) per process; the first `pull_events` call opens the subscription, later calls return only new events buffered since the previous drain. Use this after mutations to stream console output without polling `/ping` or re-reading the full console (`console_get_logs`).

Why poll instead of push? The MCP server runs over a stdio transport and has no native way to forward bridge SSE → MCP notifications. Polling per call keeps the model in the loop and lets an agent decide when to drain.

**Input:** `max_events` (integer, default 50, clamped to [1, 1000]), `subscriber` (optional string — defaults to a server-scoped id; pass an explicit id to resume a cursor across MCP server restarts).

**Result:**

```json
{
  "subscriberId": "<server-scoped-or-caller-id>",
  "events": [
    { "seq": 1, "ts": "2026-07-12T00:00:01.234Z", "type": "log", "logType": "warning", "message": "..." },
    { "seq": 2, "ts": "2026-07-12T00:00:02.000Z", "type": "editor_state", "state": "playing", "isCompiling": false, "isPlaying": true }
  ],
  "dropped": 0,
  "connected": true,
  "started": false,
  "lastError": null
}
```

**Field notes:**

- `subscriberId` — the id used across reconnects; the SSE reader keeps its cursor on the bridge across a 10-minute SSE timeout or a Godot reload.
- `events[].seq` — monotonic sequence. For `log` events it matches the `console_get_logs` sequence (single fan-in from the P4.7 collector), so the two surfaces share a cursor vocabulary. `editor_state` events use the bridge's own sequence space.
- `events[].type` — `log` (carries `logType`/`message`/optional `stack`), `editor_state` (carries `state`/`isCompiling`/`isPlaying`), plus control events `ready` / `missed` / `close` from the SSE wire.
- `dropped` — events evicted from the client-side queue (capacity 500) before this pull. Non-zero only under sustained burst.
- `connected` — whether the SSE reader is currently connected to the bridge.
- `started` — `true` only on the call that opened the subscription; `false` on subsequent calls.
- `lastError` — the last reconnect failure reason; non-null only when `connected` is false.

**Never throws on an offline bridge.** When the bridge is unreachable the tool returns `connected:false` + `lastError` (and `events:[]`) with `isError:false` so an agent can branch on the connection state. The reader reconnects automatically (2 s backoff) once the bridge is back.

**Capture scope.** `log` events come from the P4.7 collector — the addon's captured activity (bridge lifecycle, tool-handler errors, routed game/script output when a supported hook is active), NOT the entire Godot editor Output panel. `editor_state` events fire at authoritative observed transitions (the bridge-driven play start/stop settle); a background observer for user-clicked play arrives in a later phase.

## `godot_open_mcp_scene_get_data`

Read the hierarchy of a Godot scene as a structured NodeData tree (read-only, gate-free). The first **live-first / offline-fallback** tool: when the Godot editor is running it reads the currently edited scene and reflects unsaved editor state; when the editor is unavailable it parses the `.tscn` from disk with no Godot process required.

**Routing:**

- **Live (bridge reachable):** forwards to the bridge handler, which walks the edited scene root via `NodeTools.ToNodeData`. `path` is optional — omit to read the edited scene; set it to assert the edited scene matches (refuses with `scene_not_edited` otherwise, since switching scenes is a mutating op that belongs to `scene_open`). Result carries live instance IDs and the bridge-tracked `isDirty` flag.
- **Offline (bridge unreachable):** parses the `.tscn` text directly. `path` is **required** (there is no edited-scene context to fall back on); omitting it returns `path_required_offline`. The result is normalized to the same envelope as the live read, with the deltas below.

**Input:**

- `path` (string) — `res://` path of the scene. Optional live (asserts the edited scene); required offline.
- `hierarchy_depth` (integer, default `1`) — `0` = root node only; `1` = root + direct children; `N` = N layers; `-1` = the whole tree. Positive values are capped at `5` to bound the response (deeper trees blow the token budget — drill in with `node_find` instead).

**Result envelope** (shared live + offline shape): `{ path, name, isDirty, rootType, hierarchyDepth, root }` where `root` is a NodeData (`instanceId`, `name`, `path`, `type`, `scriptResourcePath`, `childCount`, `children` per depth). The offline read adds:

- `stateSource: "disk"` — marks the read as disk-origin so a client never mistakes it for live.
- `isDirty: false` — offline state has no unsaved edits.
- `root.instanceId: null` — no live instance IDs offline (never a fake hashed id).
- `warnings` — present only when non-empty (e.g. an inherited scene whose base is not expanded offline).

**Offline limitations** (documented deltas from the live read): no unsaved state, no instance IDs, partial instanced/inherited-scene expansion (an instanced node with no explicit `type` uses the `PackedSceneInstance` fallback), and script paths come from serialized `ExtResource` references rather than runtime `Script` objects. The offline reader never evaluates code, loads resources, or writes project files; it is read-only and rebuilt per request (no on-disk cache).

**Offline error codes:** `path_required_offline`, `invalid_path` (non-`res://`, traversal, wrong extension, invalid characters), `path_outside_project` (symlink or canonical escape), `scene_not_found`, `scene_unreadable` (permission or non-regular file), `scene_too_large` (exceeds the 8 MiB read cap), `scene_parse_error` (malformed `.tscn`), `scene_hierarchy_invalid` (orphan/duplicate/cyclic parent graph).

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

## `godot_open_mcp_resource_move`

Move a Godot resource file (`.tres`/`.res`) and its `.import` sidecar to a new `res://` destination via `DirAccess.RenameAbsolute`. Mutating (default gate `enforce`). The destination must not already exist — no overwrite. The destination parent directory is created when missing. The `.import` sidecar is moved alongside the primary file when present.

**No reference rewriting.** Hard-coded `res://` references in other text assets are NOT rewritten. UID-based references are expected to remain stable where Godot supports them. Use `resource_find` to check dependents before moving.

**Atomicity.** Preflight (same-path, existence, collision, extension) runs before any mutation. The primary file moves first, the sidecar second. If the sidecar move fails, a rollback of the primary file is attempted and the result exposes the observed final state — the contract never claims atomicity across two OS-level operations.

**Input:**
- `source_path` (required) — `res://` path or `uid://` (mapped first).
- `destination_path` (required) — new `res://` destination, must end in `.tres`/`.res` (extension changes rejected), must not exist.
- `paths_hint` (required) — mutation scope; must contain BOTH `source_path` and `destination_path`. Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result (success):**

```json
{
  "before": { "resourcePath": "res://old.tres", "uid": "uid://abc", "type": "Resource" },
  "after": { "resourcePath": "res://dir/new.tres", "uid": "uid://abc", "type": "Resource" },
  "sidecarMoved": false,
  "filesystemScan": { "settled": true, "scanTriggered": true, "settledMs": 0, "reason": null },
  "moved": true
}
```

`before`/`after` are `ResourceIdentity` objects. `sidecarMoved` is `true` when a `.import` sidecar was relocated. `filesystemScan` reports whether the editor filesystem scan settled within the budget.

**Partial failure** (e.g. primary moved but sidecar failed and rollback was not possible): the error carries an observed-state payload under `result` — `sourceExistsAfter`, `destinationExistsAfter`, `rolledBack` — so the agent can recover.

**Errors:** `missing_parameter` (no `source_path`/`destination_path`), `paths_hint_required` (missing/empty/incomplete `paths_hint`), `invalid_path` (bad scheme/extension/traversal/directory), `same_path` (normalized source equals destination), `resource_not_found` (source missing), `destination_exists` (destination file or sidecar collision), `resource_move_failed` (primary rename fails before any move, or rolled back), `resource_move_partial` (primary moved but sidecar failed and rollback not possible), `filesystem_unavailable`.

## `godot_open_mcp_resource_delete`

Delete a Godot resource file (`.tres`/`.res`) and its `.import` sidecar via `DirAccess.RemoveAbsolute`. Mutating (default gate `enforce`). Delete is explicit and immediate — no soft-delete or recycle bin. A pre-delete identity snapshot (`path`/`uid`/`type`) is returned so the agent has a durable record of what was removed. The `.import` sidecar is removed alongside the primary file when present.

**Atomicity.** The primary file is removed first, the sidecar second. If the sidecar removal fails, the orphan `.import` path is reported — the contract never claims atomicity across two OS-level operations.

**Input:**
- `resource_path` (required) — `res://` path or `uid://` (mapped first).
- `paths_hint` (required) — mutation scope; must contain `resource_path`. Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result (success):**

```json
{
  "resource": { "resourcePath": "res://doomed.tres", "uid": "uid://abc", "type": "Resource" },
  "sidecarDeleted": false,
  "filesystemScan": { "settled": true, "scanTriggered": true, "settledMs": 0, "reason": null },
  "deleted": true
}
```

`resource` is the pre-delete `ResourceIdentity` snapshot. On partial failure (sidecar remains), the error carries `orphanSidecarPath` so the agent can clean up.

**Errors:** `missing_parameter` (no `resource_path`), `paths_hint_required`, `invalid_path`, `resource_not_found` (target missing), `resource_delete_failed` (primary removal fails), `resource_delete_partial` (primary removed but sidecar remains), `filesystem_unavailable`.

## `godot_open_mcp_filesystem_list`

List the immediate children of one `res://` directory from the editor's indexed filesystem. Read-only (gate-free). Returns directories first, then files, each group sorted by name (ordinal). No resource is loaded — file types come from `EditorFileSystemDirectory.GetFileType` and UIDs from `ResourceLoader.GetResourceUid` (both read the import index). Listing is one level; recursive full-tree listing is deferred to the offline project indexer (Phase 7).

**Input:**
- `path` (optional) — `res://` directory (trailing slash optional); omit or pass `res://` for the project root.
- `page_size` (optional, default 100, max 500) — max entries per page (directories + files combined, directories first).
- `cursor` (optional) — opaque continuation cursor from a previous response's `pagination.nextCursor`.
- `include_hidden` (optional, default false) — include hidden entries the editor index exposes.

**Result (success):**

```json
{
  "path": "res://materials/",
  "directoryCount": 1,
  "fileCount": 2,
  "entries": [
    { "name": "sub", "path": "res://materials/sub/", "isDirectory": true, "resourceType": null, "uid": null },
    {
      "name": "wood.tres",
      "path": "res://materials/wood.tres",
      "isDirectory": false,
      "resourceType": "StandardMaterial3D",
      "uid": "uid://abc123"
    }
  ],
  "pagination": { "nextCursor": null, "hasMore": false }
}
```

`directoryCount`/`fileCount` describe the full one-level directory; `entries` is the current page (directories first, then files).

**Errors:** `invalid_path` (non-`res://`, traversal, or file path), `directory_not_found` (indexed directory missing), `filesystem_unavailable` (editor filesystem not available).

## `godot_open_mcp_filesystem_reimport`

Reimport specific `res://` files via `EditorFileSystem.ReimportFiles`, or trigger a full `EditorFileSystem.Scan` when no files are given. Mutating (default gate `enforce`). The Godot analog of Unity's `AssetDatabase.Refresh`.

Two modes (selected by whether `files` is non-empty):
- **exact files** — reimport exactly those files. The entire list is validated and normalized before any file is touched; a single bad/missing entry is a clean error, never a partial effect. Duplicates are removed while preserving first occurrence.
- **full scan** — trigger `EditorFileSystem.Scan` to pick up added/removed/changed files. Requires explicit `paths_hint: ["res://"]` (no implicit whole-project gate fallback).

The call blocks until the import pipeline settles (bounded by `timeout_ms`). A timeout is a **successful** request with `settle.settled: false` — the tool never falsely claims completion. Call `filesystem_list` / `resource_find` afterwards to observe the post-scan state.

**Input:**
- `files` (optional) — list of `res://` file paths to reimport exactly. Omitted/empty → full scan.
- `timeout_ms` (optional, default 5000, clamped [1000, 60000]) — bounded settle timeout.
- `paths_hint` (required) — the exact files (must contain every requested file), or `["res://"]` for a full scan. Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result (success):**

```json
{
  "mode": "files",
  "requestedFiles": ["res://a.tres"],
  "reimportedCount": 1,
  "settle": {
    "scanStarted": true,
    "settled": true,
    "scanningProgress": null,
    "elapsedMs": 0,
    "reason": null
  },
  "reimported": true
}
```

`mode` is `"files"` or `"full_scan"`. `settle` reports whether the scan started, whether it settled, the scanning progress (when busy), the elapsed settle time, and a `reason` on timeout (`"timeout"`, `"scan_did_not_start"`, `"not_attempted"` on a pre-flight failure).

**Errors:** `paths_hint_required` (missing/empty/incomplete `paths_hint`, or full scan without `["res://"]`), `invalid_path` (bad scheme/traversal/directory), `file_not_found` (any requested file missing), `filesystem_unavailable`, `reimport_failed` (Godot rejects the reimport/scan — observed state is surfaced under `result`).

## `godot_open_mcp_editor_application_get_state`

Get a truthful snapshot of the Godot editor's play-process state. Read-only (gate-free).

Godot launches the game as a **separate OS process** (not an in-editor playmode toggle), so the state model is deliberately narrower than Unity's `editor_status`: there is no `isPaused`, no `isCompiling`, no domain-reload concept. The tool reports only what is observable — whether a play process is running and which scene it is running.

**Input:** empty object.

**Result:**

```json
{
  "isPlaying": true,
  "playingScene": "res://main.tscn",
  "editorVersion": "4.3.stable.mono",
  "observedAt": "2026-07-10T19:00:00.000Z"
}
```

`playingScene` is `null` when not playing or when Godot does not report a path. `observedAt` is an ISO-8601 UTC timestamp so callers can correlate the snapshot with their own request timing.

## `godot_open_mcp_editor_application_set_state`

Start or stop the Godot editor's play process. Mutating (default gate `enforce`). The play lifecycle writes no files, but the gate still runs because the tool changes editor/project runtime state (the verify delta is clean in the common case).

**Input:**
- `is_playing` (optional, default `false`) — `true` starts a play process for the selected scene; `false` stops any running process.
- `scene` (optional) — selector, only meaningful when `is_playing` is `true`:
  - `"main"` (default) — `EditorInterface.PlayMainScene`.
  - `"current"` — `EditorInterface.PlayCurrentScene` (requires a saved edited scene).
  - an explicit `res://...tscn` / `res://...scn` path — `EditorInterface.PlayCustomScene` (must exist).
- `timeout_ms` (optional, default `5000`, clamped `[1000, 60000]`) — bounded state-transition deadline.
- `paths_hint` (required) — the explicit scene path (custom scene), the edited scene path (`current`), or `res://project.godot` (`main` and `stop`). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result (start):**

```json
{
  "requested": { "action": "start", "scene": "main" },
  "before": { "isPlaying": false, "playingScene": null, "editorVersion": "4.3.stable.mono", "observedAt": "..." },
  "after": { "isPlaying": true, "playingScene": "res://main.tscn", "editorVersion": "4.3.stable.mono", "observedAt": "..." },
  "state": { "isPlaying": true, "playingScene": "res://main.tscn", "editorVersion": "4.3.stable.mono", "observedAt": "..." },
  "settled": true,
  "elapsedMs": 0,
  "timeoutMs": 5000
}
```

`requested.action` is `"start"` (transition observed), `"start_noop"` (already playing the same scene — idempotent), `"stop"` (transition observed), or `"stop_noop"` (already stopped — idempotent success). `before`/`after` are the observed snapshots; `state` is an alias for the final observed state. `settled` is `true` when the requested state was observed within `timeoutMs`.

**Errors:** `paths_hint_required` (missing/empty `paths_hint`), `invalid_scene_selector` (`scene` is not `main`/`current`/a valid `res://` scene path), `scene_not_found` (explicit path does not exist), `current_scene_unavailable` (no edited scene, or unsaved), `already_playing` (a process is running a different scene — observed state surfaced under `result`), `play_start_failed` / `play_stop_failed` (the `EditorInterface` API threw — observed state surfaced), `state_transition_timeout` (the requested state was not observed within `timeoutMs` — last observed state surfaced under `result`; a caller can safely follow up with `editor_application_get_state`).

**No automatic save.** A play start does not save the edited scene first. A `current` start requires a saved edited scene (a path); a freshly-created unsaved scene yields `current_scene_unavailable`.

## `godot_open_mcp_editor_selection_get`

Get the Godot editor's current node selection as structured data. Read-only (gate-free).

Godot's `EditorSelection` selects scene-tree `Node`s only — there is no asset-GUID or component selection distinction, and no first-class "active object". So the result is a flat list of selected nodes (each as shallow `NodeData`) plus the active (last-selected) node. Godot has no first-class active-object concept; the last selected node is reported as active, matching how the editor inspector tracks the most-recently-clicked node.

**Input:** empty object.

**Result:**

```json
{
  "nodes": [
    { "instanceId": 12345, "name": "Player", "path": "/root/Main/Player", "type": "Node3D", "scriptResourcePath": null, "childCount": 2, "children": null }
  ],
  "activeNode": { "instanceId": 12345, "name": "Player", "path": "/root/Main/Player", "type": "Node3D", "scriptResourcePath": null, "childCount": 2, "children": null },
  "count": 1,
  "scenePath": "res://main.tscn"
}
```

An empty selection (`count: 0`, `activeNode: null`) is a success, not an error. `scenePath` is the active edited scene path (or null when no scene is edited).

## `godot_open_mcp_editor_selection_set`

Set the Godot editor's node selection to the provided nodes (replacing any current selection). Mutating (default gate `enforce`). The selection write changes no files, but the gate still runs because the tool changes editor selection state (the verify delta is clean in the common case).

All refs are resolved completely BEFORE the current selection is cleared — a single bad ref leaves the existing selection intact (all-or-nothing). Resolution precedence: `instance_id` (priority 1) then `node_path` (priority 2). Each resolved node must belong to the active edited scene; duplicates (after resolution) and foreign-scene nodes are rejected.

**Input:**
- `select` (optional) — array of node refs; empty/omitted clears. Each ref: `{ instance_id?: integer, node_path?: string }`. When both are set, `instance_id` wins.
- `paths_hint` (required) — active edited scene path (or `res://project.godot` for a clear). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:**

```json
{
  "nodes": [
    { "instanceId": 12345, "name": "Player", "path": "/root/Main/Player", "type": "Node3D", "scriptResourcePath": null, "childCount": 2, "children": null }
  ],
  "activeNode": { "instanceId": 12345, "name": "Player", "path": "/root/Main/Player", "type": "Node3D", "scriptResourcePath": null, "childCount": 2, "children": null },
  "count": 1,
  "scenePath": "res://main.tscn",
  "cleared": true
}
```

The observed post-change selection (same shape as `editor_selection_get`) plus a `cleared` boolean. Order is preserved in the request; the observed post-state may reorder (Godot's `EditorSelection` does not guarantee order-stable reads).

**Errors:** `paths_hint_required` (missing/empty `paths_hint`), `selection_limit_exceeded` (request exceeds the hard maximum of 256 nodes), `edited_scene_unavailable` (non-empty selection with no edited scene), `node_not_found` (a ref cannot resolve — names the offending index), `node_not_in_edited_scene` (resolved node belongs elsewhere), `duplicate_node` (multiple refs resolve to the same node), `selection_update_failed` (observed post-state differs from requested — observed state surfaced under `result`), `selection_unavailable` (`EditorInterface.GetSelection()` returned null).

## `godot_open_mcp_console_get_logs`

Retrieve captured Godot Open MCP log lines, newest-first. Read-only (gate-free).

**NOTE:** Godot's C# API exposes no global managed log hook at the 4.3 baseline, so this returns the addon's own captured activity (bridge lifecycle, tool-handler errors, routed game/script output when a supported hook is active) — NOT the entire Godot editor Output panel. The response carries explicit capture-capability metadata so callers do not over-trust the contents. Each entry has a monotonic `sequence` (for future event-stream cursor use), a `logType` (`log`/`warning`/`error`), the `message`, a UTC `timestamp`, an optional `stackTrace`, and a `source` (`bridge`/`script`/`engine`/`tool`).

**Input:**
- `max_entries` (optional, default 100, [1, 1000]) — max entries to return (newest-first, so the cap keeps the most recent).
- `log_type_filter` (optional) — array of `"log"` | `"warning"` | `"error"`; omitted means all. Multiple values are unioned.
- `include_stack_trace` (optional, default false) — include stack traces.
- `last_minutes` (optional, default 0 = all retained, non-negative) — only lines captured in the last N minutes.

**Result:**

```json
{
  "entries": [
    {
      "sequence": 42,
      "logType": "error",
      "message": "Example",
      "timestamp": "2026-07-10T19:00:00.000Z",
      "stackTrace": null,
      "source": "bridge"
    }
  ],
  "returned": 1,
  "retained": 20,
  "capacity": 1000,
  "order": "newest_first",
  "capture": {
    "nativeOutputComplete": false,
    "engineErrorSinkActive": false
  }
}
```

An empty result is a successful response with capture metadata, not an error. `capture.nativeOutputComplete` is always `false` at the 4.3 baseline; `capture.engineErrorSinkActive` is `false` unless a version-gated engine error sink is armed.

**Errors:** `invalid_max_entries` (outside [1, 1000]), `invalid_log_type` (unknown filter value), `invalid_last_minutes` (negative), `console_unavailable` (collector not initialized).

## `godot_open_mcp_console_clear_logs`

Clear the Godot Open MCP log cache (read by `console_get_logs`). Gate-free direct — mutates only ephemeral addon-owned collector state, not project files or Godot editor state. Checkpoint/delta cannot meaningfully cover ephemeral memory, so there is no `paths_hint` and no gate surface.

NOTE: Godot's C# API exposes no managed hook to clear the editor's own Output panel, so (unlike Unity) this clears ONLY the addon-side collector — `nativeOutputCleared` is always `false`. The collector sequence is NOT reset on clear (a future event-stream cursor stays monotonic across a clear).

**Input:** empty object.

**Result:**

```json
{
  "cleared": 20,
  "retained": 0,
  "nativeOutputCleared": false
}
```

`cleared` is the number of entries removed; `retained` is the post-clear count (always 0).

## Screenshot tools (P4.8)

Three read-only (gate-free) tools that capture images and return them as **MCP image content blocks** (`image/png`). Unlike every other tool, the success result is a two-block content array: the image first, then a short text block with capture metadata (width, height, byteLength, mode, caption, clamped). The base64 PNG payload never appears inside the text JSON block on success — the MCP server detects the bridge image envelope (`mediaType: "image/png"` + non-empty `data`) and unwraps it into the image block. Error responses stay structured text errors.

All three tools create transient editor render nodes (an off-screen `SubViewport`, a clone camera, a light, optionally a `WorldEnvironment`) but free them on every path — success, error, and timeout — and write no project files. Headless / no-GPU environments produce a structured `render_unavailable` / `empty_image` error rather than a blank image.

**Shared limits:**

- Positive dimensions only (>= 1 px); caller requests above 16384 px are rejected.
- The longest encoded edge is clamped to **3840 px** (aspect preserved). The result reports `clamped: true` when downscaling was applied.
- The encoded PNG must stay under an **8 MB** transport ceiling. If it still exceeds that after dimension clamping, the tool rejects it with `image_too_large`.

### `godot_open_mcp_screenshot_viewport`

Capture the active Godot editor 2D or 3D viewport. Read-only (gate-free). The viewport is read back in-memory via `EditorInterface.GetEditorViewport2D()` / `GetEditorViewport3D(0)`; no file is written.

**Input:**

| Field | Type | Default | Notes |
|---|---|---|---|
| `mode` | `"2d"` \| `"3d"` | `"3d"` | Which editor viewport to capture. |

**Result:** image content block (`image/png`) + text metadata block (`{ mediaType, width, height, byteLength, mode, caption, clamped, source }`).

**Errors:** `invalid_capture_mode` (unknown mode), `viewport_unavailable` (no open viewport), `render_unavailable` (no GPU / `--headless` / readback failure), `empty_image` (readback produced no pixels), `png_encode_failed`, `image_too_large`.

### `godot_open_mcp_screenshot_camera`

Render an off-screen capture from a `Camera2D` or `Camera3D` in the edited scene. Read-only (gate-free). The source camera is never moved — a transient `SubViewport` shares the camera's world (`World3D` / `World2D`) and clones its transform/projection so the off-screen render sees the same content.

**Input:**

| Field | Type | Default | Notes |
|---|---|---|---|
| `node_ref` | object | (required) | `{ instance_id?, node_path? }` — `instance_id` is priority 1, `node_path` priority 2. Must resolve to a `Camera2D` or `Camera3D`. |
| `width` | integer | 1920 | Clamped to a 3840 px longest edge (aspect preserved). |
| `height` | integer | 1080 | Clamped to a 3840 px longest edge (aspect preserved). |

**Result:** image content block + text metadata block (`{ mediaType, width, height, byteLength, mode:"camera", caption, clamped, source }`).

**Errors:** `invalid_dimensions`, `node_not_found`, `invalid_camera_node` (target is not `Camera2D`/`Camera3D`), `render_unavailable`, `empty_image`, `png_encode_failed`, `image_too_large`.

### `godot_open_mcp_screenshot_isolated`

Render a `Node3D` alone in an isolated world from one of six directions. Read-only (gate-free). The target is duplicated **without its scripts** (so `_EnterTree`/`_Ready` side effects never run) into a fresh `SubViewport` with `OwnWorld3D = true`, framed by a transient camera computed from the combined AABB of the target's `VisualInstance3D` descendants. An empty/degenerate geometry falls back to a 0.1-unit bounds box (`usedFallbackBounds: true` in the metadata).

**Input:**

| Field | Type | Default | Notes |
|---|---|---|---|
| `node_ref` | object | (required) | `{ instance_id?, node_path? }`. Must resolve to a `Node3D`. |
| `camera_view` | enum | `"front"` | `front` (-Z), `back` (+Z), `left` (-X), `right` (+X), `top` (+Y), `bottom` (-Y). |
| `background` | enum | `"solid_color"` | `solid_color` (uses `background_color` via a transient `WorldEnvironment`) or `transparent` (alpha-clear). |
| `background_color` | string | `"#404040"` | Hex color (`#RGB` / `#RRGGBB` / `#RRGGBBAA`). Ignored when `background` is `transparent`. |
| `field_of_view` | number | 60 | Degrees, range [1, 179]. |
| `near_clip_plane` | number | 0.05 | Loosened (moved closer) by the framing math to guarantee the target is inside the clip range. |
| `far_clip_plane` | number | 4000 | Loosened (moved farther) by the framing math to guarantee the target is inside the clip range. |
| `padding` | number | 1.2 | Framing padding multiplier. Larger values zoom out. |
| `resolution` | integer | 512 | Square output resolution (px). Clamped to a 3840 px longest edge. |

**Result:** image content block + text metadata block (`{ mediaType, width, height, byteLength, mode:"isolated", caption, clamped, source, bounds: { center, size, radius, cameraDistance, near, far, usedFallbackBounds } }`).

**Errors:** `invalid_capture_mode` (unknown view/background/color), `invalid_dimensions`, `invalid_isolated_node` (target is not `Node3D`), `node_not_found`, `degenerate_geometry` (camera framing impossible after fallback rules), `render_unavailable`, `empty_image`, `png_encode_failed`, `image_too_large`.

## Source of truth

- Tool definitions: `mcp-server/src/tools/`
- Capabilities builder + catalog: `mcp-server/src/capabilities/`
- CallTool routing (local vs live): `mcp-server/src/index.ts`
- MCP-side client (live POST + envelope unwrap): `mcp-server/src/live-client.ts`
