# MCP tool catalog

This page documents every MCP tool shipped by Godot Open MCP. Tool names follow the `godot_open_mcp_*` convention; the tool definitions, route policies, and group assignments are code source-of-truth (see [Source of truth](#source-of-truth)), and the canonical inventory table below is the published view of that registry.

For the bridge HTTP contract (`/ping`, `/tools/*`, `/events`), see [`bridge-http.md`](bridge-http.md).

## Purpose and source of truth

- This page documents MCP tools — what each one does, what it accepts, what it returns, and how it routes.
- Bridge transport details (HTTP envelope, SSE stream, status codes, instance lock) live in [`bridge-http.md`](bridge-http.md) and are not duplicated here.
- Tool definitions live in `mcp-server/src/tools/{name}.ts`; the registration array is `ALL_TOOLS` in `mcp-server/src/tools/index.ts`. The route policy per tool comes from `mcp-server/src/capabilities/route-policy.ts`; the visibility group comes from `mcp-server/src/capabilities/tool-groups.ts`. Those three modules are the source of truth — this page mirrors them, and `scripts/check-tool-docs.mjs` fails CI when they drift.
- Names use `godot_open_mcp_*`. Bare suffixes (`ping`, `node_find`, …) appear in prose for readability, but every canonical heading, anchor, and inventory-table row uses the full id.

## Quick-start workflow

```text
1. capabilities          → learn the tool/rule/fix surface and route vocabulary
2. manage_tools          → activate typed-editor if you need nodes/scenes/resources/…
3. bridge_status / ping  → confirm the editor bridge is reachable
4. typed read / mutation → node_find → node_create → node_modify → scene_save
5. gate review           → checkpoint_create → mutate → delta (or validate_edit)
6. recovery / fix        → apply_fix (dry_run first) or read_compile_errors when the bridge is dead
```

For deeper agent workflow guidance (when to checkpoint, how to chain reads into mutations, how to recover from a dead bridge), see the [agent skill](../skills.md).

## Canonical inventory table

The table below is the published view of `ALL_TOOLS`. The `scripts/check-tool-docs.mjs` parity checker compares this table against the registry, route-policy, and tool-group modules — adding or renaming a tool without updating this table fails CI.

<!-- tool-docs:inventory -->
| Tool | Family | Route | Visibility | Mutates | Gate | Summary |
|---|---|---|---|---|---|---|
| `godot_open_mcp_apply_fix` | core | live | core | disk | warn/off capable | Apply (or preview) a structured fix for a verify issue; non-dry-run applies roll back on new errors under `enforce`. |
| `godot_open_mcp_bridge_status` | core | local | always visible | no | n/a | Operator-oriented health snapshot composing the instance-lock classifier with one `/ping` probe. |
| `godot_open_mcp_capabilities` | core | local | always visible | no | n/a | Discover the full capability surface (tools + verify rules + fixes + groups + routing) in one call. |
| `godot_open_mcp_checkpoint_create` | core | live | core | no | n/a | Capture a project-health baseline over res:// paths for later `delta`. |
| `godot_open_mcp_console_clear_logs` | editor | live | typed-editor | ephemeral | n/a | Clear the addon-owned log collector (ephemeral; never touches the native Output panel). |
| `godot_open_mcp_console_get_logs` | editor | live | typed-editor | no | n/a | Read captured Godot Open MCP log lines, newest-first, with capture-capability metadata. |
| `godot_open_mcp_delta` | core | live | core | no | n/a | Compare current state against a prior checkpoint and return the new/resolved issue delta. |
| `godot_open_mcp_editor_application_get_state` | editor | live | typed-editor | no | n/a | Truthful play-process snapshot (`isPlaying`, `playingScene`, `editorVersion`, `observedAt`). |
| `godot_open_mcp_editor_application_set_state` | editor | live | typed-editor | editor state | enforce | Start (main/current/custom scene) or stop the play process with a bounded observation window. |
| `godot_open_mcp_editor_selection_get` | editor | live | typed-editor | no | n/a | Read the editor's node selection as shallow NodeData + the active node. |
| `godot_open_mcp_editor_selection_set` | editor | live | typed-editor | editor state | enforce | Replace or clear the node selection (all-or-nothing resolution). |
| `godot_open_mcp_filesystem_list` | filesystem | live-first | typed-editor | no | n/a | List immediate children of a `res://` directory; live reads authoritative importer metadata. |
| `godot_open_mcp_filesystem_reimport` | filesystem | live | typed-editor | disk | enforce | Reimport exact files or trigger a full scan; blocks until the import pipeline settles. |
| `godot_open_mcp_manage_tools` | core | local | always visible | ephemeral | n/a | Per-session tool-group visibility mutator (activate/deactivate/reset/list_groups). |
| `godot_open_mcp_node_create` | node | live | typed-editor | editor state | warn/off capable | Create a Node in the edited scene (typed ClassDB instantiate or PackedScene instance). |
| `godot_open_mcp_node_delete` | node | live | typed-editor | editor state | warn/off capable | Delete one or more Nodes (and their sub-trees) synchronously via `Node.Free`. |
| `godot_open_mcp_node_duplicate` | node | live | typed-editor | editor state | warn/off capable | Duplicate a Node sub-tree via `Node.Duplicate`, optionally cross-parent and renamed. |
| `godot_open_mcp_node_find` | node | live | typed-editor | no | n/a | Find Nodes in the edited scene (targeted lookup or filtered list). |
| `godot_open_mcp_node_modify` | node | live | typed-editor | editor state | warn/off capable | Apply property/transform updates to one or more Nodes (single + batch). |
| `godot_open_mcp_node_set_parent` | node | live | typed-editor | editor state | warn/off capable | Reparent a Node via `Node.Reparent`, cycle-safe, transform-preserving by default. |
| `godot_open_mcp_ping` | core | live | core | no | n/a | Bridge health check (`GET /ping` round-trip). |
| `godot_open_mcp_pull_events` | core | local | always visible | no | n/a | Drain incremental bridge events (console logs + editor-state transitions) since the last pull. |
| `godot_open_mcp_read_compile_errors` | core | offline | always visible | no | n/a | Offline diagnostic: read a bounded Godot log tail and extract structured C#/GDScript/load errors. |
| `godot_open_mcp_reflection_method_call` | reflection | live | typed-editor | disk | enforce | Invoke a C# method via reflection (static or instance) and return a JSON-serializable result. |
| `godot_open_mcp_reflection_method_find` | reflection | live | typed-editor | no | n/a | Discover C# types/methods/properties across loaded Godot/.NET assemblies. |
| `godot_open_mcp_resource_create` | resource | live | typed-editor | disk | enforce | Instantiate a Resource subclass via ClassDB and persist via ResourceSaver (no overwrite). |
| `godot_open_mcp_resource_delete` | resource | live | typed-editor | disk | enforce | Delete a `.tres`/`.res` file and its `.import` sidecar via `DirAccess.RemoveAbsolute`. |
| `godot_open_mcp_resource_find` | resource | live | typed-editor | no | n/a | Find Godot resources by uid/path or indexed type search over `EditorFileSystem`. |
| `godot_open_mcp_resource_get_data` | resource | live | typed-editor | no | n/a | Load a resource and return a bounded, cycle-safe property tree. |
| `godot_open_mcp_resource_modify` | resource | live | typed-editor | disk | enforce | Apply validated property-path patches to a resource and persist via ResourceSaver. |
| `godot_open_mcp_resource_move` | resource | live | typed-editor | disk | enforce | Move a `.tres`/`.res` file + `.import` sidecar via `DirAccess.RenameAbsolute` (no reference rewriting). |
| `godot_open_mcp_scene_create` | scene | live | typed-editor | disk | warn/off capable | Create a new `.tscn` asset at a res:// path and optionally open it as the active scene. |
| `godot_open_mcp_scene_get_data` | scene | live-first | typed-editor | no | n/a | Read the edited scene's hierarchy as a NodeData tree; offline falls back to `.tscn` disk parse. |
| `godot_open_mcp_scene_list_opened` | scene | live | typed-editor | no | n/a | List every scene currently open in the editor as a shallow snapshot. |
| `godot_open_mcp_scene_open` | scene | live | typed-editor | editor state | warn/off capable | Open a `.tscn`/`.scn` asset and make it the active/edited scene. |
| `godot_open_mcp_scene_save` | scene | live | typed-editor | disk | warn/off capable | Save the edited scene (or save-as / save-all). |
| `godot_open_mcp_screenshot_camera` | screenshot | live | typed-editor | no | n/a | Off-screen render from a `Camera2D`/`Camera3D` in the edited scene (image content block). |
| `godot_open_mcp_screenshot_isolated` | screenshot | live | typed-editor | no | n/a | Render a `Node3D` alone in an isolated world from one of six directions (image content block). |
| `godot_open_mcp_screenshot_viewport` | screenshot | live | typed-editor | no | n/a | Capture the active editor 2D/3D viewport (image content block). |
| `godot_open_mcp_tilemap_clear` | tilemap | live | tilemap | editor state | enforce | Clear every cell on a `TileMapLayer` while keeping its TileSet. |
| `godot_open_mcp_tilemap_create` | tilemap | live | tilemap | editor state | enforce | Create a Godot 4.3+ `TileMapLayer` node in the edited scene (returns NodeData). |
| `godot_open_mcp_tilemap_erase_cell` | tilemap | live | tilemap | editor state | enforce | Erase one cell from a `TileMapLayer`. |
| `godot_open_mcp_tilemap_get_used_cells` | tilemap | live | tilemap | no | n/a | List used cells on a `TileMapLayer` (bounded by `max_results`). |
| `godot_open_mcp_tilemap_set_cell` | tilemap | live | tilemap | editor state | enforce | Paint one cell via Godot's atlas addressing quadruple (source/atlas/alternative). |
| `godot_open_mcp_tilemap_set_tileset` | tilemap | live | tilemap | editor state | enforce | Assign an existing `TileSet` resource (`res://`) to a `TileMapLayer`. |
| `godot_open_mcp_validate_edit` | core | live | core | no | n/a | Run a scoped read-only verify pass over res:// paths and return the health verdict. |
<!-- /tool-docs:inventory -->

The `Mutates` column distinguishes `no` (read-only, gate-free), `disk` (writes project files), `editor state` (mutates unsaved editor state — the gate still runs but the verify delta is typically clean), and `ephemeral` (mutates only addon-owned in-memory state — checkpoint/delta cannot meaningfully cover it). The `Gate` column lists the bridge's catalogued default: `enforce`, `warn/off capable` (default `off`, but `enforce`/`warn` are accepted), or `n/a` (read-only/ephemeral, no gate surface).

The `node_*` and `scene_*` mutation tools (`scene_open`, `scene_save`, `scene_create`, `node_create`, `node_modify`, `node_set_parent`, `node_duplicate`, `node_delete`) currently default to `gate: "off"` in their input schema — they were shipped before the gate flow landed. The bridge handler still records `isMutating: true` for each, so an agent that passes `gate: "enforce"` gets the full checkpoint → mutate → validate → delta cycle. A later phase will flip the schema defaults to `enforce` to match the resource/editor/filesheet mutators.

## Shared contracts

The per-tool sections below link back to these shared contracts and only repeat a rule when omitting it would be unsafe.

### stdio → loopback bridge topology

The MCP server speaks stdio MCP to the AI client and loopback HTTP to the Godot bridge addon. Each `CallTool` is dispatched by the router (`mcp-server/src/tool-router.ts`) to one of four policies (see [Route policy](#route-policy)).

### Route policy

Every registered tool follows exactly one route policy. The policy is descriptive metadata advertised in `godot_open_mcp_capabilities` (the `routePolicy` field on each tool entry) and the authoritative classification is shared between the catalog and the router via `mcp-server/src/capabilities/route-policy.ts` so the two cannot drift.

| Policy | Meaning |
|---|---|
| **live** | The CallTool handler POSTs to the bridge; the bridge handler runs on the editor main thread. Requires the bridge; no disk substitute. The default for any tool not in an override set. |
| **local** | The CallTool handler resolves the response in the MCP process — no `POST /tools/{name}` bridge hop. `bridge_status` and `pull_events` may touch the live transport (one bounded `/ping` probe; one SSE-driven queue drain) but the call is synthesized locally; the bridge has no dedicated handler for them. |
| **offline** | The CallTool handler NEVER probes the bridge and NEVER POSTs to it — it reads disk/config straight. Used for diagnostics that must work in the exact state a dead bridge describes (the addon is not running its listener). |
| **live-first** | The CallTool handler probes the bridge once; if reachable it forwards to the live handler (reflecting unsaved editor state / authoritative import metadata), otherwise it reads from disk with no editor required. A live semantic error (e.g. `scene_not_edited`, `directory_not_found`) is authoritative and does NOT trigger the fallback — only an unreachable bridge does. |

**Route selection.** The router (`mcp-server/src/tool-router.ts`) selects one policy per call: a tool in an override set (`local` / `offline` / `live-first`) is dispatched to its named handler; every other registered tool falls through to the generic live route (`LiveClient.route` → bridge). There is no per-call route override — `routePolicy` advertises possible behavior, it does not let callers pick a route.

**No batch route.** Godot has no headless editor batch equivalent, so there is no `batch` policy, no `batchCapable` flag, and no headless spawn fallback. Every call is live / offline / local.

**Runtime metadata.** Every parseable JSON result is tagged with two MCP-server-owned fields so an agent can answer "where did this originate?":

- `_source` — `live` | `offline` | `local` (where the payload originated).
- `_route` — `{ route, fallbackReason? }` (which policy executed the call). `fallbackReason` appears only when a live-first tool fell back to disk (`"live_unavailable"`).

### Tool groups and session visibility

The MCP server filters `ListTools` through a per-session `ToolSessionState` so the prompt surface stays small. Every registered tool maps to exactly one group via `groupFor(toolName)`; meta-tools (`capabilities`, `bridge_status`, `pull_events`, `read_compile_errors`, `manage_tools`) map to `null` and are always visible.

| Group id | Default-on | Covers |
|---|---|---|
| `core` | yes | Essential entry points + the gate/verify safety surface (`ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`). The only group visible in a fresh session. |
| `typed-editor` | no | The whole typed editor surface: nodes, scenes, resources, filesystem, editor state/selection, console, screenshots, reflection. One activate brings up the full typed surface. |
| `tilemap` | no | Godot 4.3+ `TileMapLayer` tools: create a layer, assign a `TileSet`, set/erase/clear cells, list used cells. |
| `navigation` / `particles` / `animation` / `csg` | no | Domain pack stubs. Reserved ids with empty tool rosters — empty until the packs ship. |

The catalog source of truth is `mcp-server/src/capabilities/tool-groups.ts`. Activate or deactivate groups with `godot_open_mcp_manage_tools`; on a successful change the server emits the MCP `notifications/tools/list_changed` notification so clients that support `listChanged` refresh `ListTools` automatically. Hiding a tool is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

### Success and error handling

Every CallTool returns a `CallToolResult`. JSON payloads are tagged with `_source` + `_route` (see [Route policy](#route-policy)).

- **Success.** `isError: false` and a content array of one text block (the JSON body) — except screenshots, which return an image content block plus a text metadata block (see [Screenshots](#screenshot-tools)).
- **Structured error.** `isError: true` with an `error: { code, message }` body. Error codes are stable lowercase tokens (`missing_parameter`, `invalid_path`, `resource_not_found`, `node_not_found`, `scene_dirty`, `paths_hint_required`, …). The catalog lists every code per tool.
- **Bridge transport failure.** When the bridge is unreachable on a live route, the result is `isError: true` with a transport-shaped error (timeout / connection refused / 5xx). The local/live hybrid tools (`bridge_status`, `pull_events`) and the offline tool (`read_compile_errors`) never throw on an offline bridge — `stopped` / `unreachable` / `dead_bridge` are observed states, not execution failures.

### Mutation gate, paths_hint, checkpoint/delta, and rollback

Mutating tools accept two gate-control arguments:

- `paths_hint` (required on every mutating tool that writes disk or persistent editor state) — the `res://` paths the gate should checkpoint and re-validate. There is no whole-project fallback; an empty hint is rejected with `paths_hint_required` before the mutation runs.
- `gate` (`enforce` | `warn` | `off`, default per tool) — selects whether the full cycle runs:
  - `enforce` — checkpoint → mutate → validate → delta; new Error-severity issues flip the result to `isError: true`.
  - `warn` — run the cycle but report `gate.delta` without hard-failing.
  - `off` — skip the cycle entirely (`paths_hint` is still required when the schema marks it so).

The `gate` block in the result carries `{ mode, outcome, ran, failed, delta, agentNextSteps }`. The checkpoint/delta cycle is also exposed directly via `godot_open_mcp_checkpoint_create` + `godot_open_mcp_delta` for an explicit workflow across separate tool calls.

**`apply_fix` rollback.** A non-dry-run `apply_fix` under `enforce` that fails or introduces new errors is restored to its pre-fix state; the response carries a top-level `rollback` block (`rolledBack`, `reason`, `restoredPaths`). Applying a fix with `gate: "off"` commits without rollback protection.

**Legitimate gate-free ephemeral exceptions.** Two tools mutate without a gate surface because checkpoint/delta cannot meaningfully cover ephemeral in-memory state:

- `console_clear_logs` — clears only the addon-owned log collector. No `paths_hint`, no `gate`.
- `manage_tools` — mutates the per-session `ToolSessionState`. Local-only, no `paths_hint`, no `gate`.

### Path rules

Tool arguments that take project paths use the `res://` scheme exclusively.

- **Resolution.** Every `res://` path is resolved safely beneath the project root. Traversal (`..`), canonical escapes, and symlink escapes are rejected with `path_outside_project`. Non-`res://` schemes are rejected with `invalid_path`.
- **Extension constraints.** Scene paths require `.tscn` / `.scn`; resource paths require `.tres` / `.res`; script paths accept `.gd` / `.cs`. A wrong extension surfaces `invalid_path`.
- **Node paths** (used by `node_*` tools, `reflection_method_call`, `editor_selection_set`) are scene-tree paths relative to the edited scene root: `Main/Player`, `/root/Main/Player`, or `.` for the root. They are not `res://` paths.
- **UIDs** (`uid://…`) are accepted alongside `res://` paths by the resource tools and mapped first via `ResourceUid`.

### Pagination / cursors and bounded outputs

Several readers bound their output by default and accept paging:

- `filesystem_list` — `page_size` (default 100, max 500) + opaque `cursor`; directories first, then files, each group sorted by name.
- `resource_find` — `page_size` (default 50, max 200) + `cursor` in search mode; a `total` field reports the full match count.
- `node_find` — `max_results` (default 50, minimum 1); a `truncated` count reports the remainder.
- `reflection_method_find` — `max_results` (default 50, max 200); a `truncated` count reports the remainder.
- `reflection_method_call` — `max_depth` (default 4) + `max_items` (default 100) bound the returned object graph.

### Image content block behavior

Screenshot tools return success as a **two-block content array**: an MCP image content block (`image/png`) first, then a short text metadata block (`width`, `height`, `byteLength`, `mode`, `caption`, `clamped`, …). The base64 PNG payload never appears inside the text JSON block on success — the MCP server detects the bridge image envelope (`mediaType: "image/png"` + non-empty `data`) and unwraps it into the image block. See [Screenshot tools](#screenshot-tools).

### Common bridge-unavailable / compile-load recovery

When the bridge is unreachable:

- `bridge_status` returns `stopped` / `unreachable` / `dead_bridge` as a successful status read (never an error).
- `pull_events` returns `connected: false` + `lastError` (and `events: []`).
- `read_compile_errors` is always-offline and never depends on the bridge — use it to diagnose a `dead_bridge` (the recovery hint from `bridge_status` points here).
- Live-first readers fall back to disk: `scene_get_data` parses the `.tscn`; `filesystem_list` lists the directory.
- Every other live tool surfaces a structured transport error.

### No batch route

Godot has no headless editor batch equivalent. There is no `batch` policy, no `batchCapable` flag, no headless spawn, and no `batch_execute` tool. Every call is live / offline / local.

## Tool families

| Family | Tools | Mutating | Notes |
|---|---|---|---|
| core | `ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`, `capabilities`, `bridge_status`, `pull_events`, `read_compile_errors`, `manage_tools` | `apply_fix` only | Always visible in `ListTools`. `manage_tools` is the per-session visibility mutator. |
| node | `node_find`, `node_create`, `node_modify`, `node_set_parent`, `node_duplicate`, `node_delete` | create/modify/set-parent/duplicate/delete | Scene-tree operations. |
| scene | `scene_open`, `scene_save`, `scene_list_opened`, `scene_get_data`, `scene_create` | open/save/create | Scene lifecycle + read. |
| resource | `resource_find`, `resource_get_data`, `resource_create`, `resource_modify`, `resource_move`, `resource_delete` | create/modify/move/delete | `.tres`/`.res` discovery + bounded property inspection (read-only) + gated create/modify + file lifecycle move/delete. |
| filesystem | `filesystem_list`, `filesystem_reimport` | reimport | Indexed `res://` directory listing (read-only) + exact-file reimport or full scan with a bounded, truthful settle status. |
| editor | `editor_application_get_state`, `editor_application_set_state`, `editor_selection_get`, `editor_selection_set`, `console_get_logs`, `console_clear_logs` | set_state, selection_set | Play-process state read + start/stop with a bounded observation window; node selection read + replace/clear; bounded log collector get/clear. Godot launches the game as a separate OS process — no pause/compile fields. Selection is node-only; the log collector is addon-owned (not the native Output panel). |
| screenshot | `screenshot_viewport`, `screenshot_camera`, `screenshot_isolated` | (none — all read-only) | Editor viewport capture, off-screen `Camera2D`/`Camera3D` capture, and isolated `Node3D` capture. All three return MCP image content blocks (`image/png`); transient render nodes are freed on every path and no files are written. |
| reflection | `reflection_method_find`, `reflection_method_call` | `reflection_method_call` | First-party C# member discovery across loaded Godot/.NET assemblies (read-only) + gated method invoke (mutating). Find returns bounded, structured member entries (returnType/parameters[]/isStatic/isGeneric/genericParameters[]) so an agent can plan an invoke without hallucinating signatures; call resolves a method by type+name with overload/generic disambiguation, targets an instance via `node_path` from the edited scene (Godot-native; replaces Unity's `object_id`-first targeting), and serializes the return value with depth/cycle guards. `Activator` is used only for pure POCOs — Godot.Object subclasses require an explicit `node_path`. |

## Core / meta tools

### `godot_open_mcp_ping`

- Route: `live`
- Visibility group: `core`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Bridge health check. The canonical end-to-end probe: AI client → MCP server → live bridge `GET /ping`.

**Input:** empty object.

**Result:** the parsed `/ping` body — `{ connected, compiling, isPlaying, godotVersion, bridgeVersion, mode }`. Tagged with `_source: "live"` + `_route.route: "live"`.

**Errors:** transport failure (timeout / connection refused / 5xx) surfaces as `isError: true`. Use `bridge_status` for the richer health snapshot that distinguishes a clean stop from a dead bridge.

### `godot_open_mcp_capabilities`

- Route: `local`
- Visibility group: always visible
- Read-only/mutating: read-only
- Live editor requirement: none (built locally in the MCP server; no bridge hop)

Discover the full capability surface in one call.

**Input:**

- `kind` (optional) — `tools` | `rules` | `fixes`. Filter to a single surface; omit for all three.
- `include_planned` (optional, default `true`) — include planned-but-unbuilt capabilities (`status: "planned"`). The v1 catalog has no planned entries yet.

**Result:**

```json
{
  "tools": [{ "name": "godot_open_mcp_ping", "implemented": true, "status": "implemented", "description": "Bridge health check.", "routePolicy": "live" }],
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
  "toolGroups": [{
    "id": "core",
    "description": "Essential entry points and the gate/verify safety surface …",
    "defaultEnabled": true,
    "tools": ["godot_open_mcp_apply_fix", "godot_open_mcp_checkpoint_create", "godot_open_mcp_delta", "godot_open_mcp_ping", "godot_open_mcp_validate_edit"],
    "available": true
  }],
  "counts": { "toolsImplemented": 40, "toolsPlanned": 0, "rulesImplemented": 3, "rulesPlanned": 0, "fixesImplemented": 1, "fixesPlanned": 0 },
  "routing": { "liveDefault": true, "policies": ["live", "local", "offline", "live-first"] }
}
```

Each capability carries an `implemented` boolean; planned-but-unbuilt items return with `status: "planned"` and actionable `guidance` instead of failing. Counts are derived from the registry at call time (not hand-maintained literals). The `routing` block advertises the route vocabulary; per-tool `routePolicy` lives on each tool entry. The `toolGroups` block is compiled-state catalog only — per-session activation lives on `manage_tools` `list_groups`.

#### Rule + fix catalog

The `rules[]` and `fixes[]` arrays mirror the C# verify package and MUST stay in sync on every rule/fix change. The drift-detection tests in `mcp-server/src/capabilities/rule-catalog.test.ts` pin the issue codes, severities, and fix mappings against the C# constants:

| Rule id | Issue code | Severity | Fix |
|---|---|---|---|
| `broken_references` | `broken_scene_reference` | Error | — |
| `missing_scripts` | `missing_script` | Error | `remove_missing_script` |
| `import_health` | `orphan_import` | Warning | — |
| `import_health` | `duplicate_uid` | Error | — |

| Fix id | Resolves | Safe |
|---|---|---|
| `remove_missing_script` | `missing_scripts` / `missing_script` | true |

The catalog source of truth is `mcp-server/src/capabilities/rule-catalog.ts`; the builder is `mcp-server/src/capabilities/build-capabilities.ts`.

### `godot_open_mcp_manage_tools`

- Route: `local`
- Visibility group: always visible
- Read-only/mutating: mutates per-session ephemeral state (no `paths_hint`, no gate)
- Live editor requirement: none (no bridge hop)

Per-session tool-group visibility mutator. The MCP server holds the session state (`ToolSessionState`); the bridge does not track it. Activating a group makes its tools appear in subsequent `ListTools` responses; deactivating removes them; `reset` restores the default-on groups (`core` only). Always visible so an agent can reach the visibility surface before any other group is active.

State is ephemeral and per-session — it resets to `core` only when the MCP server restarts. The store is intentionally not keyed by session id: the stdio MCP server has exactly one client per process.

**Input:**

- `action` (required) — `list_groups` | `activate` | `deactivate` | `reset`.
- `group` (optional, required for `activate` / `deactivate`) — group id; valid ids come from `list_groups`.

**Result (`list_groups`):**

```json
{
  "groups": [
    {
      "id": "core",
      "description": "Essential entry points and the gate/verify safety surface …",
      "defaultEnabled": true,
      "active": true,
      "activationSource": "default",
      "toolCount": 5,
      "tools": ["godot_open_mcp_apply_fix", "godot_open_mcp_checkpoint_create", "godot_open_mcp_delta", "godot_open_mcp_ping", "godot_open_mcp_validate_edit"]
    },
    {
      "id": "typed-editor",
      "description": "Typed editor surface: nodes, scenes, resources, …",
      "defaultEnabled": false,
      "active": false,
      "activationSource": null,
      "toolCount": 30,
      "tools": ["godot_open_mcp_node_find", "..."]
    },
    {
      "id": "tilemap",
      "description": "TileMapLayer tools (domain pack). Empty until the pack ships.",
      "defaultEnabled": false,
      "active": false,
      "activationSource": null,
      "toolCount": 0,
      "tools": []
    }
  ],
  "activeGroups": ["core"],
  "note": "Activate a group to add its tools to your ListTools surface; deactivate to hide them. State is per-session and ephemeral — it resets to `core` only when the MCP server restarts.",
  "_source": "local",
  "_route": { "route": "local" }
}
```

Catalog order is preserved (the order groups appear in `capabilities.toolGroups`). `toolCount: 0` on a stub pack conveys "empty until the pack ships" without a separate availability field.

**Result (`activate` / `deactivate`):**

```json
{
  "action": "activate",
  "group": "typed-editor",
  "changed": true,
  "activeGroups": ["core", "typed-editor"],
  "message": "Group 'typed-editor' activated. Its tools will appear in the next ListTools response; MCP clients that support listChanged will refresh automatically.",
  "_source": "local",
  "_route": { "route": "local" }
}
```

`changed` is `false` on an idempotent call (the group was already in the requested state). Activating a default-on group that was previously deactivated flips its `activationSource` from `"default"` to `"manual"` — the explicit act wins.

**Result (`reset`):**

```json
{
  "reset": true,
  "changed": true,
  "activeGroups": ["core"],
  "message": "Tool-group visibility restored to `core` only. …",
  "_source": "local",
  "_route": { "route": "local" }
}
```

`changed` is `false` when the state was already at the defaults (the call was a no-op for both state and notification).

**Field notes:**

- `activationSource` — `default` (default-on group), `manual` (activated via `manage_tools`), or `null` (group is not active). There is intentionally NO `auto` value today — Godot has no bridge compile inventory for domain packs yet; a later phase may add it when pack auto-activation lands.
- `activeGroups` — sorted snapshot of the active set, the same value `ListTools` consults.

**Errors (structured, never throws):**

| Condition | `isError` | Code |
|---|---|---|
| Missing `action` | true | `missing_parameter` |
| Unknown `action` | true | `unknown_action` (lists valid actions) |
| `activate` / `deactivate` without `group` | true | `missing_parameter` |
| Unknown `group` | true | `unknown_group` (lists valid ids; hint to use `list_groups`) |

**Notifications.** When an `activate` / `deactivate` / `reset` call actually changes the visible tool set, the server emits the MCP `notifications/tools/list_changed` notification so clients that support `listChanged` refresh `ListTools` automatically. Idempotent calls (no state change) and `list_groups` never emit. Transport faults on the notification are isolated: the error is logged to stderr and the manage_tools result is still returned as a success.

**Non-goals.** Four actions only. Intent-driven activation (free-text task description) is not ported; persisting activation across MCP server restarts is not supported; `CallTool` is NOT filtered by active group (hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly).

### `godot_open_mcp_bridge_status`

- Route: `local` (local/live hybrid — synthesizes in the MCP server with one bounded `/ping` probe)
- Visibility group: always visible
- Read-only/mutating: read-only
- Live editor requirement: never spawns Godot

Operator-oriented bridge health snapshot. Composes the instance-lock classifier (`instance-discovery.ts#classifyInstance`) with a single `/ping` probe and returns a coarse `status` token so an operator (or the future Validation Suite) can branch recovery in one call.

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

- `ready` — coarse boolean for clients that want a single flag: `true` only when `status === "running"`.
- `classification` — top-level mirror of the instance-lock classification (`healthy | reloading | dead_bridge | gone`) so agents can branch on one field without digging into `instance`.
- `recoveryHint` — `{ tool, reason } | null`. Non-null **only** for `dead_bridge`. Names `godot_open_mcp_read_compile_errors` (the always-offline log reader) — it reads the project's Godot log from disk independent of the bridge, so it works in the exact state `dead_bridge` describes.
- `instance.lock` — `null` when no lock was read (no live instance known). The compact summary mirrors the operator-relevant fields; sensitive fields (`authToken`, `projectPath`) are not leaked.
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

### `godot_open_mcp_pull_events`

- Route: `local` (local-drains-live-stream — drains an MCP-process queue fed by a live SSE subscription)
- Visibility group: always visible
- Read-only/mutating: read-only
- Live editor requirement: live (requires a connected bridge for events; `connected:false` is a valid status when offline)

Drain incremental bridge Events (console logs + editor-state transitions) since the previous call. The MCP server holds one `BridgeEventStream` (a single SSE reader against the bridge's `GET /events`) per process; the first `pull_events` call opens the subscription, later calls return only new events buffered since the previous drain. Use this after mutations to stream console output without polling `/ping` or re-reading the full console (`console_get_logs`).

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
- `events[].seq` — monotonic sequence. For `log` events it matches the `console_get_logs` sequence (single fan-in from the collector), so the two surfaces share a cursor vocabulary. `editor_state` events use the bridge's own sequence space.
- `events[].type` — `log` (carries `logType`/`message`/optional `stack`), `editor_state` (carries `state`/`isCompiling`/`isPlaying`), plus control events `ready` / `missed` / `close` from the SSE wire.
- `dropped` — events evicted from the client-side queue (capacity 500) before this pull. Non-zero only under sustained burst.
- `connected` — whether the SSE reader is currently connected to the bridge.
- `started` — `true` only on the call that opened the subscription; `false` on subsequent calls.
- `lastError` — the last reconnect failure reason; non-null only when `connected` is false.

**Never throws on an offline bridge.** When the bridge is unreachable the tool returns `connected:false` + `lastError` (and `events:[]`) with `isError:false` so an agent can branch on the connection state. The reader reconnects automatically (2 s backoff) once the bridge is back.

**Capture scope.** `log` events come from the collector — the addon's captured activity (bridge lifecycle, tool-handler errors, routed game/script output when a supported hook is active), NOT the entire Godot editor Output panel. `editor_state` events fire at authoritative observed transitions (the bridge-driven play start/stop settle); a background observer for user-clicked play arrives in a later phase.

### `godot_open_mcp_read_compile_errors`

- Route: `offline` (always-offline — never probes the bridge, never spawns Godot)
- Visibility group: always visible
- Read-only/mutating: read-only
- Live editor requirement: none — this is the diagnostic that works when the bridge addon itself has failed to compile or load

Read C# compiler errors AND GDScript parse errors AND script/addon load failures directly from the project's configured Godot log file (offline, no bridge, no Godot spawn). The one diagnostic channel that works when the bridge addon itself has failed to compile or load: in that state every in-bridge channel (`console_get_logs`, `bridge_status`'s `/ping`) is dead with it, but the live Godot editor still writes parse/compile/plugin-load errors to the project log regardless of bridge health.

`console_get_logs` is the addon-captured collector; it stops accumulating once the bridge is dead. `read_compile_errors` reads the Godot-written log file straight from disk, so it surfaces the failure that preceded the dead bridge. The `dead_bridge` `bridge_status` recovery hint points here.

**Log path resolution** (precedence):

1. `GODOT_OPEN_MCP_LOG_FILE` env override — operator configuration supplied when launching the MCP server (e.g. when Godot is launched with `--log-file`). Absolute path.
2. Parsed `debug/file_logging/log_path` project setting — `user://`-relative when it starts with `user://`, otherwise absolute.
3. Default `user://logs/godot.log` under the per-platform user-data root (`~/Library/Application Support/Godot/app_userdata/<name>/` on macOS, `${XDG_DATA_HOME:-~/.local/share}/godot/app_userdata/<name>/` on Linux, `%APPDATA%/Godot/app_userdata/<name>/` on Windows). Custom user dir settings drop the `Godot/app_userdata/` segment.

**No arbitrary file-read surface.** The tool exposes NO `log_path` argument — only the operator env override + the resolved project path are honored. A caller cannot read arbitrary files.

**Input:**

- `tail_bytes` (optional, default `262144`, bounds `4096`–`1048576`) — maximum bytes read from the END of the log.
- `include_rotated` (optional, default `true`) — fall back to the newest rotated log (`godot.log.N`) when the current log is missing/empty. Strict `godot.log.<digits>` filename policy, bounded by `debug/file_logging/max_log_files`.
- `max_diagnostics` (optional, default `50`, bounds `1`–`200`) — cap on distinct diagnostics after dedup.

**Result envelope:** `{ status, unhealthy, headline, errorCount, warningCount, diagnostics, logPath, selectedLogKind, logSource, loggingDisabled, tailBytes, truncated, envOverrideUsed, staleLogSuspected?, staleLogNewerFiles?, staleLogHint?, logMtimeMs? }`.

- `status` — `compile_failed` (C#/GDScript/load errors present) | `warnings_only` | `no_errors_found` | `logging_disabled` (file logging off in project.godot + no log) | `log_not_found` (logging enabled but no log file yet).
- `unhealthy` — `true` when `status === "compile_failed"`.
- `diagnostics[]` — `{ kind, severity, file, line, column, code, message, raw }`. `kind` is `csharp | gdscript | script_load | addon_load | other`; `severity` is `error | warning`. Deduplicated by the normalized tuple, capped at `max_diagnostics`.
- `logPath` / `selectedLogKind` / `logSource` / `envOverrideUsed` — provenance for where the diagnostics came from.
- `staleLogSuspected` — advisory flag (with `staleLogNewerFiles` + `staleLogHint`) when a cited source file's mtime is newer than the log's. Never suppresses diagnostics.

**Non-error diagnostic statuses.** `logging_disabled`, `log_not_found`, `warnings_only`, and `no_errors_found` are SUCCESSFUL results (`isError:false`) — the tool succeeded; the file/state just has nothing to report. Only `project_not_found`, `project_config_unreadable`, `invalid_log_configuration`, `editor_log_unreadable`, and `offline_error` are hard errors.

**Limitations.** Godot crash backtraces may only print to the terminal and never reach the file log. A custom `--log-file` is only discoverable through `GODOT_OPEN_MCP_LOG_FILE`. File logging is OFF by default in Godot — `logging_disabled` is the expected status until `debug/file_logging/enable_file_logging = true` is set in project.godot (Project Settings > Debug > File Logging).

## Gate / verify tools

### `godot_open_mcp_validate_edit`

- Route: `live`
- Visibility group: `core`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Run a scoped read-only verify pass over res:// paths and return the health verdict + every issue found. This is the explicit form of the gate's validate step (the gate runs it implicitly on every mutating call in `enforce`/`warn` mode; this tool exposes it directly so an agent can inspect current state without mutating).

**Input:**

- `paths` (required, non-empty) — res:// asset paths to validate (e.g. `["res://Scenes/Main.tscn"]`). The verify rules scan these paths; there is no whole-project fallback.
- `categories` (optional) — rule-id filter. When omitted/empty, every registered rule runs (`broken_references`, `missing_scripts`, `import_health`). An unknown id returns a structured `unknown_rule` body listing the available rules.

**Result:** `{ passed, issues[], categoriesRun, rulesApplied, durationMs }`.

- `passed` — strict-error: any Error severity issue flips it to `false`.
- `issues[]` — `{ ruleId, categoryId, severity, code, issueCode, assetPath, description, evidence?, fixCandidates?, fixId?, fixSafe? }`. `categoryId` mirrors `ruleId` and `issueCode` mirrors `code` so agents can match the catalog field either way.
- `categoriesRun` / `rulesApplied` — the rule ids that actually ran.
- `durationMs` — wall-clock scan time.

**Errors:** `missing_parameter` (empty/absent `paths`), `unknown_rule` (unknown `categories` id — the tool still succeeds and lists the available rules).

### `godot_open_mcp_checkpoint_create`

- Route: `live`
- Visibility group: `core`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Capture a project-health baseline over res:// paths and stash it in a session-scoped (in-memory) store so a later `godot_open_mcp_delta` call can compute the before/after issue delta. This is the explicit form of the gate's checkpoint step.

**Input:**

- `paths` (optional, recommended) — res:// paths to capture in the baseline. When omitted/empty, the baseline covers nothing and every post-mutation issue counts as new.
- `label` (optional) — human-readable label for the checkpoint (logging/UI only; not part of identity).

**Result:** `{ checkpointId, timestamp, fingerprint }`.

- `checkpointId` — opaque resume key to pass to `delta`.
- `fingerprint` — per-rule map of `errors` / `warnings` / `issueKeys[]` (canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` keys).

**Session lifetime.** Checkpoints are session-scoped: they are cleared on script recompile, assembly reload, or editor restart. A `delta` against a cleared checkpoint returns a structured `unavailable` payload (NOT a hard error) so the agent can fall back to `validate_edit`.

### `godot_open_mcp_delta`

- Route: `live`
- Visibility group: `core`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Compare the current project state against a previously captured checkpoint and return the new/resolved issue delta.

**Input:**

- `checkpoint_id` (required) — opaque id returned by `checkpoint_create`.
- `paths` (optional) — res:// paths to re-validate; defaults to the paths captured at checkpoint time. Pass a narrower scope to delta only a subset.

**Result:** `{ passed, unavailable?, summary, newIssues[], resolvedIssues[], agentNextSteps? }`.

- `passed` — strict on new errors: any new Error severity issue (present after the mutation but absent at checkpoint) flips it to `false`; pre-existing issues never count as new.
- `summary` — `{ newErrors, newWarnings, resolvedErrors, resolvedWarnings }`.
- `newIssues[]` / `resolvedIssues[]` — canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` keys.
- `unavailable` — `true` when the checkpoint id is no longer in the in-memory store. The tool returns `passed:true` + `unavailable:true` + `agentNextSteps[]` recovery guidance (NOT a hard error) so the agent can fall back to `validate_edit` for a direct current-state check.

**Errors:** `missing_parameter` (empty/absent `checkpoint_id`).

### `godot_open_mcp_apply_fix`

- Route: `live`
- Visibility group: `core`
- Read-only/mutating: mutating (writes disk on a non-dry-run apply); default `gate: "off"` in the schema, but a non-dry-run apply under `enforce` runs the full gate cycle with safe auto-fix rollback
- Live editor requirement: requires the bridge

Apply (or preview) a structured fix for a verify issue.

**Input:**

- `issue_id` (required) — canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` key (copy verbatim from a `validate_edit` / gate-delta issue).
- `fix_id` (optional) — the fix to apply (e.g. `remove_missing_script`). Omit to list every fix that can resolve the issue.
- `dry_run` (optional, default `true`) — `true` previews; `false` applies.
- `paths_hint` (required for non-dry-run under `enforce`/`warn`) — res:// paths the gate should checkpoint and re-validate (e.g. the issue's `assetPath`).
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:**

- Omitting `fix_id` — `{ ok, applicableFixes[] }` (each fix's Safe flag is in the capabilities catalog).
- `fix_id` + `dry_run: true` — `{ ok, fixId, description, safe }`.
- `fix_id` + `dry_run: false` — `{ ok, applied, fixId, touchedPaths[], rollback? }`. A non-dry-run apply under `enforce` that fails or introduces new errors is restored to its pre-fix state; the response then carries a top-level `rollback` block (`rolledBack`, `reason`, `restoredPaths`) — no project change remains.

**Errors:** `missing_parameter` (empty `issue_id`), `invalid_issue_id` (malformed key), `fix_not_applicable`, `fix_failed`, `fix_error`. An unknown `fix_id` returns `ok:true` with an `error.code:unknown_fix` body listing available + applicable fix ids.

**Safe provider.** The initial `Safe:true` provider is `remove_missing_script` (resolves `missing_scripts|missing_script` by removing the broken script attachment from a `.tscn`/`.tres`).

## Node tools

Node tools operate on the currently edited scene. `node_path` / `node_paths` use scene-tree paths relative to the edited scene root (`Main/Player`, `/root/Main/Player`, or `.` for the root itself). Every NodeData result carries `instanceId` (live process id; `null` offline), `name`, `path`, `type`, `scriptResourcePath`, `childCount`, and optional `children` per `hierarchy_depth`.

### `godot_open_mcp_node_find`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Find Nodes in the currently edited Godot scene. Two modes: (a) targeted lookup by `node_path` or `name` — returns a single-node result (empty list when not found, with `notFound: true`); (b) list mode (omit both) — walks the edited scene with optional type / name_contains filters, bounded by `max_results`.

**Selector precedence:** `node_path` > `name`. Prefer `node_path` over `name` — `name` matches the first hit only and can be ambiguous in a large scene.

**Input:**

- `node_path` (optional, priority 1) — targeted mode scene-tree path.
- `name` (optional, priority 2) — targeted mode node name (first depth-first match).
- `type` (optional, list mode) — Godot class name (`Node3D`, `Sprite2D`, …). Case-sensitive exact match against each node's `GetClass()`.
- `name_contains` (optional, list mode) — case-insensitive substring filter.
- `hierarchy_depth` (optional, default `0`, minimum `0`) — depth of children in each NodeData. `0` = matched node only; `1` = direct children; etc. Applies in both modes.
- `max_results` (optional, default `50`, minimum `1`) — list mode bound. Remainder reported in `truncated`.

**Result:** `{ nodes: NodeData[], notFound?, truncated? }`. An empty targeted result returns `notFound: true` (success, not error).

**Errors:** `edited_scene_unavailable` (no edited scene), `invalid_node_path` (malformed path).

### `godot_open_mcp_node_create`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create a new Node in the currently edited Godot scene and return its NodeData. Two creation modes: (1) typed — pass `type_class_name` (a Godot class like `Node3D`, `Sprite2D`, `CharacterBody3D`; defaults to `Node`); (2) instanced scene — pass `instance_scene_path` (a `res://` path to a `.tscn`/`.scn` PackedScene), which takes precedence when both are set.

The new Node's owner is set to the edited scene root so it persists in the `.tscn` on save; the scene is marked unsaved.

**Input:**

- `name` (optional) — Node name. When omitted, Godot assigns a default name for the type/scene.
- `type_class_name` (optional, default `Node`) — Godot class name to instantiate via ClassDB. Used when `instance_scene_path` is not provided.
- `instance_scene_path` (optional) — `res://` path to a PackedScene to instance. Takes precedence over `type_class_name` when both are supplied.
- `parent_node_path` (optional, default edited scene root) — scene-tree path of the parent.
- `position` (optional) — `'x,y,z'` (Node3D) or `'x,y'` (Node2D). Applied only when the new Node is a Node3D / Node2D.
- `rotation` (optional) — degrees, same shape as `position`. Applied only when the new Node is a Node3D / Node2D.
- `scale` (optional) — same shape as `position`. Applied only when the new Node is a Node3D / Node2D.
- `paths_hint` (optional) — mutation scope (the edited scene `res://` path).
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ node: NodeData }`. Tagged with the standard `gate` block when `gate` is not `off`.

**Errors:** `edited_scene_unavailable`, `invalid_node_path`, `parent_not_found`, `type_class_not_found`, `type_class_not_instantiable`, `instance_scene_not_found`, `instance_scene_invalid`, `invalid_transform` (malformed vector / wrong arity).

### `godot_open_mcp_node_modify`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Modify properties of one or more Nodes in the currently edited Godot scene. Two target shapes: (1) single — pass `node_path`; (2) batch — pass `node_paths`. At least one is required.

**Input:**

- `node_path` (optional) — single-target scene-tree path. When both `node_path` and `node_paths` are set, the union is applied (de-duplicated).
- `node_paths` (optional) — batch-target scene-tree paths.
- `properties` (optional) — free-form string→string property map. Keys are property names; values are raw strings the handler coerces.
- `position` (optional) — `'x,y,z'` / `'x,y'`. Overrides `properties.position` on collision.
- `rotation` (optional) — degrees. Overrides `properties.rotation`.
- `scale` (optional) — Overrides `properties.scale`.
- `name` (optional) — new name (per target). Overrides `properties.name`.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Supported property keys:** `visible` (bool), `name` (string), `position` / `rotation` / `scale` (vector strings, Node3D/Node2D), `modulate` (Color `'r,g,b[,a]'`, CanvasItem only).

**Result:** `{ nodes: NodeData[], warnings[] }`. The batch does NOT abort on per-target issues:

- Unknown property key → `unsupported_property` warning (non-aborting).
- Invalid value for a known key → `invalid_property_value` warning (per-target, non-aborting).
- Per-target resolution miss → `node_not_found` warning (non-aborting).

**Errors:** `missing_parameter` (no `node_path` and no `node_paths`), `edited_scene_unavailable`, `paths_hint_required` (when `gate` is not `off`).

### `godot_open_mcp_node_set_parent`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Reparent a Node under a new parent via Godot's `Node.Reparent`. Cycle-safe: refuses to reparent the edited scene root, a Node under itself, or a Node under one of its own descendants. The reparented sub-tree's owner is reset to the edited scene root so it persists in the `.tscn` on save.

**Input:**

- `node_path` (required) — scene-tree path of the Node to reparent.
- `parent_node_path` (required) — scene-tree path of the new parent (must already exist).
- `keep_global_transform` (optional, default `true`) — when `true`, preserve the Node's global transform; when `false`, keep its local transform.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ node: NodeData }`.

**Errors:** `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `parent_not_found`, `cannot_reparent_root`, `cycle_detected` (proposed parent is the node itself or one of its descendants).

### `godot_open_mcp_node_duplicate`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Duplicate a Node (and its whole sub-tree) via Godot's `Node.Duplicate`, adding the copy as a sibling under the same parent by default. The duplicate's owner (and the owner of its owner-less sub-tree) is set to the edited scene root so the copy persists in the `.tscn` on save. The duplicate is selected.

**Input:**

- `node_path` (required) — scene-tree path of the Node to duplicate. Duplicating the edited scene root is refused — pick a child node.
- `new_name` (optional) — name for the duplicate. When omitted, Godot assigns a default name. No collision guard — passing an existing sibling name leaves two siblings with the same name.
- `parent_node_path` (optional, default source's parent) — destination parent. When provided, the duplicate is added under that parent instead.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ node: NodeData }`.

**Errors:** `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `cannot_duplicate_root`, `parent_not_found`.

### `godot_open_mcp_node_delete`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (editor state; deletes are IRREVERSIBLE without a save; marks the scene unsaved)
- Live editor requirement: requires the bridge

Delete one or more Nodes (and all of their children) from the currently edited Godot scene. Nodes are removed from their parent and freed synchronously (`Node.Free` — required for editor-mode edits; `QueueFree` would defer past the tool call).

**Input:**

- `node_path` (optional) — single-target scene-tree path.
- `node_paths` (optional) — batch-target scene-tree paths.
- `fail_if_has_children` (optional, default `false`) — when `true`, refuse to delete any target that has children (the target is skipped with a `has_children` warning; the rest of the batch still proceeds). Default `false` deletes the whole sub-tree.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ deleted: string[], count, warnings? }`. Per-target resolution misses become `node_not_found` warnings (the rest of the batch still deletes). Refuses to delete the edited scene root — close or replace the scene instead.

**Errors:** `missing_parameter`, `edited_scene_unavailable`, `cannot_delete_root`.

## Scene tools

Scene tools operate on the editor's open-scene tabs. `path` arguments are `res://` paths ending in `.tscn` or `.scn`.

### `godot_open_mcp_scene_open`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (switches the active edited scene)
- Live editor requirement: requires the bridge

Open a Godot scene asset (`res://*.tscn` / `*.scn`) in the editor and make it the active/edited scene.

**Input:**

- `path` (required) — `res://` path of the scene file. Must start with `res://` and end with `.tscn` or `.scn`.
- `ignore_dirty` (optional, default `false`) — when `true`, proceed even if the current edited scene has unsaved changes (discarding them on open). Default `false`: the bridge refuses with `scene_dirty` so an agent does not lose edits by switching scenes. Godot would otherwise pop a native save modal; the bridge avoids modal dialogs entirely.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ scene: SceneSummary, previous: SceneSummary | null }`. `SceneSummary` is `{ path, name, isDirty, rootType, isActive }`. `previous` is the previously-edited scene (or `null` when no scene was edited).

**Errors:** `missing_parameter`, `invalid_path`, `scene_not_found`, `scene_dirty` (current edited scene has unsaved changes — pass `ignore_dirty: true` or call `scene_save` first).

**`isDirty` caveat.** `isDirty` reflects bridge-tracked state from tool mutations, not edits a human made directly in the editor (Godot 4.3 has no public dirty-state query API).

### `godot_open_mcp_scene_save`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (writes a `.tscn` to disk)
- Live editor requirement: requires the bridge

Save the currently edited Godot scene. Three modes: (1) `save_all: true` — save every open scene tab; (2) `path` set — save-as the edited scene to a new `res://` destination; (3) neither — save the edited scene back to its existing file.

**Input:**

- `path` (optional) — `res://` destination path (ending in `.tscn` or `.scn`) for save-as. Ignored when `save_all` is `true`.
- `save_all` (optional, default `false`) — save every open scene tab. Godot 4.3 has no API to save a non-edited open scene directly, so the handler iterates the open-scene paths, opens and saves each, then restores the originally-edited scene.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ saved: string[], count, failed?: string[] }`. Save-as verifies the edited scene's file path was re-pointed to the target (Godot's `SaveSceneAs` returns no error code).

**Errors:** `invalid_path`, `scene_not_found`, `save_failed` (the edited scene was never saved — pass `path` to save it for the first time), `path_exists` (save-as to an existing path).

### `godot_open_mcp_scene_list_opened`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

List every scene currently open in the Godot editor as a shallow snapshot.

**Input:** empty object.

**Result:** `{ scenes: SceneSummary[], editedPath: string | null }`. The active scene carries its root Node's name and type; non-active open scenes report the path + file stem only (Godot 4.3 exposes no root accessor for non-active open scenes). A freshly-created unsaved scene is surfaced explicitly even when not listed by the editor. `editedPath` is the active/edited scene path (or `null` for a never-saved scene).

**Errors:** `edited_scene_unavailable` (no scenes open).

### `godot_open_mcp_scene_get_data`

- Route: `live-first`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: live preferred; offline reads the `.tscn` from disk

Read the hierarchy of a Godot scene as a structured NodeData tree. The first **live-first / offline-fallback** tool: when the Godot editor is running it reads the currently edited scene and reflects unsaved editor state; when the editor is unavailable it parses the `.tscn` from disk with no Godot process required.

**Routing:**

- **Live (bridge reachable):** forwards to the bridge handler, which walks the edited scene root via `NodeTools.ToNodeData`. `path` is optional — omit to read the edited scene; set it to assert the edited scene matches (refuses with `scene_not_edited` otherwise, since switching scenes is a mutating op that belongs to `scene_open`). Result carries live instance IDs and the bridge-tracked `isDirty` flag.
- **Offline (bridge unreachable):** parses the `.tscn` text directly. `path` is **required** (there is no edited-scene context to fall back on); omitting it returns `path_required_offline`. The result is normalized to the same envelope as the live read, with the deltas below.

**Input:**

- `path` (string) — `res://` path of the scene. Optional live (asserts the edited scene); required offline.
- `hierarchy_depth` (integer, default `1`, minimum `-1`) — `0` = root node only; `1` = root + direct children; `N` = N layers; `-1` = the whole tree. Positive values are capped at `5` to bound the response (deeper trees blow the token budget — drill in with `node_find` instead).

**Result envelope** (shared live + offline shape): `{ path, name, isDirty, rootType, hierarchyDepth, root }` where `root` is a NodeData. The offline read adds:

- `stateSource: "disk"` — marks the read as disk-origin so a client never mistakes it for live.
- `isDirty: false` — offline state has no unsaved edits.
- `root.instanceId: null` — no live instance IDs offline (never a fake hashed id).
- `warnings` — present only when non-empty (e.g. an inherited scene whose base is not expanded offline).

**Offline error codes:** `path_required_offline`, `invalid_path` (non-`res://`, traversal, wrong extension, invalid characters), `path_outside_project` (symlink or canonical escape), `scene_not_found`, `scene_unreadable` (permission or non-regular file), `scene_too_large` (exceeds the 8 MiB read cap), `scene_parse_error` (malformed `.tscn`), `scene_hierarchy_invalid` (orphan/duplicate/cyclic parent graph).

**Live error codes:** `scene_not_edited` (the asserted `path` does not match the edited scene), `edited_scene_unavailable`, `invalid_path`.

### `godot_open_mcp_scene_create`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (writes a new `.tscn` to disk; optionally switches the edited scene)
- Live editor requirement: requires the bridge

Create a new Godot scene asset (`.tscn`) at a `res://` path and optionally open it as the active scene. A root Node is created (class given by `root_type`, default `Node2D`), packed into a PackedScene, saved via ResourceSaver, then opened in the editor (unless `open: false`).

**Input:**

- `path` (required) — `res://` path for the new scene file. Intermediate parent directories are created if missing.
- `root_type` (optional, default `Node2D`) — Godot class for the scene's root Node. Must be a ClassDB-instantiable class.
- `root_name` (optional) — name for the root Node. When omitted, defaults to a PascalCased derivation of the filename stem (`res://levels/level_2.tscn` → `Level2`).
- `overwrite` (optional, default `false`) — when `true`, replace a scene file that already exists at `path`. Default `false`: refuses with `path_exists`.
- `open` (optional, default `true`) — when `true`, open the newly-created scene as the active/edited scene. Pass `false` to create the file without switching.
- `paths_hint` (optional) — mutation scope.
- `gate` (optional, default `off`) — `enforce` | `warn` | `off`.

**Result:** `{ created, opened, path, name, rootType, root? }`. When `open` is `true`, `root` is the new scene's root NodeData so an agent can chain straight into `node_create` to populate it.

**Errors:** `missing_parameter`, `invalid_path`, `path_exists` (target already exists — pass `overwrite: true`), `root_type_invalid` (class missing/abstract/not instantiable), `save_failed`, `open_failed`.

## Resource tools

Resource tools operate on `.tres` / `.res` files. Direct selectors accept `res://` paths or `uid://` identifiers (mapped first via `ResourceUid`).

### `godot_open_mcp_resource_find`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Find Godot resources (`.tres`/`.res`) in the project's `res://` filesystem. Two modes: direct lookup (by `uid` or `resource_path`) resolves a single resource; indexed type search (by `type_filter`) recursively scans `EditorFileSystem` for files whose importer-assigned type equals or derives from the filter — without eagerly loading every candidate.

**Selector precedence:** `uid` > `resource_path` > `type_filter`. A direct selector resolves at most one resource and ignores search-only options. At least one selector is required.

**Input:**

- `uid` (optional) — priority 1: `uid://` identifier.
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

**Errors:** `invalid_request` (no selector), `invalid_path`, `resource_not_found` (path/uid does not resolve), `filesystem_unavailable` (editor filesystem not ready).

### `godot_open_mcp_resource_get_data`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Load a Godot resource (`.tres`/`.res`) and return a bounded, cycle-safe property tree. Object references that would create a cycle or an unbounded graph are represented by a descriptive reference leaf (`res://` path + `uid` when available), never blindly traversed — process-local instance IDs are not exposed as durable identity.

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

**Errors:** `missing_parameter` (no `resource_path`), `invalid_path` (bad scheme/extension), `resource_not_found`, `resource_load_failed` (`ResourceLoader.Load` fails), `serialization_failed` (property access/serialization fails safely).

**Tip:** prefer `compact` first, then drill in with `property_path` or a higher `max_depth`. `full` can be expensive on large resources.

### `godot_open_mcp_resource_create`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (writes a new `.tres`/`.res` to disk)
- Live editor requirement: requires the bridge

Create a new Godot resource (`.tres`/`.res`) by instantiating a `Resource` subclass via `ClassDB` and persisting it through `ResourceSaver`. The destination must not already exist — no overwrite. Optional initial properties are validated and applied before the first save (all-or-nothing; a single bad patch aborts the create with no file written).

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

The first segment must be a property name. Values are raw JSON converted to the property's `Variant.Type`: `bool`, `int`, `float`, `string`, vectors as `[x,y,...]`, colors as `[r,g,b,a]`, resource refs as `{"resource_path":"res://..."}`, or `null` to clear.

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

**Errors:** `missing_parameter`, `paths_hint_required`, `invalid_path`, `resource_exists`, `resource_type_invalid`, `patch_invalid`, `value_type_mismatch`, `no_changes`, `resource_save_failed`, `filesystem_unavailable`.

### `godot_open_mcp_resource_modify`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (writes the `.tres`/`.res` to disk)
- Live editor requirement: requires the bridge

Modify writable properties of an existing Godot resource (`.tres`/`.res`) through explicit property-path assignments, then persist via `ResourceSaver`. All patches are validated atomically before any mutation — a single bad path or value aborts with the resource untouched. No-op detection reports `no_changes` (save skipped) when all patches already match. Imported/generated resources (those with a `.import` sidecar) are rejected with `resource_not_writable`.

**Input:**

- `resource_path` (required) — `res://` path or `uid://` (mapped first).
- `patches` (required, non-empty) — array of `{ path, value }` property patches (see grammar under `resource_create`).
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

**Errors:** `missing_parameter`, `paths_hint_required`, `invalid_path`, `resource_not_found`, `resource_load_failed`, `resource_not_writable`, `patch_invalid`, `value_type_mismatch`, `no_changes`, `resource_save_failed`, `filesystem_unavailable`.

### `godot_open_mcp_resource_move`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (renames a `.tres`/`.res` file + `.import` sidecar)
- Live editor requirement: requires the bridge

Move a Godot resource file (`.tres`/`.res`) and its `.import` sidecar to a new `res://` destination via `DirAccess.RenameAbsolute`. The destination must not already exist — no overwrite. The destination parent directory is created when missing. The `.import` sidecar is moved alongside the primary file when present.

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

**Errors:** `missing_parameter`, `paths_hint_required`, `invalid_path`, `same_path`, `resource_not_found`, `destination_exists`, `resource_move_failed`, `resource_move_partial`, `filesystem_unavailable`.

### `godot_open_mcp_resource_delete`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (deletes a `.tres`/`.res` file + `.import` sidecar)
- Live editor requirement: requires the bridge

Delete a Godot resource file (`.tres`/`.res`) and its `.import` sidecar via `DirAccess.RemoveAbsolute`. Delete is explicit and immediate — no soft-delete or recycle bin. A pre-delete identity snapshot (`path`/`uid`/`type`) is returned so the agent has a durable record of what was removed. The `.import` sidecar is removed alongside the primary file when present.

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

**Errors:** `missing_parameter`, `paths_hint_required`, `invalid_path`, `resource_not_found`, `resource_delete_failed`, `resource_delete_partial`, `filesystem_unavailable`.

## Filesystem tools

### `godot_open_mcp_filesystem_list`

- Route: `live-first`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: live preferred; offline reads the directory from disk

List the immediate children of one `res://` directory. The second **live-first / offline-fallback** tool: when the Godot editor is running it reads the editor's indexed filesystem for authoritative importer resource type + UID; when the editor is unavailable it lists the directory straight from disk with best-effort extension metadata.

**Routing:**

- **Live (bridge reachable):** forwards to the bridge handler, which walks `EditorFileSystemDirectory` and reads file types from `GetFileType` + UIDs from `ResourceLoader.GetResourceUid` (both read the import index). No resource is loaded.
- **Offline (bridge unreachable):** lists the directory from disk via `readdir`. The result carries `stateSource: "disk"`, the resource type is a best-effort guess from the file extension, and `uid` is always `null` (the UID table lives in `.godot/` import state, which the offline reader refuses to read). The response shape is otherwise identical to the live read.

**Input:**

- `path` (optional) — `res://` directory (trailing slash optional); omit or pass `res://` for the project root. Resolved safely beneath the project root — traversal and symlink escapes are rejected.
- `page_size` (optional, default 100, max 500) — max entries per page (directories + files combined, directories first).
- `cursor` (optional) — opaque continuation cursor from a previous response's `pagination.nextCursor`. Offline: a cursor that belongs to another directory, or whose directory has changed since the previous page, is rejected (`invalid_cursor` / `stale_cursor`).
- `include_hidden` (optional, default `false`) — include hidden entries (dotfiles) the listing would otherwise skip. Engine/import internals (`.godot/`) and VCS directories (`.git`, `.hg`, `.svn`, `node_modules`) are **always** excluded — `include_hidden` does not expose them.

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

`directoryCount`/`fileCount` describe the full one-level directory; `entries` is the current page (directories first, then files, each group sorted by name with a locale-independent ordinal compare so the order is deterministic across environments).

**Offline resource-type mapping** (best-effort, by extension): `.tscn` → `PackedScene`, `.tres` → `Resource` (generic; the serialized sub-type is inside the file, not the extension), `.gd` → `GDScript`, `.cs` → `CSharpScript`, `.gdshader` → `Shader`. All other extensions (images, audio, fonts, `.gdshaderinc`) surface `resourceType: null` rather than a guess — offline never claims importer authority.

**Errors:** `invalid_path`, `path_outside_project`, `directory_not_found`, `directory_unreadable`, `invalid_cursor`, `stale_cursor`, `filesystem_unavailable`. `project_not_found` / `project_config_unreadable` surface when the project root lacks a valid `project.godot` marker.

### `godot_open_mcp_filesystem_reimport`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (reimports files; may write `.import` sidecars)
- Live editor requirement: requires the bridge

Reimport specific `res://` files via `EditorFileSystem.ReimportFiles`, or trigger a full `EditorFileSystem.Scan` when no files are given. The Godot analog of Unity's `AssetDatabase.Refresh`.

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

**Errors:** `paths_hint_required`, `invalid_path`, `file_not_found`, `filesystem_unavailable`, `reimport_failed`.

## Editor state / selection / console tools

### `godot_open_mcp_editor_application_get_state`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Get a truthful snapshot of the Godot editor's play-process state.

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

### `godot_open_mcp_editor_application_set_state`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (changes editor runtime state)
- Live editor requirement: requires the bridge

Start or stop the Godot editor's play process. The play lifecycle writes no files, but the gate still runs because the tool changes editor/project runtime state (the verify delta is clean in the common case).

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

**Errors:** `paths_hint_required`, `invalid_scene_selector`, `scene_not_found`, `current_scene_unavailable`, `already_playing`, `play_start_failed` / `play_stop_failed` (the `EditorInterface` API threw — observed state surfaced), `state_transition_timeout` (the requested state was not observed within `timeoutMs` — last observed state surfaced under `result`; a caller can safely follow up with `editor_application_get_state`).

**No automatic save.** A play start does not save the edited scene first. A `current` start requires a saved edited scene (a path); a freshly-created unsaved scene yields `current_scene_unavailable`.

### `godot_open_mcp_editor_selection_get`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Get the Godot editor's current node selection as structured data.

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

An empty selection (`count: 0`, `activeNode: null`) is a success, not an error. `scenePath` is the active edited scene path (or `null` when no scene is edited).

### `godot_open_mcp_editor_selection_set`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (changes editor selection state)
- Live editor requirement: requires the bridge

Set the Godot editor's node selection to the provided nodes (replacing any current selection). The selection write changes no files, but the gate still runs because the tool changes editor selection state (the verify delta is clean in the common case).

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

**Errors:** `paths_hint_required`, `selection_limit_exceeded` (request exceeds the hard maximum of 256 nodes), `edited_scene_unavailable`, `node_not_found` (a ref cannot resolve — names the offending index), `node_not_in_edited_scene`, `duplicate_node`, `selection_update_failed` (observed post-state differs from requested — observed state surfaced), `selection_unavailable` (`EditorInterface.GetSelection()` returned null).

### `godot_open_mcp_console_get_logs`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge (the collector is fed by the bridge's own logging path)

Retrieve captured Godot Open MCP log lines, newest-first.

**NOTE:** Godot's C# API exposes no global managed log hook at the 4.3 baseline, so this returns the addon's own captured activity (bridge lifecycle, tool-handler errors, routed game/script output when a supported hook is active) — NOT the entire Godot editor Output panel. The response carries explicit capture-capability metadata so callers do not over-trust the contents. Each entry has a monotonic `sequence` (for future event-stream cursor use), a `logType` (`log`/`warning`/`error`), the `message`, a UTC `timestamp`, an optional `stackTrace`, and a `source` (`bridge`/`script`/`engine`/`tool`).

**Input:**

- `max_entries` (optional, default 100, [1, 1000]) — max entries to return (newest-first, so the cap keeps the most recent).
- `log_type_filter` (optional) — array of `"log"` | `"warning"` | `"error"`; omitted means all. Multiple values are unioned.
- `include_stack_trace` (optional, default `false`) — include stack traces.
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

### `godot_open_mcp_console_clear_logs`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating — gate-free direct (mutates only ephemeral addon-owned collector state)
- Live editor requirement: requires the bridge

Clear the Godot Open MCP log cache (read by `console_get_logs`). Mutates only ephemeral addon-owned collector state, not project files or Godot editor state. Checkpoint/delta cannot meaningfully cover ephemeral memory, so there is no `paths_hint` and no gate surface.

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

## Screenshot tools

Three read-only (gate-free) tools that capture images and return them as **MCP image content blocks** (`image/png`). Unlike every other tool, the success result is a two-block content array: the image first, then a short text block with capture metadata (width, height, byteLength, mode, caption, clamped). The base64 PNG payload never appears inside the text JSON block on success — the MCP server detects the bridge image envelope (`mediaType: "image/png"` + non-empty `data`) and unwraps it into the image block. Error responses stay structured text errors.

All three tools create transient editor render nodes (an off-screen `SubViewport`, a clone camera, a light, optionally a `WorldEnvironment`) but free them on every path — success, error, and timeout — and write no project files. Headless / no-GPU environments produce a structured `render_unavailable` / `empty_image` error rather than a blank image.

**Shared limits:**

- Positive dimensions only (>= 1 px); caller requests above 16384 px are rejected.
- The longest encoded edge is clamped to **3840 px** (aspect preserved). The result reports `clamped: true` when downscaling was applied.
- The encoded PNG must stay under an **8 MB** transport ceiling. If it still exceeds that after dimension clamping, the tool rejects it with `image_too_large`.

### `godot_open_mcp_screenshot_viewport`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge (editor viewport + GPU)

Capture the active Godot editor 2D or 3D viewport. The viewport is read back in-memory via `EditorInterface.GetEditorViewport2D()` / `GetEditorViewport3D(0)`; no file is written.

**Input:**

| Field | Type | Default | Notes |
|---|---|---|---|
| `mode` | `"2d"` \| `"3d"` | `"3d"` | Which editor viewport to capture. |

**Result:** image content block (`image/png`) + text metadata block (`{ mediaType, width, height, byteLength, mode, caption, clamped, source }`).

**Errors:** `invalid_capture_mode` (unknown mode), `viewport_unavailable` (no open viewport), `render_unavailable` (no GPU / `--headless` / readback failure), `empty_image` (readback produced no pixels), `png_encode_failed`, `image_too_large`.

### `godot_open_mcp_screenshot_camera`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge (edited scene + GPU)

Render an off-screen capture from a `Camera2D` or `Camera3D` in the edited scene. The source camera is never moved — a transient `SubViewport` shares the camera's world (`World3D` / `World2D`) and clones its transform/projection so the off-screen render sees the same content.

**Input:**

| Field | Type | Default | Notes |
|---|---|---|---|
| `node_ref` | object | (required) | `{ instance_id?, node_path? }` — `instance_id` is priority 1, `node_path` priority 2. Must resolve to a `Camera2D` or `Camera3D`. |
| `width` | integer | 1920 | Clamped to a 3840 px longest edge (aspect preserved). |
| `height` | integer | 1080 | Clamped to a 3840 px longest edge (aspect preserved). |

**Result:** image content block + text metadata block (`{ mediaType, width, height, byteLength, mode:"camera", caption, clamped, source }`).

**Errors:** `invalid_dimensions`, `node_not_found`, `invalid_camera_node` (target is not `Camera2D`/`Camera3D`), `render_unavailable`, `empty_image`, `png_encode_failed`, `image_too_large`.

### `godot_open_mcp_screenshot_isolated`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge (GPU)

Render a `Node3D` alone in an isolated world from one of six directions. The target is duplicated **without its scripts** (so `_EnterTree`/`_Ready` side effects never run) into a fresh `SubViewport` with `OwnWorld3D = true`, framed by a transient camera computed from the combined AABB of the target's `VisualInstance3D` descendants. An empty/degenerate geometry falls back to a 0.1-unit bounds box (`usedFallbackBounds: true` in the metadata).

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

## Reflection tools

Reflection tools target C# types/methods/properties across loaded Godot/.NET assemblies. Prefer typed tools (`node_*`, `scene_*`, `resource_*`) when one fits — reflection is the escape hatch.

### `godot_open_mcp_reflection_method_find`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge (assembly inventory is in-process)

Discover C# types, methods, and properties across loaded Godot/.NET assemblies so an agent can plan `reflection_method_call` invocations against the actually-installed assemblies instead of hallucinating signatures. Token-bounded: `max_results` caps the returned list and `truncated` reports how many additional matches were dropped.

**Input:**

- `query` (optional) — substring filter (case-insensitive) matched against type names, full names, and member names. Empty/unset still scans (bounded by `max_results`).
- `kind` (optional, default `all`) — `type` | `method` | `property` | `all`. An unrecognized value normalizes to `all`.
- `assembly_filter` (optional) — assembly simple-name contains filter (case-insensitive). When set, it wins over `include_godot_editor` / `include_project` (both are ignored).
- `include_godot_editor` (optional, default `true`) — include Godot editor assemblies (names starting with `GodotSharpEditor` or `Godot.*Editor`).
- `include_project` (optional, default `true`) — include the game/scripts assembly (the one assembly not prefixed by a framework/engine root).
- `include_signatures` (optional, default `true`) — include the flat signature string and structured parameter/generic metadata on each member. Set `false` for a lighter names-only payload.
- `type_name` (optional) — limit the member enumeration to a single declaring type (full or simple name). When set, `query` is still applied to the type's members.
- `max_results` (optional, default `50`, [1, 200]) — max members returned. Additional matches counted in `truncated`.

**Result:** `{ members: MemberEntry[], truncated }`. Each member carries a flat `signature` string AND structured fields (`returnType`, `parameters[]`, `isStatic`, `isGeneric`, `genericParameters[]` for methods; `propertyType`, `canRead`, `canWrite` for properties) so an agent can pick a specific overload and call it. When `query` matches a method with overloads, every overload is listed separately.

**Errors:** `invalid_kind` (unrecognized value — normalized to `all` rather than erroring in v1).

### `godot_open_mcp_reflection_method_call`

- Route: `live`
- Visibility group: `typed-editor`
- Read-only/mutating: mutating (default gate `enforce`; runs the full checkpoint → invoke → validate → delta cycle)
- Live editor requirement: requires the bridge (the invoke runs on the editor main thread)

Invoke a C# method via reflection — static or instance — and return a JSON-serializable result. Use `reflection_method_find` first to discover exact signatures.

**Input:**

- `type_name` (required) — declaring type full name (preferred) or simple name. `assembly_name` disambiguates when the simple name is ambiguous.
- `method_name` (required) — method name to invoke. Overloads are disambiguated by `arg_type_names`.
- `args` (optional) — positional arguments (JSON-serializable). Coerced to the resolved parameter types: primitives (`bool`/`int`/`long`/`float`/`double`/`string`/`char`), enums (by name, case-insensitive), `Nullable<T>`. Nested objects/arrays are passed as raw JSON strings in v1.
- `arg_type_names` (optional) — explicit parameter type names (full or simple) used to disambiguate overloads when multiple methods share `method_name`. Length must match the overload's parameter count. CLR aliases (`int`/`Int32`, `float`/`Single`, …) are accepted.
- `generic_arg_types` (optional) — type-name strings substituted for the method's generic parameters when invoking a generic method. Length must match the method's generic parameter count.
- `is_static` (optional, default `false`) — `true` to invoke a static method (no instance target).
- `assembly_name` (optional) — assembly simple name to disambiguate an ambiguous `type_name`.
- `node_path` (optional, primary instance target) — scene-tree path relative to the edited scene root. The resolved node must be assignable to `type_name`. Required for instance methods on `Godot.Object` subclasses.
- `object_id` (optional, default `0`) — secondary instance target (a stable handle registry id). v1 has no handle registry, so a non-zero `object_id` fails with `unsupported_target`. Prefer `node_path`.
- `execute_in_main_thread` (optional, default `true`) — echoed in the result; the invoke always runs on the main thread in v1 (forward-compat flag for a future off-thread path).
- `max_depth` (optional, default `4`, minimum `0`) — max recursion depth when serializing the returned object graph.
- `max_items` (optional, default `100`, minimum `0`) — max items emitted per list/enumerable in the returned object graph. Truncated lists report a `__truncated__` count.
- `paths_hint` (required) — mutation scope; the gate validates only these paths after the invoke. Mandatory (the dispatcher rejects an empty hint with `paths_hint_required` before the invoke runs).
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.
- `timeout_ms` (optional, default `30000`) — max milliseconds to wait for the invoke to complete on the main thread.

**Result:** `{ ok, returnValue, durationMs, gate }`. `Godot.Object` instances in the return value are summarized (type + path/id), not walked.

**Errors:** `validation_error` (missing `type_name`/`method_name`), `type_not_found`, `method_not_found`, `ambiguous_match` (multiple overloads, no `arg_type_names`), `invalid_argument` (count/type coercion), `missing_target`, `unsupported_target` (`object_id` without a handle registry), `instantiation_error`, `invoke_failed` (target threw — message includes exception type + message, no large stacks).

## Tilemap tools

Tilemap tools author 2D tile levels on Godot 4.3+ `TileMapLayer` nodes. The deprecated `TileMap` multi-layer node is not supported — use one `TileMapLayer` per layer. Every tool takes a `node_path` (scene-tree path relative to the edited scene root, same vocabulary as `node_find` / `node_create`) that must resolve to a `TileMapLayer`; a different node type returns `wrong_node_type`.

This is a **`tilemap` group** family — hidden from `ListTools` until an agent activates it via `godot_open_mcp_manage_tools({ action: "activate", group: "tilemap" })`. As with every group, hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

**Cell addressing.** Godot tiles are addressed by an atlas quadruple inside the layer's `TileSet`: `source_id` (which atlas source), `atlas_x` / `atlas_y` (the tile inside the atlas), and `alternative_tile` (an alternative variant). This differs from Unity's `TileBase` asset-path model — there is no `tile_asset_path`. The quadruple defaults to `(0, 0, 0, 0)` so a single-source single-tile `TileSet` can omit every optional field.

### `godot_open_mcp_tilemap_create`

- Route: `live`
- Visibility group: `tilemap`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create a Godot 4.3+ `TileMapLayer` node in the currently edited scene and return its NodeData (same shape as `node_create`) so an agent can chain `node_path` straight into `tilemap_set_tileset` / `tilemap_set_cell`. The new node's owner is set to the edited scene root so it persists in the `.tscn` on save.

**Input:**

- `name` (optional) — Node name. When omitted, Godot assigns the default (`TileMapLayer`).
- `parent_node_path` (optional, default edited scene root) — scene-tree path of the parent.
- `position` (optional) — `'x,y'`. Applied because `TileMapLayer` derives from `Node2D`.
- `paths_hint` (required) — mutation scope (the edited scene `res://` path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** a NodeData object (`{ instanceId, name, path, type, scriptResourcePath, childCount, children }`) plus the standard `gate` block. Tagged with the standard gate block when `gate` is not `off`.

**Errors:** `paths_hint_required`, `edited_scene_unavailable`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_tilemap_set_tileset`

- Route: `live`
- Visibility group: `tilemap`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Assign an existing Godot `TileSet` resource to a `TileMapLayer`. No TileSet authoring is bundled — point this at an existing `.tres`/`.res` `TileSet` (use `resource_create` to build one first if needed). After assignment the layer can be painted with `tilemap_set_cell`.

**Input:**

- `node_path` (required) — scene-tree path of the target `TileMapLayer`.
- `tileset_path` (required) — `res://` (or `uid://`) path to an existing `TileSet` resource. Must exist and be a `TileSet`.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, tilesetPath }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `wrong_node_type`, `resource_not_found`, `resource_load_failed`.

### `godot_open_mcp_tilemap_set_cell`

- Route: `live`
- Visibility group: `tilemap`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Paint one cell on a `TileMapLayer` using Godot's atlas addressing quadruple. The layer must already have a `TileSet` assigned (call `tilemap_set_tileset` first) — Godot's `SetCell` silently no-ops without one, so this tool surfaces that as a structured error. A `source_id` not present in the `TileSet` is also rejected.

**Input:**

- `node_path` (required) — scene-tree path of the target `TileMapLayer`.
- `x` (required) — map cell x coordinate.
- `y` (required) — map cell y coordinate.
- `source_id` (optional, default `0`) — `TileSet` source id. Must be valid in the layer's `TileSet`.
- `atlas_x` (optional, default `0`) — atlas x coordinate inside the source.
- `atlas_y` (optional, default `0`) — atlas y coordinate inside the source.
- `alternative_tile` (optional, default `0`) — alternative tile id inside the source.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, x, y, sourceId, atlasX, atlasY, alternativeTile }` echoing the cell written, plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `wrong_node_type`, `tileset_required` (no `TileSet` assigned), `invalid_parameter` (`source_id` not present in the `TileSet`).

### `godot_open_mcp_tilemap_erase_cell`

- Route: `live`
- Visibility group: `tilemap`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Erase one cell from a `TileMapLayer`. Erasing an already-empty cell is a no-op success. Only `node_path` + `x` + `y` are needed — erase does not care which tile occupied the cell.

**Input:**

- `node_path` (required) — scene-tree path of the target `TileMapLayer`.
- `x` (required) — map cell x coordinate.
- `y` (required) — map cell y coordinate.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, x, y, erased: true }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `wrong_node_type`.

### `godot_open_mcp_tilemap_get_used_cells`

- Route: `live`
- Visibility group: `tilemap`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

List the used cells on a `TileMapLayer`, bounded by `max_results`. Each cell carries the full addressing quadruple (`x`, `y`, `sourceId`, `atlasX`, `atlasY`, `alternativeTile`) so an agent can echo it back into `tilemap_set_cell`. Read-only — no `paths_hint`, no gate.

**Input:**

- `node_path` (required) — scene-tree path of the target `TileMapLayer`.
- `max_results` (optional, default `256`, minimum `1`, hard cap `2000`) — max cells returned. A non-positive value falls back to the default. The remainder is reported in `truncated`.

**Result:**

```json
{
  "cells": [
    { "x": 0, "y": 0, "sourceId": 0, "atlasX": 0, "atlasY": 0, "alternativeTile": 0 },
    { "x": 1, "y": 0, "sourceId": 0, "atlasX": 1, "atlasY": 0, "alternativeTile": 0 }
  ],
  "count": 2,
  "truncated": 0
}
```

`count` is the number of cells returned (the page); `truncated` is the remainder beyond `max_results` so an agent knows whether to page.

**Errors:** `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `wrong_node_type`.

### `godot_open_mcp_tilemap_clear`

- Route: `live`
- Visibility group: `tilemap`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Clear every cell on a `TileMapLayer` while keeping its `TileSet` assignment. Idempotent on an empty layer. Use `tilemap_erase_cell` to remove a single cell.

**Input:**

- `node_path` (required) — scene-tree path of the target `TileMapLayer`.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, tilesetPath, cleared: true }` plus the standard `gate` block. `tilesetPath` echoes the retained `TileSet` resource path (or `null` when the layer had no `TileSet`) so an agent can confirm the contract held.

**Errors:** `paths_hint_required`, `missing_parameter`, `edited_scene_unavailable`, `node_not_found`, `wrong_node_type`.

## Offline fidelity limitations

The offline / live-first tools read from disk and never require the Godot editor. The disk parse is rebuilt per request (no persistent cache), so it is always consistent with the files on disk but cannot reflect state that lives only in a running editor:

- **No unsaved editor state.** An offline `scene_get_data` reports `isDirty: false`; pending edits not yet saved to the `.tscn` are invisible.
- **No instance IDs.** Offline reads surface `instanceId: null` — they never fabricate a hashed id, so an agent cannot chain into `node_modify` from an offline read.
- **Degraded importer metadata.** Offline `filesystem_list` guesses the resource type from the file extension and reports `uid: null` (the UID table lives in `.godot/` import state, which the offline reader refuses to read).
- **Partial instanced/inherited-scene expansion.** An instanced node with no explicit `type` uses a `PackedSceneInstance` fallback; script paths come from serialized `ExtResource` references rather than runtime `Script` objects.
- **Read-only.** The offline readers never evaluate code, load resources, or write project files.

A live semantic error is authoritative and does NOT trigger a fallback — only an unreachable bridge does.

## Source of truth

- Tool definitions: `mcp-server/src/tools/` (`ALL_TOOLS` in `mcp-server/src/tools/index.ts`)
- Route policy: `mcp-server/src/capabilities/route-policy.ts`
- Visibility groups: `mcp-server/src/capabilities/tool-groups.ts`
- Capabilities builder + rule/fix catalog: `mcp-server/src/capabilities/`
- CallTool routing (local vs live vs offline): `mcp-server/src/tool-router.ts`
- MCP-side client (live POST + envelope unwrap): `mcp-server/src/live-client.ts`
- Bridge tool handlers: `packages/bridge/Editor/Tools/` + `MetaTools/` + `Screenshot/` + `Reflection/`
- Catalog parity checker: `scripts/check-tool-docs.mjs` (runs in CI)
- Inventory builder (deterministic snapshot): `scripts/build-tool-doc-inventory.mjs`
