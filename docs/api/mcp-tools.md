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
| `godot_open_mcp_animation_add_track` | animation | live | animation | editor state | enforce | Add a value / position_3d / rotation_3d / scale_3d track to an `Animation` clip; returns the track index. |
| `godot_open_mcp_animation_create` | animation | live | animation | editor state | enforce | Create an `Animation` clip in a named `AnimationLibrary` (auto-creating the library when missing). |
| `godot_open_mcp_animation_defaults` | animation | live | animation | no | n/a | Recommended starter length + loop mode for an `Animation` clip (pure helper, no scene). |
| `godot_open_mcp_animation_get` | animation | live | animation | no | n/a | Read an `AnimationPlayer`'s libraries / animations / tracks (bounded; keys opt-in). |
| `godot_open_mcp_animation_insert_key` | animation | live | animation | editor state | enforce | Insert a keyframe on a track; returns the key index Godot assigned. |
| `godot_open_mcp_animation_library_add` | animation | live | animation | editor state | enforce | Add an empty `AnimationLibrary` registered by name on an `AnimationPlayer`. |
| `godot_open_mcp_animation_player_create` | animation | live | animation | editor state | enforce | Create an `AnimationPlayer` node in the edited scene (returns NodeData). |
| `godot_open_mcp_apply_fix` | core | live | core | disk | warn/off capable | Apply (or preview) a structured fix for a verify issue; non-dry-run applies roll back on new errors under `enforce`. |
| `godot_open_mcp_baseline_create` | core | offline | core | disk | n/a | Run a full offline scan and save a schema-v1 baseline JSON for CI regression tracking. |
| `godot_open_mcp_bridge_status` | core | local | always visible | no | n/a | Operator-oriented health snapshot composing the instance-lock classifier with one `/ping` probe. |
| `godot_open_mcp_capabilities` | core | local | always visible | no | n/a | Discover the full capability surface (tools + verify rules + fixes + groups + routing) in one call. |
| `godot_open_mcp_checkpoint_create` | core | live | core | no | n/a | Capture a project-health baseline over res:// paths for later `delta`. |
| `godot_open_mcp_console_clear_logs` | editor | live | typed-editor | ephemeral | n/a | Clear the addon-owned log collector (ephemeral; never touches the native Output panel). |
| `godot_open_mcp_console_get_logs` | editor | live | typed-editor | no | n/a | Read captured Godot Open MCP log lines, newest-first, with capture-capability metadata. |
| `godot_open_mcp_csg_box_create` | csg | live | csg | editor state | enforce | Create a `CsgBox3D` primitive node (+ optional `size` + `operation`). |
| `godot_open_mcp_csg_combiner_create` | csg | live | csg | editor state | enforce | Create a `CsgCombiner3D` boolean-group container (groups child CSG shapes for boolean ops). |
| `godot_open_mcp_csg_cylinder_create` | csg | live | csg | editor state | enforce | Create a `CsgCylinder3D` primitive node (+ optional `radius` / `height` / `sides` / `cone` / `smooth_faces` + `operation`). |
| `godot_open_mcp_csg_defaults` | csg | live | csg | no | n/a | Recommended starter scalars for a CSG kind (pure helper, no scene). |
| `godot_open_mcp_csg_get` | csg | live | csg | no | n/a | Read a CSG shape's scalar config + type/kind/operation (read-only). |
| `godot_open_mcp_csg_set_operation` | csg | live | csg | editor state | enforce | Set the boolean operation (union / intersection / subtraction) on any CSG shape. |
| `godot_open_mcp_csg_sphere_create` | csg | live | csg | editor state | enforce | Create a `CsgSphere3D` primitive node (+ optional `radius` / `radial_segments` / `rings` / `smooth_faces` + `operation`). |
| `godot_open_mcp_delta` | core | live | core | no | n/a | Compare current state against a prior checkpoint and return the new/resolved issue delta. |
| `godot_open_mcp_editor_application_get_state` | editor | live | typed-editor | no | n/a | Truthful play-process snapshot (`isPlaying`, `playingScene`, `editorVersion`, `observedAt`). |
| `godot_open_mcp_editor_application_set_state` | editor | live | typed-editor | editor state | enforce | Start (main/current/custom scene) or stop the play process with a bounded observation window. |
| `godot_open_mcp_editor_selection_get` | editor | live | typed-editor | no | n/a | Read the editor's node selection as shallow NodeData + the active node. |
| `godot_open_mcp_editor_selection_set` | editor | live | typed-editor | editor state | enforce | Replace or clear the node selection (all-or-nothing resolution). |
| `godot_open_mcp_filesystem_list` | filesystem | live-first | typed-editor | no | n/a | List immediate children of a `res://` directory; live reads authoritative importer metadata. |
| `godot_open_mcp_filesystem_reimport` | filesystem | live | typed-editor | disk | enforce | Reimport exact files or trigger a full scan; blocks until the import pipeline settles. |
| `godot_open_mcp_dependencies` | asset-intelligence | offline | asset-intelligence | no | n/a | Offline forward + reverse dependencies, broken edges, cycles, optional transitive impact. |
| `godot_open_mcp_find_references` | asset-intelligence | offline | asset-intelligence | no | n/a | Offline reverse dependency lookup — assets that reference a given `res://` path or `uid://`. |
| `godot_open_mcp_generate_skill` | core | local | always visible | disk (when `write:true`) | n/a | Generate a project-specific `SKILL.md` (Godot version, enabled plugins, autoloads, available rules, key types); merges with the canonical playbook. |
| `godot_open_mcp_manage_tools` | core | local | always visible | ephemeral | n/a | Per-session tool-group visibility mutator (activate/deactivate/reset/list_groups). |
| `godot_open_mcp_navigation_agent_configure` | navigation | live | navigation | editor state | enforce | Patch clamped scalar properties on a `NavigationAgent2D`/`3D`. |
| `godot_open_mcp_navigation_agent_create` | navigation | live | navigation | editor state | enforce | Create a `NavigationAgent2D`/`3D` node (pathfinding + avoidance) in the edited scene. |
| `godot_open_mcp_navigation_defaults` | navigation | live | navigation | no | n/a | Recommended starter scalars for a 2D/3D agent (pure helper, no scene). |
| `godot_open_mcp_navigation_get` | navigation | live | navigation | no | n/a | Read a navigation node's scalar config + resolved type/dimension (read-only). |
| `godot_open_mcp_navigation_link_create` | navigation | live | navigation | editor state | enforce | Create a `NavigationLink2D`/`3D` (off-mesh connection) with start/end. |
| `godot_open_mcp_navigation_region_create` | navigation | live | navigation | editor state | enforce | Create a `NavigationRegion2D`/`3D` node (navigable area) in the edited scene. |
| `godot_open_mcp_navigation_region_set_mesh` | navigation | live | navigation | editor state | enforce | Assign a region's navigation resource (`NavigationPolygon` 2D / `NavigationMesh` 3D). |
| `godot_open_mcp_node_create` | node | live | typed-editor | editor state | warn/off capable | Create a Node in the edited scene (typed ClassDB instantiate or PackedScene instance). |
| `godot_open_mcp_node_delete` | node | live | typed-editor | editor state | warn/off capable | Delete one or more Nodes (and their sub-trees) synchronously via `Node.Free`. |
| `godot_open_mcp_node_duplicate` | node | live | typed-editor | editor state | warn/off capable | Duplicate a Node sub-tree via `Node.Duplicate`, optionally cross-parent and renamed. |
| `godot_open_mcp_node_find` | node | live | typed-editor | no | n/a | Find Nodes in the edited scene (targeted lookup or filtered list). |
| `godot_open_mcp_node_modify` | node | live | typed-editor | editor state | warn/off capable | Apply property/transform updates to one or more Nodes (single + batch). |
| `godot_open_mcp_node_set_parent` | node | live | typed-editor | editor state | warn/off capable | Reparent a Node via `Node.Reparent`, cycle-safe, transform-preserving by default. |
| `godot_open_mcp_particles_configure` | particles | live | particles | editor state | enforce | Patch clamped scalar properties on a `GpuParticles2D`/`3D` emitter (allow-list only). |
| `godot_open_mcp_particles_create` | particles | live | particles | editor state | enforce | Create a `GpuParticles2D`/`3D` node (+ optional initial scalars + process material). |
| `godot_open_mcp_particles_defaults` | particles | live | particles | no | n/a | Recommended starter scalars for a 2D/3D emitter (pure helper, no scene). |
| `godot_open_mcp_particles_get` | particles | live | particles | no | n/a | Read an emitter's scalar config + type/dimension + process material path. |
| `godot_open_mcp_particles_set_emitting` | particles | live | particles | editor state | enforce | Start/stop emission on a `GpuParticles2D`/`3D`; optional restart clears particles. |
| `godot_open_mcp_ping` | core | live | core | no | n/a | Bridge health check (`GET /ping` round-trip). |
| `godot_open_mcp_pull_events` | core | local | always visible | no | n/a | Drain incremental bridge events (console logs + editor-state transitions) since the last pull. |
| `godot_open_mcp_read_compile_errors` | core | offline | always visible | no | n/a | Offline diagnostic: read a bounded Godot log tail and extract structured C#/GDScript/load errors. |
| `godot_open_mcp_regression_check` | core | offline | core | no | n/a | Compare the current offline scan against a baseline; returns exitCode 0/1/2/3 for CI. |
| `godot_open_mcp_restart_editor` | core | local | always visible | OS process | n/a | Terminate a wedged Godot editor process after confirming a hang/crash signature; requires `confirm: true`. |
| `godot_open_mcp_reflection_method_call` | reflection | live | typed-editor | disk | enforce | Invoke a C# method via reflection (static or instance) and return a JSON-serializable result. |
| `godot_open_mcp_reflection_method_find` | reflection | live | typed-editor | no | n/a | Discover C# types/methods/properties across loaded Godot/.NET assemblies. |
| `godot_open_mcp_resource_create` | resource | live | typed-editor | disk | enforce | Instantiate a Resource subclass via ClassDB and persist via ResourceSaver (no overwrite). |
| `godot_open_mcp_resource_delete` | resource | live | typed-editor | disk | enforce | Delete a `.tres`/`.res` file and its `.import` sidecar via `DirAccess.RemoveAbsolute`. |
| `godot_open_mcp_resource_find` | resource | live | typed-editor | no | n/a | Find Godot resources by uid/path or indexed type search over `EditorFileSystem`. |
| `godot_open_mcp_resource_get_data` | resource | live | typed-editor | no | n/a | Load a resource and return a bounded, cycle-safe property tree. |
| `godot_open_mcp_resource_modify` | resource | live | typed-editor | disk | enforce | Apply validated property-path patches to a resource and persist via ResourceSaver. |
| `godot_open_mcp_resource_move` | resource | live | typed-editor | disk | enforce | Move a `.tres`/`.res` file + `.import` sidecar via `DirAccess.RenameAbsolute` (no reference rewriting). |
| `godot_open_mcp_resource_pressure` | core | local | always visible | no | n/a | Sample the live Godot process's fd/handle usage + trend (proactive leak warning before a wedge). |
| `godot_open_mcp_scene_create` | scene | live | typed-editor | disk | warn/off capable | Create a new `.tscn` asset at a res:// path and optionally open it as the active scene. |
| `godot_open_mcp_scene_get_data` | scene | live-first | typed-editor | no | n/a | Read the edited scene's hierarchy as a NodeData tree; offline falls back to `.tscn` disk parse. |
| `godot_open_mcp_scene_list_opened` | scene | live | typed-editor | no | n/a | List every scene currently open in the editor as a shallow snapshot. |
| `godot_open_mcp_scene_open` | scene | live | typed-editor | editor state | warn/off capable | Open a `.tscn`/`.scn` asset and make it the active/edited scene. |
| `godot_open_mcp_scene_save` | scene | live | typed-editor | disk | warn/off capable | Save the edited scene (or save-as / save-all). |
| `godot_open_mcp_screenshot_camera` | screenshot | live | typed-editor | no | n/a | Off-screen render from a `Camera2D`/`Camera3D` in the edited scene (image content block). |
| `godot_open_mcp_screenshot_isolated` | screenshot | live | typed-editor | no | n/a | Render a `Node3D` alone in an isolated world from one of six directions (image content block). |
| `godot_open_mcp_screenshot_viewport` | screenshot | live | typed-editor | no | n/a | Capture the active editor 2D/3D viewport (image content block). |
| `godot_open_mcp_settings_get_project` | settings | live | settings | no | n/a | Read one `project.godot` section (rendering / physics / input / layer_names / autoload / application / display) or a per-section summary. |
| `godot_open_mcp_settings_set_project` | settings | live | settings | disk | enforce | Write key/value pairs within one `project.godot` section via Godot's `ProjectSettings` API (no raw text edits). |
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
| **local** | The CallTool handler resolves the response in the MCP process — no `POST /tools/{name}` bridge hop. `bridge_status` and `pull_events` may touch the live transport (one bounded `/ping` probe; one SSE-driven queue drain) but the call is synthesized locally; the bridge has no dedicated handler for them. `restart_editor` acts on the OS process directly (`process.kill` / `taskkill`) — the bridge is the thing that dies on a hang, so it may not depend on it for its primary path (it consults the bridge only opportunistically for the `/ping` reachability signal and the dirty-scene warning). `resource_pressure` samples the live Godot PID's fd/handle count server-side (`lsof` / `/proc` / `Get-Process.HandleCount`) — the bridge is the thing that dies on resource exhaustion, so the probe must not depend on it. `generate_skill` reads `project.godot` + the capability catalog + a project type scan in-process and writes the client skill dirs from `skills/client-paths.json` — no bridge round-trip. |
| **offline** | The CallTool handler NEVER probes the bridge and NEVER POSTs to it — it reads disk/config straight. Used for diagnostics that must work in the exact state a dead bridge describes (the addon is not running its listener). |
| **live-first** | The CallTool handler probes the bridge once; if reachable it forwards to the live handler (reflecting unsaved editor state / authoritative import metadata), otherwise it reads from disk with no editor required. A live semantic error (e.g. `scene_not_edited`, `directory_not_found`) is authoritative and does NOT trigger the fallback — only an unreachable bridge does. |

**Route selection.** The router (`mcp-server/src/tool-router.ts`) selects one policy per call: a tool in an override set (`local` / `offline` / `live-first`) is dispatched to its named handler; every other registered tool falls through to the generic live route (`LiveClient.route` → bridge). There is no per-call route override — `routePolicy` advertises possible behavior, it does not let callers pick a route.

**No batch route.** Godot has no headless editor batch equivalent, so there is no `batch` policy, no `batchCapable` flag, and no headless spawn fallback. Every call is live / offline / local.

**Runtime metadata.** Every parseable JSON result is tagged with two MCP-server-owned fields so an agent can answer "where did this originate?":

- `_source` — `live` | `offline` | `local` (where the payload originated).
- `_route` — `{ route, fallbackReason? }` (which policy executed the call). `fallbackReason` appears only when a live-first tool fell back to disk (`"live_unavailable"`).

### Tool groups and session visibility

The MCP server filters `ListTools` through a per-session `ToolSessionState` so the prompt surface stays small. Every registered tool maps to exactly one group via `groupFor(toolName)`; meta-tools (`capabilities`, `bridge_status`, `pull_events`, `read_compile_errors`, `manage_tools`, `restart_editor`, `resource_pressure`, `generate_skill`) map to `null` and are always visible.

| Group id | Default-on | Covers |
|---|---|---|
| `core` | yes | Essential entry points + the gate/verify safety surface (`ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`). The only group visible in a fresh session. |
| `typed-editor` | no | The whole typed editor surface: nodes, scenes, resources, filesystem, editor state/selection, console, screenshots, reflection. One activate brings up the full typed surface. |
| `asset-intelligence` | no | Offline asset-graph intelligence: reverse reference lookup (`find_references`), forward/reverse dependencies (`dependencies`), and related readers. |
| `tilemap` | no | Godot 4.3+ `TileMapLayer` tools: create a layer, assign a `TileSet`, set/erase/clear cells, list used cells. |
| `navigation` | no | Godot 4.3+ navigation tools (2D + 3D): starter defaults, create `NavigationRegion`/`Agent`/`Link`, assign a region's navigation resource, configure agent scalars, inspect any navigation node. |
| `particles` | no | Godot 4.3+ `GpuParticles2D`/`3D` tools (2D + 3D): starter defaults, create an emitter (+ optional initial scalars + process material), configure allow-listed + clamped scalars, start/stop emission (with optional restart), inspect any emitter. |
| `animation` | no | Godot 4.3+ `AnimationPlayer` tools: starter defaults, create a player node, add an empty `AnimationLibrary`, create an `Animation` clip (auto-creating the library when missing), add a value / position_3d / rotation_3d / scale_3d track, insert a keyframe, inspect any player's libraries / animations / tracks. |
| `csg` | no | Godot 4.3+ CSG primitive tools (3D only): starter defaults, create `CsgBox3D` / `CsgSphere3D` / `CsgCylinder3D` / `CsgCombiner3D` nodes (with optional kind-specific scalars + boolean operation), set the boolean operation (union / intersection / subtraction) on any CSG shape, inspect any CSG shape's scalar config. |

The catalog source of truth is `mcp-server/src/capabilities/tool-groups.ts`. Activate or deactivate groups with `godot_open_mcp_manage_tools`; on a successful change the server emits the MCP `notifications/tools/list_changed` notification so clients that support `listChanged` refresh `ListTools` automatically. Hiding a tool is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

### Success and error handling

Every CallTool returns a `CallToolResult`. JSON payloads are tagged with `_source` + `_route` (see [Route policy](#route-policy)).

- **Success.** `isError: false` and a content array of one text block (the JSON body) — except screenshots, which return an image content block plus a text metadata block (see [Screenshots](#screenshot-tools)).
- **Structured error.** `isError: true` with an `error: { code, message }` body. Error codes are stable lowercase tokens (`missing_parameter`, `invalid_path`, `resource_not_found`, `node_not_found`, `scene_dirty`, `paths_hint_required`, …). The catalog lists every code per tool.
- **Bridge transport failure.** When the bridge is unreachable on a live route, the result is `isError: true` with a transport-shaped error (timeout / connection refused / 5xx). The local/live hybrid tools (`bridge_status`, `pull_events`) and the offline tools (`read_compile_errors`, `find_references`) never throw on an offline bridge — `stopped` / `unreachable` / `dead_bridge` are observed states, not execution failures.

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
- `find_references` — `profile` (`compact` default = counts only; `balanced`/`full` = per-asset list) + optional `page_size`/`cursor` over the referencing-assets list. Pagination block uses `next_cursor`.
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
- `find_references` is always-offline and never depends on the bridge — use it to discover reverse dependencies before a move/delete.
- `dependencies` is always-offline — use it for forward deps, reverse deps, broken edges, cycles, and optional transitive impact before destructive ops.
- `restart_editor` is the acting recovery tool when the editor is truly wedged (crash marker in the log, or frozen: live PID + unreachable `/ping` + stale log). It terminates the hung Godot process after explicit `confirm: true`; relaunch is manual (via the Hub/CLI). It refuses when the hang signature is absent — never restart on a fixable compile failure.
- `resource_pressure` is the proactive prediction tool: it samples the live Godot PID's fd/handle count server-side and reports headroom + trend, catching a slow leak across recompiles/reloads BEFORE the editor wedges. The probe does not require the bridge (the bridge is the thing that dies on exhaustion).
- Every other live tool surfaces a structured transport error.

### No batch route

Godot has no headless editor batch equivalent. There is no `batch` policy, no `batchCapable` flag, no headless spawn, and no `batch_execute` tool. Every call is live / offline / local.

## Tool families

| Family | Tools | Mutating | Notes |
|---|---|---|---|
| core | `ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`, `capabilities`, `bridge_status`, `pull_events`, `read_compile_errors`, `manage_tools`, `baseline_create`, `regression_check`, `restart_editor`, `resource_pressure`, `generate_skill` | `apply_fix` + `restart_editor` (OS process kill) + `generate_skill` (writes client skill dirs when `write:true`) | Always visible in `ListTools`. `manage_tools` is the per-session visibility mutator. `baseline_create`/`regression_check` are the CI regression gate; `restart_editor` is the wedged-editor recovery (kill-only — relaunch is manual); `resource_pressure` is the proactive fd/handle leak warning (counterpart to `restart_editor`); `generate_skill` emits a project-specific `SKILL.md` merged with the canonical playbook. |
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
  "counts": { "toolsImplemented": 40, "toolsPlanned": 0, "rulesImplemented": 8, "rulesPlanned": 0, "fixesImplemented": 1, "fixesPlanned": 0 },
  "routing": { "liveDefault": true, "policies": ["live", "local", "offline", "live-first"] }
}
```

Each capability carries an `implemented` boolean; planned-but-unbuilt items return with `status: "planned"` and actionable `guidance` instead of failing. Counts are derived from the registry at call time (not hand-maintained literals). The `routing` block advertises the route vocabulary; per-tool `routePolicy` lives on each tool entry. The `toolGroups` block is compiled-state catalog only — per-session activation lives on `manage_tools` `list_groups`.

#### Rule + fix catalog

The `rules[]` and `fixes[]` arrays mirror the C# verify package and MUST stay in sync on every rule/fix change. The drift-detection tests in `mcp-server/src/capabilities/rule-catalog.test.ts` pin the issue codes, severities, root-cause codes, and fix mappings against the C# constants. Each issue descriptor also carries a stable `rootCause` code from the explainability taxonomy (mirrors the C# `IssueExplainability.Table`): a machine-readable category an agent can branch on (`resource_missing`, `configuration_mismatch`, `structural_complexity`, `missing_script_class`, `orphaned_import`, …). The remediation copy lives on each emitted `VerifyIssue` (resolved per-instance) and is not duplicated in the catalog.

| Rule id | Issue code | Severity | Fix |
|---|---|---|---|
| `broken_references` | `broken_scene_reference` | Error | `relink_broken_reference` |
| `missing_scripts` | `missing_script` | Error | `remove_missing_script` |
| `import_health` | `orphan_import` | Warning | `remove_orphan_import` |
| `import_health` | `duplicate_uid` | Error | `fix_duplicate_uid` |
| `project_health` | `project_empty_folder` | Warning | _(none in v1)_ |
| `project_health` | `project_uid_only_folder` | Warning | _(none in v1)_ |
| `project_health` | `project_deep_nesting` | Warning | _(none in v1)_ |
| `project_health` | `project_large_folder` | Warning | _(none in v1)_ |
| `project_health` | `project_broken_asset` | Error | _(none in v1)_ |
| `project_health` | `project_empty_scene` | Warning | _(none in v1)_ |
| `scene_structure_health` | `scene_deep_nesting` | Warning | _(none in v1)_ |
| `scene_structure_health` | `scene_high_node_count` | Warning | _(none in v1)_ |
| `scene_structure_health` | `scene_wide_sibling_list` | Warning | _(none in v1)_ |
| `scene_structure_health` | `scene_duplicate_node_name` | Warning | _(none in v1)_ |
| `scene_structure_health` | `scene_empty_node_branch` | Warning | _(none in v1)_ |
| `materials_shader_health` | `materials_missing_shader` | Error | _(none in v1 — future `reassign_missing_shader`)_ |
| `materials_shader_health` | `materials_builtin_shader_only` | Warning | _(none in v1)_ |
| `materials_shader_health` | `materials_orphan_shader_include` | Warning | _(none in v1)_ |
| `materials_shader_health` | `materials_duplicate_material` | Warning | _(none in v1)_ |
| `materials_shader_health` | `materials_unused_material` | Warning | _(none in v1)_ |
| `script_audit` | `script_class_mismatch` | Warning | _(none in v1)_ |
| `script_audit` | `script_missing_class_name` | Warning | _(none in v1)_ |
| `script_audit` | `script_cyclic_class_name` | Warning | _(none in v1)_ |
| `animation_analysis` | `missing_clip` | Error | _(none in v1)_ |
| `animation_analysis` | `empty_clip` | Warning | _(none in v1)_ |
| `animation_analysis` | `unreachable_state` | Warning | _(none in v1)_ |
| `animation_analysis` | `parameter_mismatch` | Warning | _(none in v1)_ |
| `animation_analysis` | `duplicate_clip` | Warning | _(none in v1)_ |

| Fix id | Resolves | Safe |
|---|---|---|
| `remove_missing_script` | `missing_scripts` / `missing_script` | true |
| `relink_broken_reference` | `broken_references` / `broken_scene_reference` | false |
| `remove_orphan_import` | `import_health` / `orphan_import` | true |
| `fix_duplicate_uid` | `import_health` / `duplicate_uid` | false |

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

**Input:** `max_events` (integer, default 50, clamped to [1, 1000]).

**Result:**

```json
{
  "subscriberId": "<server-scoped>",
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

- `subscriberId` — the server-scoped id of the single per-process subscription. It is fixed at MCP-server startup and is **not** settable by the caller (a previously-advertised `subscriber` input param was removed because the bridge ignored it); just read it back for logging.
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

### `godot_open_mcp_find_references`

- Route: `offline`
- Visibility group: `asset-intelligence` (activate via `manage_tools`)
- Read-only/mutating: read-only
- Live editor requirement: none — scans disk; never probes the bridge

Reverse dependency lookup for Godot assets. Returns every asset that references a given `res://` path or `uid://` handle by scanning `.tscn`/`.tres` text for `[ext_resource]` declarations and bare `uid://` tokens (optional `.gd`/`preload`/`load` literals behind `include_scripts`). Use before move/delete/rename to see who depends on an asset — `resource_move` does not rewrite references.

**Input:**

- `asset_path` **or** `uid` (exactly one required) — target as a canonical `res://` path or `uid://` handle. uid↔path is resolved through the offline index (resource headers, `*.uid` sidecars, `.import` remaps). When a uid has no current path, the result sets `unresolvedUid: true` and still scans for that uid token.
- `profile` (optional, default `compact`) — `compact` | `balanced` | `full`. Compact = counts + `byKind`/`byFolder` only; balanced = referencing asset paths; full = also field/header locations.
- `page_size` / `cursor` (optional) — page the referencing-assets list (balanced/full). Response carries `pagination.next_cursor` when more remain.
- `detail` (optional, legacy) — `summary`/`normal`/`verbose` alias for profile; ignored when `profile` is set.
- `max_results` (optional, default 100) — single-page cap when `page_size` is omitted.
- `max_per_file` (optional, default 5) — full profile: max locations per referencing file.
- `include_scripts` (optional, default `false`) — also scan `.gd`/`.cs` `preload`/`load`/`ResourceLoader.load` string literals.

**Result:**

```json
{
  "queriedAssetPath": "res://Resources/DemoData.tres",
  "queriedAssetUid": "uid://demotarget00001",
  "unresolvedUid": false,
  "referencedBy": [],
  "totalCount": 2,
  "byKind": { "scene": 2 },
  "byFolder": { "res://Scenes/": 2 },
  "detail": "summary",
  "truncated": 0
}
```

- `compact` (`detail: "summary"`) — `referencedBy` is empty; use `totalCount` + rollups.
- `balanced` / `full` — `referencedBy[]` entries are `{ assetPath, uid?, kind, folder, locations? }`.
- `unresolvedUid` — `true` when a uid-only query has no path in the offline index (never guesses).

**Errors:** `missing_parameter` (neither selector), `invalid_request` (both selectors).

### `godot_open_mcp_dependencies`

- Route: `offline`
- Visibility group: `asset-intelligence` (activate via `manage_tools`)
- Read-only/mutating: read-only
- Live editor requirement: none — scans disk; never probes the bridge

Forward + reverse dependency lookup for Godot assets. Forward edges come from the target file's `[ext_resource]` header declarations; reverse edges reuse the same scanner as `find_references`. Optional `include_impact` walks the reverse graph for a bounded transitive closure ("what breaks if I delete/move this?").

**Input:**

- `asset_path` **or** `uid` (exactly one required).
- `detail` (optional, default `normal`) — `summary` = counts only; `normal` = full forward + reverse edge rosters.
- `max_results` (optional, default 100) — cap reverse-dependencies roster (forward edges are never capped).
- `include_impact` (optional, default `false`) — include transitive reverse closure with per-node hop depth.
- `max_impact_depth` (optional, default 5, max 20) — depth bound for the impact BFS; sets `impact.truncated` when the frontier is non-empty at this bound.

**Result:**

```json
{
  "queriedAssetPath": "res://Resources/DemoData.tres",
  "queriedAssetUid": "uid://demotarget00001",
  "forwardDependencies": [{ "uid": "", "assetPath": "res://Scripts/DemoData.gd", "extResourceId": "1_script", "resolved": true }],
  "forwardCount": 1,
  "brokenForwardUids": [],
  "cycles": [],
  "reverseDependencies": [{ "assetPath": "res://Scenes/UsesData.tscn", "uid": "", "kind": "scene" }],
  "reverseCount": 1,
  "detail": "normal",
  "truncated": 0,
  "_source": "offline"
}
```

- `impact` (when `include_impact: true`) — `{ affected[{ assetPath, depth }], affectedCount, maxDepth, truncated }`.
- `brokenForwardUids` — distinct unresolved `uid://` targets from forward declarations.
- `cycles` — dependency cycle path lists through the queried asset (forward-graph DFS).

**Errors:** `missing_parameter` (neither selector), `invalid_request` (both selectors).

### `godot_open_mcp_baseline_create`

- Route: `offline` (always-offline — never probes the bridge, never spawns Godot)
- Visibility group: `core`
- Read-only/mutating: writes a baseline JSON file to disk (not project source)
- Live editor requirement: none — scans `.tscn`/`.tres` text on disk

Run a full offline scan and save a baseline JSON file (schema v1) for CI regression tracking. The baseline records the severity summary, the per-rule issue keys, and the `ciExcludedRules` the offline scanner cannot detect. Commit the resulting file (convention: `CI/godot-open-mcp-baseline.json`) and compare future scans against it with `regression_check` in CI. This is the project-level "did this PR introduce new errors?" gate — distinct from the per-mutation `checkpoint_create` / `delta` gate, which covers a single edit.

**Input:**

- `baseline_path` (optional, default `CI/godot-open-mcp-baseline.json`) — path for the baseline file, relative to the project root or absolute. Parent directories are created when missing.
- `platform_profile` (optional, default `desktop`) — `mobile` | `console` | `desktop`. Recorded in the baseline metadata (informational; does not change which rules run).

**Result:**

```json
{
  "baselinePath": "/abs/CI/godot-open-mcp-baseline.json",
  "schemaVersion": 1,
  "platformProfile": "desktop",
  "summary": { "error": 2, "warn": 1, "info": 0 },
  "rules": [
    { "ruleId": "broken_references", "error": 1, "warn": 0, "issueKeyCount": 1 },
    { "ruleId": "missing_scripts", "error": 1, "warn": 0, "issueKeyCount": 1 }
  ],
  "ciExcludedRules": ["import_health", "project_health", "scene_structure_health", "materials_shader_health", "script_audit", "animation_analysis"],
  "scannedFileCount": 12,
  "durationMs": 8
}
```

- `ciExcludedRules` — rule ids the offline scanner does NOT cover. The baseline cannot detect these (they live in the C# verify package and run via the live `validate_edit` surface). A clean baseline means "no broken references or missing scripts offline", not "the project is fully healthy".

**Errors:** `baseline_write_failed` (the baseline file could not be written — e.g. permission denied).

### `godot_open_mcp_regression_check`

- Route: `offline` (always-offline — never probes the bridge, never spawns Godot)
- Visibility group: `core`
- Read-only/mutating: read-only (writes nothing; compares scans)
- Live editor requirement: none — scans `.tscn`/`.tres` text on disk

Compare the current offline scan against a baseline file by error-count delta and return a compact regression summary suitable for CI logs. Returns an `exitCode` field mirroring the CI exit-code contract so a wrapper CLI can pass it through directly.

**Exit-code contract** (`exitCode` in the result body):

| Code | Meaning |
|---|---|
| 0 | No regression — the error-count increase is within every threshold. |
| 1 | Regression — global error delta > `regression_threshold`, or any per-category threshold breached. |
| 2 | Baseline missing — `baseline_path` does not exist. |
| 3 | Baseline invalid — unreadable, unparseable, or schema-version mismatch. |

**Input:**

- `baseline_path` (required) — path to a baseline JSON created by `baseline_create` (relative to the project root or absolute).
- `regression_threshold` (optional, default 0) — max allowed increase in total Error count before the check fails (applied globally). A delta equal to the threshold is tolerated (strict `>`).
- `per_category_thresholds` (optional) — `{ ruleId: maxIncrease }`. Each key overrides `regression_threshold` for that rule; rules absent from the map fall back to `regression_threshold`. The overall verdict is the OR of the global gate and every per-rule gate. Example: `{"broken_references": 2}`.
- `platform_profile` (optional, default `desktop`) — `mobile` | `console` | `desktop`. Informational metadata for the current scan.

**Result:**

```json
{
  "baselinePath": "/abs/CI/godot-open-mcp-baseline.json",
  "schemaVersion": 1,
  "platformProfile": "desktop",
  "exitCode": 1,
  "regressed": true,
  "summary": "Godot Open MCP regression: REGRESSION (global error delta +3 > 0)\n  errors: baseline=2 current=5 delta=+3 (threshold=0)\n  warnings: baseline=1 current=1\n  broken_references: FAIL baseline=2 current=5 delta=+3 (threshold=0)",
  "regression": {
    "baselineSummary": { "error": 2, "warn": 1, "info": 0 },
    "currentSummary": { "error": 5, "warn": 1, "info": 0 },
    "errorDelta": 3,
    "errorThreshold": 0,
    "regressed": true,
    "perRule": [
      { "ruleId": "broken_references", "baselineError": 2, "currentError": 5, "errorDelta": 3, "errorThreshold": 0, "regressed": true }
    ]
  },
  "scannedFileCount": 12,
  "durationMs": 9
}
```

- `summary` — multi-line, CI-log-friendly string (greppable: the verdict line carries `REGRESSION` or `OK`; per-rule lines carry `FAIL` or `ok`).
- `regression.perRule` — `null` when no `per_category_thresholds` were supplied (global-only compare).
- `isError` (the MCP-level flag) is `true` when `exitCode` is 1 (regression), `false` otherwise. Exit codes 2/3 are surfaced as `isError: true` with an `error` block.

**Errors:** `missing_parameter` (empty `baseline_path`), `baseline_missing` (exit 2), `baseline_invalid` (exit 3).

### `godot_open_mcp_restart_editor`

- Route: `local` (acts on the OS process — no `POST /tools/restart_editor` on the bridge; the bridge is the thing that dies on a hang, so the tool may not depend on it for its primary path)
- Visibility group: always visible (meta-tool — survives any group teardown so an operator can always recover)
- Read-only/mutating: mutates the OS process (kills the Godot editor); does NOT touch project source
- Live editor requirement: none for the kill path; the bridge is consulted opportunistically for the `/ping` reachability signal and the active-scene-dirty warning

Auto-recover from a wedged Godot editor by terminating the hung Godot process. Use ONLY when the editor is truly hung: `bridge_status` reports `unreachable` (live PID, `/ping` not responding) or `dead_bridge`, AND the Godot log shows a crash marker (backtrace / segfault / abort / fatal) or a frozen signature (live PID + unreachable `/ping` + no recent log writes). The tool refuses when the hang signature is absent — never restart on a fixable compile failure (use `read_compile_errors` + fix the source instead).

Relaunch is NOT automatic — the interactive Godot editor's launch recipe (the flags the Hub/operator used) is not knowable from the server, so the response carries "relaunch via the Hub/CLI" guidance instead. After relaunch, poll `godot_open_mcp_bridge_status` until it returns `running`.

**Input:**

- `confirm` (optional, default `false`) — must be `true` to actually terminate the editor. When false/absent the call is a dry-run: it returns the PID + diagnosis it would act on without any side effect.
- `kill_grace_ms` (optional, default 5000) — SIGTERM→SIGKILL grace window in milliseconds on macOS/Linux. Ignored on Windows (`taskkill /F` is forced).

**Hang signature.** The kill is gated on a confirmed signature (so the tool never restarts on a fixable failure):

| Signal | Source | Verdict |
|---|---|---|
| Compile/parse errors in the log | `read_compile_errors` extractor | **Absent** — fixable failure; recovery is "fix the source", never "kill the editor". Runs first, overrides everything. |
| Crash marker (backtrace / segfault / abort / fatal) | Godot log tail | **Present** (`godot_log_crash`) — definitive crash. |
| Frozen main thread (live PID + `/ping` unreachable + stale log) | instance lock + `/ping` + log mtime | **Present** (`godot_log_frozen`) — editor wedged, not making forward progress. |
| Anything else | — | **Absent** — re-confirm with `bridge_status` + `read_compile_errors` before killing. |

**Dry-run result** (`confirm` false/absent — no side effect):

```json
{
  "action": "restart_editor",
  "confirm": false,
  "dryRun": true,
  "signaturePresent": true,
  "signatureSource": "godot_log_crash",
  "wouldKillPid": 4242,
  "logPath": "/abs/path/to/godot.log",
  "diagnosis": "A crash marker ... is present in the Godot log tail ...",
  "projectPath": "/abs/path/to/project",
  "message": "Dry-run preview. The Godot editor appears wedged. Pass confirm: true ...",
  "_source": "local",
  "_route": { "route": "local" }
}
```

**Confirmed-kill result** (`confirm: true` — process terminated):

```json
{
  "action": "restart_editor",
  "confirm": true,
  "killed": true,
  "pid": 4242,
  "method": "sigterm",
  "elapsedMs": 120,
  "graceMs": 5000,
  "signatureSource": "godot_log_crash",
  "dirtyScenesWarning": [{ "name": "Main", "path": "res://Main.tscn", "isDirty": true }],
  "dirtyScenesNote": "These scenes had unsaved changes when the editor was killed ...",
  "nextSteps": [
    "Godot editor terminated. Relaunch Godot for this project via the Hub/CLI ...",
    "After relaunch, poll godot_open_mcp_bridge_status until it returns status: \"running\" ..."
  ],
  "_source": "local",
  "_route": { "route": "local" }
}
```

- `method` — `sigterm` | `sigkill` (POSIX) | `taskkill` (Windows).
- `dirtyScenesWarning` — present only when the bridge was still reachable AND dirty scenes were found. Surfaced as a warning (does NOT block the kill — the editor is hung, saving is not an option).
- `nextSteps` — relaunch guidance. `isError: false` on a successful kill.

**Kill-failed result** (`isError: true`) — the automated kill did not terminate the editor. `reason` is `not_found` | `timeout` | `signal_error` | `spawn_failed` | `invalid_pid`; the operator must force-quit manually (Activity Monitor / Task Manager / `kill -9 <pid>`).

**Errors:** `restart_signature_absent` (no hang signature — the note explains what was checked), `godot_process_not_found` (no live PID matches the project's instance lock).

### `godot_open_mcp_resource_pressure`

- Route: `local` (samples the OS process server-side — no `POST /tools/resource_pressure` on the bridge; the bridge is the thing that dies on resource exhaustion, so the probe must not depend on it)
- Visibility group: always visible (meta-tool — the proactive diagnostic counterpart to `restart_editor`, kept reachable so an operator can catch a leak before a wedge)
- Read-only/mutating: read-only (records samples in a session-scoped in-memory ring; no disk cache, no project-file writes)
- Live editor requirement: none — resolves the PID from the instance lock (same source as `bridge_status`) or accepts an explicit `pid`; the probe runs against the OS directly

Sample the live Godot process's file-descriptor / handle usage and report headroom + trend — a proactive warning BEFORE the editor wedges from resource exhaustion (the reactive recovery is `restart_editor`; the diagnosis channel is `read_compile_errors`). Use after heavy automation (many recompiles / editor reloads / long play sessions) to catch a slow fd/handle leak across samples.

The ceiling is PROBED per-OS (not a fixed constant): Linux `/proc/<pid>/limits` `Max open files` soft limit; macOS `launchctl limit maxfiles` system-wide soft limit (a GUI-launched Godot inherits it, not the MCP server's shell `ulimit`); Windows has no Unix fd ceiling (`null`). Because the ceiling is OS/runtime-dependent and only a best-effort reference, the actionable signal is the **trend** (rising/leaking) across successive samples, not the absolute count. A monotonic climb across recompiles/reloads is the leak signature.

**Input:**

- `pid` (optional) — explicit Godot PID to probe. When omitted, resolves the live PID from the project's instance lock. Pass an explicit PID only when you have one from a prior call and want to skip the lock read.

**Probes (cross-platform):**

| Platform | fd count | ceiling |
|---|---|---|
| macOS | `lsof -p <pid>` (line count − header) | `launchctl limit maxfiles` soft value (system-wide, flagged `approximate`) |
| Linux | `readdirSync(/proc/<pid>/fd).length` | `/proc/<pid>/limits` `Max open files` soft value (per-process, authoritative) |
| Windows | `Get-Process -Id <pid>.HandleCount` (approximate) | `null` (no Unix fd ceiling; rely on the trend) |

A failed fd probe still records a sample (`count: null`) so the trend detector sees the gap and does not interpolate across it.

**Result (single sample, no leak):**

```json
{
  "pid": 4242,
  "fdCount": 42,
  "fdMethod": "proc",
  "approximate": false,
  "ceiling": 1024,
  "ceilingMethod": "proc_limits",
  "headroom": 982,
  "pressureRatio": 0.041,
  "state": "ok",
  "reliable": true,
  "trend": { "state": "no_history", "delta": null, "sampleCount": 1 },
  "samples": [{ "ts": 1700000000000, "pid": 4242, "count": 42 }],
  "sampleCount": 1,
  "launchContextCaveat": "Headroom is measured against the per-OS fd ceiling ...",
  "_source": "local",
  "_route": { "route": "local" }
}
```

- `fdCount` — `null` when the probe failed. `fdMethod` — `lsof` | `proc` | `handle_count`.
- `approximate` — `true` on Windows (HandleCount is broader than Unix fds); the state then only ever reaches `critical`, never `warn`.
- `ceiling` — the probed per-OS soft limit, or `null` (Windows / probe failure). `ceilingMethod` — `proc_limits` | `launchctl` | `none`. `ceilingReason` is present when the ceiling is `null`.
- `state` — `ok` | `warn` (≥80% of ceiling) | `critical` (≥90%) | `unknown` (count or ceiling could not be read). Windows degrades to `unknown` (no ceiling).
- `trend` — `{ state, delta, sampleCount }`. `state` is `no_history` (fewer than two usable same-PID samples) | `stable` | `rising` (overall growth, not monotonic, or below the leak threshold) | `leaking` (monotonic climb ≥10% of the ceiling across ≥3 samples, or ≥50 fds when the ceiling is unknown).
- `samples[]` — the session-scoped in-memory ring (capacity 20, oldest-first). No disk cache — a server restart clears history.
- `warning` + `agentNextSteps` — present only when there is something to surface (`state` warn/critical OR `trend.state` leaking). The agent should surface the risk to the operator and recommend saving scene work + restarting via the Hub/CLI before the next reload trips the ceiling.

**Windows / unknown-ceiling result** — `ceiling: null`, `ceilingMethod: "none"`, `state: "unknown"`. The trend is the only actionable signal there.

**Errors:** `godot_process_not_found` (no live PID in the instance lock and no explicit `pid`).

### `godot_open_mcp_generate_skill`

- Route: `local` (reads `project.godot` + the capability catalog + a project type scan in-process — no `POST /tools/generate_skill` on the bridge)
- Visibility group: always visible (meta-tool — an operator should regenerate the skill after plugin/script changes regardless of which groups are active)
- Read-only/mutating: read-only when `write:false` (default — preview); writes the client skill dirs when `write:true`
- Live editor requirement: none — works offline; the project state is read from `project.godot`, the catalog from the MCP server's own registries, and the type scan from the project tree

Generate a project-specific agent skill file (`SKILL.md`) that reflects the ACTUAL project state: Godot version, enabled editor plugins (including the bridge/verify addons), autoloads, available tools + verify rules + fixes, and the key Godot types discovered in the project (`class_name` declarations + `@tool` scripts in `.gd` files; C# `Node`/`Resource` subclasses in `.cs` files). The generated content is a **project-inventory section MERGED with the canonical playbook** (`skills/godot-open-mcp/SKILL.md`) — the playbook is emitted verbatim followed by a `---` separator and a `# Project inventory — <name>` section. The canonical playbook stays hand-authored and is never overwritten. Regenerate after plugin or script changes to keep the skill current.

`write:false` (default) returns the content as a string for preview (no files written). `write:true` persists to one or more client skill directories via `skills/client-paths.json`; unknown client keys are skipped, never aborting the whole write.

**Input:**

- `write` (optional, default `false`) — when `true`, write the generated skill to the client skill directories. When `false`, return the skill content as a string.
- `clients` (optional, only used when `write:true`, default `["claude"]`) — which client skill directories to write to. Allowed values come from `skills/client-paths.json` (`cursor`, `claude`, `vscode`, `vs`, `opencode`, `gemini`, `cline`, `kilocode`, `agents`). Each entry writes to the project-relative path declared for that client.
- `include_workflow` (optional, default `true`) — when `true`, compose the canonical playbook with the project inventory. When `false`, or when the template cannot be located (standalone `mcp-server/` install), emit only the standalone project inventory.

**Preview result** (`write:false`):

```json
{
  "action": "generate_skill",
  "write": false,
  "mergedWithTemplate": true,
  "projectName": "Demo Game",
  "godotVersion": "4.3",
  "bridgeInstalled": true,
  "verifyInstalled": false,
  "pluginCount": 2,
  "typeCount": 12,
  "preview": "# Godot Open MCP — agent skill\n...\n## Project environment\n- **Godot version:** 4.3\n...",
  "nextSteps": [
    "Preview only — no files written. Pass write:true to persist ...",
    "Regenerate after plugin or script changes to keep the skill current."
  ],
  "_source": "local",
  "_route": { "route": "local" }
}
```

**Write result** (`write:true`) — same fields plus a `written[]` array and `knownClients`:

- `written[]` — `{ client, relativePath, existed }` per successfully written client dir. `existed` is `true` when the call overwrote a prior file (the canonical playbook install or a prior generate).
- `knownClients` — the full client-key roster from `skills/client-paths.json`, so a caller can expand the `clients` list on the next call.
- `preview` — a bounded preview of the full content (the full content is on disk). Truncated at ~6000 chars with a `… (N more chars; full content written to disk)` tail marker.

**Merge model.** The canonical playbook (`skills/godot-open-mcp/SKILL.md`) is the source of truth for operational guidance and is emitted verbatim. The generator appends a project-inventory section carrying only what the template cannot know (Godot version, plugin install state, autoloads, discovered types, the live capability surface). A missing template degrades gracefully to a standalone inventory with its own short workflow summary.

**Errors:** `generate_skill_failed` (unexpected disk read failure — the orchestrator is designed not to throw; a missing `project.godot` degrades to a `godotVersion: "unknown"` standalone inventory).

### `godot_open_mcp_validate_edit`

- Route: `live`
- Visibility group: `core`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Run a scoped read-only verify pass over res:// paths and return the health verdict + every issue found. This is the explicit form of the gate's validate step (the gate runs it implicitly on every mutating call in `enforce`/`warn` mode; this tool exposes it directly so an agent can inspect current state without mutating).

**Input:**

- `paths` (required, non-empty) — res:// asset paths to validate (e.g. `["res://Scenes/Main.tscn"]`). The verify rules scan these paths; there is no whole-project fallback.
- `categories` (optional) — rule-id filter. When omitted/empty, every registered rule runs (`broken_references`, `missing_scripts`, `import_health`, `project_health`, `scene_structure_health`, `materials_shader_health`, `script_audit`, `animation_analysis`). An unknown id returns a structured `unknown_rule` body listing the available rules.

**Result:** `{ passed, issues[], categoriesRun, rulesApplied, durationMs }`.

- `passed` — strict-error: any Error severity issue flips it to `false`.
- `issues[]` — `{ ruleId, categoryId, severity, code, issueCode, assetPath, description, rootCause?, remediation?, evidence?, fixCandidates?, fixId?, fixSafe? }`. `categoryId` mirrors `ruleId` and `issueCode` mirrors `code` so agents can match the catalog field either way. `rootCause` (a stable explainability code such as `resource_missing` / `configuration_mismatch`) and `remediation` (clean user-visible fix guidance) are present whenever the emitting rule attached them (every P14 rule + the backfilled P3 rules).
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
- `target_uid` (optional) — for `relink_broken_reference`: replacement `uid://` handle.
- `target_path` (optional) — for `relink_broken_reference`: replacement `res://` path.
- `keep_path` (optional) — for `fix_duplicate_uid`: sidecar that retains the colliding uid.

**Result:**

- Omitting `fix_id` — `{ ok, applicableFixes[] }` (each fix's Safe flag is in the capabilities catalog).
- `fix_id` + `dry_run: true` — `{ ok, fixId, description, safe }`.
- `fix_id` + `dry_run: false` — `{ ok, applied, fixId, touchedPaths[], rollback? }`. A non-dry-run apply under `enforce` that fails or introduces new errors is restored to its pre-fix state; the response then carries a top-level `rollback` block (`rolledBack`, `reason`, `restoredPaths`) — no project change remains.

**Errors:** `missing_parameter` (empty `issue_id`), `invalid_issue_id` (malformed key), `fix_not_applicable`, `fix_failed`, `fix_error`. An unknown `fix_id` returns `ok:true` with an `error.code:unknown_fix` body listing available + applicable fix ids.

**Fix providers.**

| Fix id | Safe | Notes |
|---|---|---|
| `remove_missing_script` | true | Removes broken `script = ExtResource("id")` from `.tscn`/`.tres`. |
| `relink_broken_reference` | false | Repoints a broken `[ext_resource]` to `target_uid` or `target_path`. |
| `remove_orphan_import` | true | Deletes an orphan `.import` sidecar. |
| `fix_duplicate_uid` | false | Re-issues `uid://` on the non-`keep_path` side of a collision. |

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

**Errors:** `no_edited_scene` (no edited scene), `invalid_node_path` (malformed path).

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

**Errors:** `no_edited_scene`, `invalid_node_path`, `parent_not_found`, `type_class_not_found`, `type_class_not_instantiable`, `instance_scene_not_found`, `instance_scene_invalid`, `invalid_transform` (malformed vector / wrong arity).

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

**Errors:** `missing_parameter` (no `node_path` and no `node_paths`), `no_edited_scene`, `paths_hint_required` (when `gate` is not `off`).

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

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `parent_not_found`, `cannot_reparent_root`, `cycle_detected` (proposed parent is the node itself or one of its descendants).

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

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `cannot_duplicate_root`, `parent_not_found`.

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

**Errors:** `missing_parameter`, `no_edited_scene`, `cannot_delete_root`.

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

**Errors:** `no_edited_scene` (no scenes open).

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

**Live error codes:** `scene_not_edited` (the asserted `path` does not match the edited scene), `no_edited_scene`, `invalid_path`.

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

**Errors:** `paths_hint_required`, `selection_limit_exceeded` (request exceeds the hard maximum of 256 nodes), `no_edited_scene`, `node_not_found` (a ref cannot resolve — names the offending index), `node_not_in_edited_scene`, `duplicate_node`, `selection_update_failed` (observed post-state differs from requested — observed state surfaced), `selection_unavailable` (`EditorInterface.GetSelection()` returned null).

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

**Errors:** `paths_hint_required`, `no_edited_scene`, `parent_not_found`, `create_failed`.

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

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `resource_not_found`, `resource_load_failed`.

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

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `tileset_required` (no `TileSet` assigned), `invalid_parameter` (`source_id` not present in the `TileSet`).

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

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

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

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

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

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

## Navigation tools

Navigation tools author Godot 4.3+ navigation nodes — `NavigationRegion2D/3D` (navigable areas), `NavigationAgent2D/3D` (pathfinding + avoidance), and `NavigationLink2D/3D` (off-mesh connections such as jump pads or ladders). Every tool that creates or configures a node takes a `dimension` arg (`"2d"` | `"3d"`) to select the parallel class hierarchy; the read/assign tools infer the dimension from the resolved node's class.

This is a **`navigation` group** family — hidden from `ListTools` until an agent activates it via `godot_open_mcp_manage_tools({ action: "activate", group: "navigation" })`. As with every group, hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

**Scope.** Built-in Godot navigation nodes only — no third-party nav addons. No baking surface in v1: the pack is create + assign resource + configure + inspect. Author a `NavigationPolygon` / `NavigationMesh` resource via the editor's bake UI or `resource_create`, then assign it with `navigation_region_set_mesh`. `navigation_get` reports a navigation resource's path only (no vertex arrays).

**Shared contracts.** `node_path` follows the same scene-tree vocabulary as `node_find` / `node_create`. `paths_hint` is the edited scene `res://` path (mandatory on every mutator, even when `gate` is `off`). The five mutators run the gate cycle by default (`enforce`); the two read-only tools (`defaults`, `get`) are gate-free.

### `godot_open_mcp_navigation_defaults`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: read-only (pure helper — no scene required)
- Live editor requirement: requires the bridge

Return recommended starter scalars (radius, height, max_speed, path_desired_distance, target_desired_distance, avoidance_enabled) for a 2D or 3D agent as a JSON object an agent can spread into `navigation_agent_configure`. The 2D defaults are in pixels (radius 10, max_speed 200, distances 20); the 3D defaults are in meters (radius 0.5, height 1.8, max_speed 5, distances 1).

**Input:**

- `dimension` (required) — `"2d"` | `"3d"`.

**Result:** `{ dimension, agent: { radius, height, maxSpeed, pathDesiredDistance, targetDesiredDistance, avoidanceEnabled } }`.

**Errors:** `invalid_parameter` (`dimension` not `"2d"`/`"3d"`).

### `godot_open_mcp_navigation_region_create`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create a `NavigationRegion2D` (`dimension: "2d"`) or `NavigationRegion3D` (`dimension: "3d"`) node in the currently edited scene and return its NodeData (same shape as `node_create`). A navigation region defines a navigable area; assign its navigation resource afterwards with `navigation_region_set_mesh`.

**Input:**

- `dimension` (required) — `"2d"` | `"3d"`.
- `name` (optional) — Node name. When omitted, Godot assigns the default.
- `parent_node_path` (optional, default edited scene root) — scene-tree path of the parent.
- `position` (optional) — `'x,y'` (2D) or `'x,y,z'` (3D), applied because the region derives from `Node2D` / `Node3D`.
- `paths_hint` (required) — mutation scope (the edited scene `res://` path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** a NodeData object (`{ instanceId, name, path, type, scriptResourcePath, childCount, children }`) plus the standard `gate` block.

**Errors:** `paths_hint_required`, `invalid_parameter` (bad `dimension`), `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_navigation_region_set_mesh`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Assign an existing navigation resource to a `NavigationRegion2D` or `NavigationRegion3D`. The resource type must match the region dimension: `NavigationPolygon` for a 2D region, `NavigationMesh` for a 3D region. No baking is bundled — supply a pre-authored resource (use the editor's bake UI or `resource_create` to build one first).

**Input:**

- `node_path` (required) — scene-tree path of the target `NavigationRegion2D/3D`.
- `mesh_path` (required) — `res://` (or `uid://`) path to an existing `NavigationPolygon` (2D) or `NavigationMesh` (3D).
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, dimension, meshPath }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `resource_not_found`, `resource_load_failed`.

### `godot_open_mcp_navigation_agent_create`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create a `NavigationAgent2D` (`dimension: "2d"`) or `NavigationAgent3D` (`dimension: "3d"`) node in the currently edited scene and return its NodeData. A navigation agent provides pathfinding and avoidance for a moving body — parent it under the `CharacterBody2D`/`3D` or `RigidBody` it steers, and set the agent's `target_position` from the parent's script each frame.

**Input:**

- `dimension` (required) — `"2d"` | `"3d"`.
- `name` (optional) — Node name.
- `parent_node_path` (optional) — parent the agent under the body it steers (a `CharacterBody2D`/`3D` or `RigidBody`).
- `position` (optional) — `'x,y'` (2D) or `'x,y,z'` (3D), in the parent's local space. Usually left at the origin.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** a NodeData object plus the standard `gate` block.

**Errors:** `paths_hint_required`, `invalid_parameter` (bad `dimension`), `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_navigation_agent_configure`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Patch clamped scalar properties on a `NavigationAgent2D` or `NavigationAgent3D`. Only the fields you send are applied — omitted scalars are left unchanged. `radius` and the distance fields are clamped to strictly positive; `height` and `max_speed` are clamped to non-negative. A non-numeric value is silently skipped (non-aborting). The same property names apply to both the 2D and 3D agent classes, except `height` (3D-only — see Errors).

**Input:**

- `node_path` (required) — scene-tree path of the target `NavigationAgent2D/3D`.
- `radius` (optional) — agent radius (avoidance cylinder). Clamped to strictly positive. In pixels (2D) / meters (3D).
- `height` (optional, 3D-only) — agent height (the 3D agent cylinder height). Clamped to non-negative. Passing this for a `NavigationAgent2D` returns `unsupported_property` (NavigationAgent2D exposes no Height property).
- `max_speed` (optional) — maximum speed to reach the target. Clamped to non-negative. Set `0` to stop at the target.
- `path_desired_distance` (optional) — distance to consider the next path position reached. Clamped to strictly positive.
- `target_desired_distance` (optional) — distance to consider the target reached (sets `is_target_reached`). Clamped to strictly positive.
- `avoidance_enabled` (optional) — enable RVO avoidance so the agent steers around other avoidance-enabled agents.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, applied: { radius?, height?, max_speed?, path_desired_distance?, target_desired_distance?, avoidance_enabled? } }` (only the keys written, clamped) plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `unsupported_property` (`height` sent for a 2D agent).

### `godot_open_mcp_navigation_link_create`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create a `NavigationLink2D` (`dimension: "2d"`) or `NavigationLink3D` (`dimension: "3d"`) node — an off-mesh connection between two points (a jump pad, a ladder, a teleport). The start/end positions are **local to the link node** (Godot exposes them in local space, relative to the node's own position).

**Input:**

- `dimension` (required) — `"2d"` | `"3d"`.
- `name` (optional) — Node name.
- `parent_node_path` (optional, default edited scene root) — scene-tree path of the parent.
- `position` (optional) — node position as `'x,y'` (2D) or `'x,y,z'` (3D), in the parent's local space. The link's start/end are relative to this.
- `start_position` (required) — start point, local to the link node, as `'x,y'` (2D) or `'x,y,z'` (3D).
- `end_position` (required) — end point, local to the link node, as `'x,y'` (2D) or `'x,y,z'` (3D).
- `bidirectional` (optional, default `false`) — when `true`, the link can be traversed both ways.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** a NodeData object plus the standard `gate` block.

**Errors:** `paths_hint_required`, `invalid_parameter` (bad `dimension` or malformed start/end position), `missing_parameter`, `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_navigation_get`

- Route: `live`
- Visibility group: `navigation`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Read the scalar configuration of any navigation node — a `NavigationRegion2D/3D`, `NavigationAgent2D/3D`, or `NavigationLink2D/3D`. The result includes the resolved `type` and `dimension` so an agent does not need a second probe to know which property set applies. Regions report their navigation resource path (or `null` when unassigned); agents report the full scalar set; links report their start/end positions and bidirectional flag.

**Input:**

- `node_path` (required) — scene-tree path of the target navigation node.

**Result:** `{ nodePath, type, dimension, kind, … }` where `kind` is `"region"` | `"agent"` | `"link"`. For a region, `meshPath` (or `null`). For an agent, `properties: { radius, height?, max_speed, path_desired_distance, target_desired_distance, avoidance_enabled }` — `height` is omitted for 2D agents (NavigationAgent2D has no Height property). For a link, `properties: { start_position, end_position, bidirectional }`.

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

## Particles tools

Particles tools author Godot 4.3+ GPU particle emitters — `GpuParticles2D` and `GpuParticles3D`. Every tool that creates an emitter takes a `dimension` arg (`"2d"` | `"3d"`) to select the parallel class hierarchy; the read/configure tools infer the dimension from the resolved node's class.

This is a **`particles` group** family — hidden from `ListTools` until an agent activates it via `godot_open_mcp_manage_tools({ action: "activate", group: "particles" })`. As with every group, hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

**Scope.** `GpuParticles2D` and `GpuParticles3D` only — not `CPUParticles2D/3D`, not `GPUParticlesAttractor*` / `GPUParticlesCollision*`. The configure surface is a fixed scalar allow-list with centralized clamping (particles tuning has many interdependent properties and easy-to-set invalid ranges); `emitting` is intentionally excluded — use the dedicated `particles_set_emitting` tool. Full `ParticleProcessMaterial` graph authoring is out of scope for v1: `create` / `configure` accept an optional `process_material_path` pointing at a pre-existing `ParticleProcessMaterial` (an emitter renders nothing without one). `particles_get` reports the scalar config + the process-material path only (no particle-instance arrays).

**Scalar allow-list + clamps.** `configure` (and the initial `properties` object on `create`) accept exactly these scalars:

| Property | Type | Clamp |
|---|---|---|
| `amount` | int | `[1, 100000]` |
| `lifetime` | float | strictly positive (`> 0`) |
| `preprocess` | float | non-negative (`≥ 0`) |
| `speed_scale` | float | non-negative (`≥ 0`) |
| `explosiveness` | float | `[0, 1]` |
| `randomness` | float | `[0, 1]` |
| `fixed_fps` | int | non-negative (`≥ 0`; 0 = render frame rate) |
| `one_shot` / `interpolate` / `fract_delta` / `local_coords` | bool | pass-through |

**Shared contracts.** `node_path` follows the same scene-tree vocabulary as `node_find` / `node_create`. `paths_hint` is the edited scene `res://` path (mandatory on every mutator, even when `gate` is `off`). The three mutators run the gate cycle by default (`enforce`); the two read-only tools (`defaults`, `get`) are gate-free.

### `godot_open_mcp_particles_defaults`

- Route: `live`
- Visibility group: `particles`
- Read-only/mutating: read-only (pure helper — no scene required)
- Live editor requirement: requires the bridge

Return recommended starter scalars (amount, lifetime, one_shot, preprocess, speed_scale, explosiveness, randomness, fixed_fps, interpolate, fract_delta, local_coords) for a 2D or 3D emitter as a JSON object an agent can spread into `particles_create`'s initial `properties` object or use as guidance for `particles_configure`. The 3D default leans higher on amount (30 vs 16 in 2D) because billboarded 3D particles need more samples to read as a continuous plume; both are mid-range values inside the clamp ranges so a spread-into-configure round-trips losslessly (the result keys already match the snake_case configure schema).

**Input:**

- `dimension` (required) — `"2d"` | `"3d"`.

**Result:** `{ dimension, properties: { amount, lifetime, one_shot, preprocess, speed_scale, explosiveness, randomness, fixed_fps, interpolate, fract_delta, local_coords } }`.

**Errors:** `invalid_parameter` (`dimension` not `"2d"`/`"3d"`).

### `godot_open_mcp_particles_create`

- Route: `live`
- Visibility group: `particles`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create a `GpuParticles2D` (`dimension: "2d"`) or `GpuParticles3D` (`dimension: "3d"`) node in the currently edited scene and return its NodeData (same shape as `node_create`). A GPU emitter needs a process material (typically a `ParticleProcessMaterial`) to render anything — pass an optional `process_material_path` to assign one at create time. An optional initial `properties` object applies the same allow-listed + clamped scalars as `particles_configure` post-creation.

**Input:**

- `dimension` (required) — `"2d"` | `"3d"`.
- `name` (optional) — Node name. When omitted, Godot assigns the default (`GPUParticles3D`, etc.).
- `parent_node_path` (optional, default edited scene root) — scene-tree path of the parent.
- `position` (optional) — `'x,y'` (2D) or `'x,y,z'` (3D), applied because the emitter derives from `Node2D` / `Node3D`.
- `process_material_path` (optional) — `res://` (or `uid://`) path to an existing `Material` (`ParticleProcessMaterial` is the typical choice; `ShaderMaterial` is also accepted). A type mismatch returns `resource_load_failed`. Omit to assign one later.
- `properties` (optional) — initial scalar properties (same allow-list + clamps as `particles_configure`). Only the fields you send are applied.
- `paths_hint` (required) — mutation scope (the edited scene `res://` path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** a NodeData object (`{ instanceId, name, path, type, scriptResourcePath, childCount, children }`) plus the standard `gate` block.

**Errors:** `paths_hint_required`, `invalid_parameter` (bad `dimension`), `no_edited_scene`, `parent_not_found`, `create_failed`, `resource_not_found`, `resource_load_failed` (bad process material).

### `godot_open_mcp_particles_configure`

- Route: `live`
- Visibility group: `particles`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Patch clamped scalar properties on a `GpuParticles2D` or `GpuParticles3D`. Only the fields you send are applied — omitted scalars are left unchanged. `amount` is clamped to `[1, 100000]`; `lifetime` to strictly positive; `preprocess` and `speed_scale` to non-negative; `explosiveness` and `randomness` to `[0, 1]`; `fixed_fps` to non-negative; the booleans pass through. A non-numeric value is silently skipped (non-aborting). The same property names apply to both the 2D and 3D emitter classes. `emitting` is intentionally not configurable here — use `particles_set_emitting`.

**Input:**

- `node_path` (required) — scene-tree path of the target `GpuParticles2D/3D`.
- `amount` (optional) — number of particles. Clamped to `[1, 100000]`.
- `lifetime` (optional) — particle lifetime in seconds. Clamped to strictly positive.
- `one_shot` (optional) — emit once and stop (one-shot burst).
- `preprocess` (optional) — preprocess duration in seconds (simulate before the first frame so the emitter appears populated). Clamped to non-negative.
- `speed_scale` (optional) — simulation speed scale. Clamped to non-negative. `1.0` = real-time.
- `explosiveness` (optional) — emission explosiveness ratio. Clamped to `[0, 1]`.
- `randomness` (optional) — emission randomness ratio. Clamped to `[0, 1]`.
- `fixed_fps` (optional) — fixed simulation framerate. Clamped to non-negative. `0` = render frame rate.
- `interpolate` (optional) — interpolate particle positions between fixed-fps steps.
- `fract_delta` (optional) — use fractional delta time for smoother timing.
- `local_coords` (optional) — use the emitter's local coordinate space (vs global).
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, applied: { …only the keys written, clamped… } }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

### `godot_open_mcp_particles_set_emitting`

- Route: `live`
- Visibility group: `particles`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Start or stop emission on a `GpuParticles2D` or `GpuParticles3D` by setting its `emitting` flag. Pass `emitting: true` to start, `false` to stop. When `restart: true`, Godot's `Restart()` is called first — it clears existing particles and restarts the emission cycle (useful for one-shot re-fire or resetting a continuous emitter's accumulator). This is the single toggle path — `emitting` is intentionally not in `particles_configure`'s allow-list.

**Input:**

- `node_path` (required) — scene-tree path of the target `GpuParticles2D/3D`.
- `emitting` (required) — `true` to start, `false` to stop. A non-boolean (e.g. `1`) returns `missing_parameter`.
- `restart` (optional, default `false`) — call `Restart()` before flipping `emitting`.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, emitting, restarted }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

### `godot_open_mcp_particles_get`

- Route: `live`
- Visibility group: `particles`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Read the scalar configuration of a `GpuParticles2D` or `GpuParticles3D`. The result includes the resolved `type` and `dimension` so an agent does not need a second probe, plus the full allow-listed scalar set (mirroring `particles_configure`'s allow-list, so a get → configure round-trip is lossless on those properties) and the `process_material_path` (or `null` when unassigned).

**Input:**

- `node_path` (required) — scene-tree path of the target `GpuParticles2D/3D`.

**Result:** `{ nodePath, type, dimension, properties: { amount, lifetime, one_shot, preprocess, speed_scale, explosiveness, randomness, fixed_fps, interpolate, fract_delta, local_coords, emitting }, process_material_path }`.

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

## Animation tools

Animation tools author Godot 4.3+ `AnimationPlayer` clips — the player owns `AnimationLibrary` resources (named slots), each of which owns `Animation` clips, each of which owns tracks, each of which owns keyframes. The seven tools walk that hierarchy top-down: create the player, add a library, create a clip, add a track, insert keys, then inspect.

This is an **`animation` group** family — hidden from `ListTools` until an agent activates it via `godot_open_mcp_manage_tools({ action: "activate", group: "animation" })`. As with every group, hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

**Scope.** `AnimationPlayer` + `AnimationLibrary` + `Animation` only — not `AnimationTree`, not `Tween`, not `.glb` retargeting. Track types in v1: **value**, **position_3d**, **rotation_3d**, **scale_3d** — the four Godot exposes cleanly for create. Blend-shape / method / bezier / audio / animation tracks are deliberately not claimed; an agent requesting one gets `unsupported_track_type`. No Unity AnimatorController / `.controller` tools (skipped — Godot's catalog is player-centric). No `save_path` persistence model — animations live as resources owned by the `AnimationPlayer` in the edited scene and persist on scene save.

**Track path format (the #1 failure mode).** Godot resolves track paths relative to the AnimationPlayer's `root_node` (an `AnimationMixer` property; default is the player's parent). The path is a `NodePath` with an optional sub-path to the animated property, e.g. `"Sprite2D:position"` animates the `position` Vector2 of a sibling node named Sprite2D. A path that does not resolve (wrong node name, or animating a property the node does not have) is **accepted by Godot at authoring time** but produces no effect at playback. This pack surfaces the resolved path in the `add_track` / `get` results so an agent can verify, but does not validate the path against the scene tree (Godot itself does not — the player may animate nodes added later). When in doubt, animate `position` / `rotation` / `scale` on a sibling Node2D / Node3D.

**Key value JSON shapes.** `insert_key` parses the `value` field into a typed structure and converts it to a Godot Variant. Supported shapes: number (`0.5`, `42`) → float; bool (`true`/`false`); string; `{x,y}` → Vector2; `{x,y,z}` → Vector3; `{r,g,b[,a]}` → Color (`a` defaults to 1). A type-tagged object (`{type:"vector3",x,y,z}`) overrides component inference. Rotation3D tracks take a Vector3 in **radians** (Godot convention). An unrecognized shape returns `invalid_parameter`.

**Shared contracts.** `node_path` follows the same scene-tree vocabulary as `node_find` / `node_create`. `paths_hint` is the edited scene `res://` path (mandatory on every mutator, even when `gate` is `off`). The five mutators run the gate cycle by default (`enforce`); the two read-only tools (`defaults`, `get`) are gate-free.

### `godot_open_mcp_animation_defaults`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: read-only (pure helper — no scene required)
- Live editor requirement: requires the bridge

Return the recommended starter Animation length (1.0s) and loop mode (`"none"`) as a JSON object an agent can spread into `animation_create`. Animation is dimension-agnostic (one class), so there is no dimension arg. The loop mode names mirror Godot's `Animation.LoopMode` enum minus the redundant `Loop` prefix (none / linear / pingpong).

**Input:** none.

**Result:** `{ length, loopMode }`.

**Errors:** none.

### `godot_open_mcp_animation_player_create`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create an `AnimationPlayer` node in the currently edited scene and return its NodeData (same shape as `node_create`). An `AnimationPlayer` is the Godot equivalent of Unity's Animator + AnimationClip container — it owns `AnimationLibrary` resources (named slots), each of which owns `Animation` clips. After creating the player, call `animation_library_add` to add an empty library, then `animation_create` to add a clip. `AnimationPlayer` derives from `Node` (not Node2D/Node3D), so it has no spatial position — the optional `position` arg is accepted for forward-compat but ignored.

**Input:**

- `name` (optional) — Node name. When omitted, Godot assigns the default (`AnimationPlayer`).
- `parent_node_path` (optional, default edited scene root) — scene-tree path of the parent.
- `position` (optional) — accepted but ignored (AnimationPlayer is not a spatial node).
- `paths_hint` (required) — mutation scope (the edited scene `res://` path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** a NodeData object plus the standard `gate` block.

**Errors:** `paths_hint_required`, `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_animation_library_add`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Add an empty `AnimationLibrary` registered under a name on a target `AnimationPlayer`. The library is a named slot on the player that will hold `Animation` clips (added afterwards via `animation_create`). The library name defaults to `"default"` when omitted (Godot's convention). A library already registered under that name returns `already_exists` — no silent overwrite.

**Input:**

- `node_path` (required) — scene-tree path of the target `AnimationPlayer`.
- `library` (optional, default `"default"`) — name to register the library under. Godot's internal default library key is the empty string; this pack names libraries `"default"` by convention so an agent does not have to know about the empty-string special case. Use a distinct name (`"player"`, `"ui"`) to hold multiple libraries on one player.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, library, animationCount }` (animationCount is `0` for a fresh library) plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `already_exists`, `create_failed`.

### `godot_open_mcp_animation_create`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Create an `Animation` clip in a named library on a target `AnimationPlayer`, and return the clip's name + length + loop mode. The library defaults to `"default"` and is auto-created when missing — you do not need to call `animation_library_add` first unless you want a non-default library name or an explicit empty library. A clip already registered under that name in that library returns `already_exists`. An explicit-but-unrecognized `loop_mode` returns `invalid_parameter`; an absent `loop_mode` leaves the Godot default (`none`).

**Input:**

- `node_path` (required) — scene-tree path of the target `AnimationPlayer`.
- `animation` (required) — clip name (the key the clip is registered under in the library, e.g. `"Idle"`).
- `library` (optional, default `"default"`) — library to add the clip to. Auto-created when missing.
- `length` (optional, default `1.0`) — clip length in seconds. Clamped to strictly positive.
- `loop_mode` (optional) — `"none"` | `"linear"` | `"pingpong"`. Absent leaves the Godot default (`none`).
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, library, animation, length, loopMode }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `invalid_parameter` (bad `loop_mode`), `no_edited_scene`, `node_not_found`, `wrong_node_type`, `already_exists`, `create_failed`.

### `godot_open_mcp_animation_add_track`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Add a track to an `Animation` clip in a named library on a target `AnimationPlayer`, and return the new track's index (pass it to `animation_insert_key`). v1 supports four track types: `value` (animate any property), `position_3d` / `rotation_3d` / `scale_3d` (animate a Node3D transform component; rotation is in radians). Other Godot track types return `unsupported_track_type`. The `track_path` is a NodePath string Godot resolves relative to the AnimationPlayer's `root_node` — e.g. `"Sprite2D:position"` animates the `position` property of a node named Sprite2D. A wrong path is accepted at authoring time but produces no playback effect; the result echoes the path so an agent can verify. For value tracks an optional `update_mode` sets how Godot applies the value (`continuous` / `discrete` / `capture`; `continuous` is the default).

**Input:**

- `node_path` (required) — scene-tree path of the target `AnimationPlayer`.
- `library` (optional, default `"default"`) — library holding the clip.
- `animation` (required) — clip name.
- `track_type` (required) — `"value"` | `"position_3d"` | `"rotation_3d"` | `"scale_3d"`.
- `track_path` (required) — NodePath string relative to the player's `root_node` (e.g. `"Sprite2D:position"`, `"Player:rotation"`).
- `value_type` (optional) — informational hint for value tracks (e.g. `"float"`, `"bool"`). Reserved for a future validation pass.
- `update_mode` (optional, value tracks only, default `continuous`) — `"continuous"` | `"discrete"` | `"capture"`. Ignored for transform tracks.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, library, animation, trackIndex, trackType, trackPath, updateMode?, keyCount }` (`updateMode` only for value tracks; `keyCount` is `0` for a fresh track) plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `unsupported_track_type`, `library_not_found`, `animation_not_found`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `create_failed`.

### `godot_open_mcp_animation_insert_key`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: mutating (editor state; marks the scene unsaved)
- Live editor requirement: requires the bridge

Insert a keyframe at the given time on a track in an `Animation` clip, and return the key index Godot assigned (plus the track's new key count). The `value` is a typed JSON token the handler converts to a Godot Variant — supported shapes: number → float; bool; string; `{x,y}` → Vector2; `{x,y,z}` → Vector3; `{r,g,b[,a]}` → Color (`a` defaults to 1). A type-tagged object (`{type:"vector3",...}`) overrides component inference. Godot checks the Variant type against the track type at insertion — a value track accepts the property's type, a transform track requires a Vector3 (rotation in radians). A mismatch returns `invalid_parameter`. An optional `interpolation` overrides the per-key easing (`nearest` / `linear` / `cubic`; absent leaves the Godot default). An optional `transition` sets the easing curve weight (default `1.0`).

**Input:**

- `node_path` (required) — scene-tree path of the target `AnimationPlayer`.
- `library` (optional, default `"default"`) — library holding the clip.
- `animation` (required) — clip name.
- `track_index` (required) — track index (the value returned by `animation_add_track`).
- `time` (required) — keyframe time in seconds.
- `value` (required) — number / boolean / string / `{x,y}` / `{x,y,z}` / `{r,g,b[,a]}` / `{type,...}`.
- `interpolation` (optional) — `"nearest"` | `"linear"` | `"cubic"`.
- `transition` (optional, default `1.0`) — easing curve weight.
- `paths_hint` (required) — mutation scope (the edited scene path). Mandatory even when `gate` is `off`.
- `gate` (optional, default `enforce`) — `enforce` | `warn` | `off`.

**Result:** `{ nodePath, library, animation, trackIndex, keyIndex, time, keyCount }` plus the standard `gate` block.

**Errors:** `paths_hint_required`, `missing_parameter`, `invalid_parameter` (bad value or interpolation), `library_not_found`, `animation_not_found`, `track_not_found`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

### `godot_open_mcp_animation_get`

- Route: `live`
- Visibility group: `animation`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Read the libraries / animations / tracks of an `AnimationPlayer` in a bounded JSON envelope. Returns the full hierarchy: player → libraries[] → animations[] → tracks[] (each track carries `index` / `type` / `path` / `keyCount`). Does **not** dump every key by default — pass `include_keys: true` plus an optional `animation` filter and `max_keys` to dump keys for one clip (capped at `max_keys` per track, default `32`, hard max `256`; a `truncated` count is reported when the cap bites). Optional `library` / `animation` filters restrict the walk to one library or one clip (an animation filter without a library filter looks in `"default"`). A filter that does not resolve returns `library_not_found` / `animation_not_found`.

**Input:**

- `node_path` (required) — scene-tree path of the target `AnimationPlayer`.
- `library` (optional) — library name filter. When absent, all libraries on the player are listed.
- `animation` (optional) — clip name filter. When set without a `library` filter, looks in `"default"`.
- `include_keys` (optional, default `false`) — if `true`, each track also carries a bounded `keys` array (`time` / `transition` / `value`).
- `max_keys` (optional, default `32`, clamped to `[0, 256]`) — max keys per track when `include_keys` is `true`. `0` returns no keys (just the `keyCount`).

**Result:** `{ nodePath, libraries: [{ name, animations: [{ name, length, loopMode, tracks: [{ index, type, path, keyCount, keys? }] }] }] }`.

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`, `library_not_found`, `animation_not_found`.

## CSG tools

CSG tools author Godot 4.3+ constructive-solid geometry primitives — `CsgBox3D`, `CsgSphere3D`, `CsgCylinder3D`, and the `CsgCombiner3D` boolean-group container. The four create tools are split per kind (the catalog's split-tool style — clearer for an agent picking a primitive than a single create-shape with a kind enum); `set_operation` is the second half of the boolean workflow.

This is a **`csg` group** family — hidden from `ListTools` until an agent activates it via `godot_open_mcp_manage_tools({ action: "activate", group: "csg" })`. As with every group, hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

**Scope.** `CsgBox3D`, `CsgSphere3D`, `CsgCylinder3D`, `CsgCombiner3D` only — not `CsgTorus3D`, not `CsgPolygon3D`, not `CsgMesh3D`. The create surface is a fixed scalar allow-list per kind with centralized clamping; full mesh vertex editing is out of scope (a CSG mesh's vertex array is large and not what an agent tunes — `csg_get` returns scalars only). Godot has no 2D CSG primitives, so the pack is 3D only. No face-editing API (CSG has no equivalent of ProBuilder's extrude / delete_faces / set_face_material).

**Boolean workflow.** Godot's CSG combiner is implicit: a `CsgCombiner3D` parent collects its child `CsgShape3D` children, and each child's `Operation` property (union / intersection / subtraction) defines how it combines with the sibling-before-it. The three-step recipe: `csg_combiner_create` to make the parent → create primitives with `parent_node_path` = the combiner → `csg_set_operation` on each child (e.g. subtraction on a cutter carves out the cutter's silhouette). Godot rebuilds the CSG mesh when the scene updates; the pack marks the scene dirty and does not force a manual rebuild.

**Scalar allow-list + clamps (per kind):**

| Kind | Property | Type | Clamp |
|---|---|---|---|
| box | `size` ("x,y,z") | Vector3 per component | strictly positive per component (`≥ 0.0001`) |
| sphere | `radius` | float | strictly positive (`≥ 0.0001`) |
| sphere | `radial_segments` | int | `[3, 1000]` |
| sphere | `rings` | int | `[3, 1000]` |
| sphere | `smooth_faces` | bool | pass-through |
| cylinder | `radius` | float | strictly positive (`≥ 0.0001`) |
| cylinder | `height` | float | strictly positive (`≥ 0.0001`) |
| cylinder | `sides` | int | `[3, 1000]` |
| cylinder | `cone` | bool | pass-through |
| cylinder | `smooth_faces` | bool | pass-through |

`operation` (union / intersection / subtraction) is shared by every kind and applies via Godot's `CsgShape3D.OperationEnum`. The combiner carries no primitive scalars — `operation` is its only knob.

**Shared contracts.** `node_path` follows the same scene-tree vocabulary as `node_find` / `node_create`. `paths_hint` is the edited scene `res://` path (mandatory on every mutator, even when `gate` is `off`). The five mutators (four creates + set_operation) run the gate cycle by default (`enforce`); the two read-only tools (`defaults`, `get`) are gate-free. Every create tool returns the new node's NodeData (same shape as `node_create`).

### `godot_open_mcp_csg_defaults`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Return recommended starter scalars for a CSG primitive kind (box / sphere / cylinder / combiner) plus the shared `operation` (`"union"`). Pure helper — no scene required, no mutation. The values mirror Godot's engine defaults for a fresh `Csg*3D` node: box `size` 1×1×1; sphere `radius` 0.5 / `radial_segments` 12 / `rings` 6 / `smooth_faces` true; cylinder `radius` 0.5 / `height` 2.0 / `sides` 8 / `cone` false / `smooth_faces` true; combiner carries `operation` only (no primitive scalars). Spread the relevant fields into the matching create tool.

**Input:**

- `kind` (required) — `"box"` | `"sphere"` | `"cylinder"` | `"combiner"`.

**Result:** `{ kind, operation, ...kind-specific scalars }`.

**Errors:** `invalid_parameter` (unknown/absent `kind`).

### `godot_open_mcp_csg_box_create`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: mutating (gate `enforce`, `paths_hint` required)
- Live editor requirement: requires the bridge

Create a `CsgBox3D` node in the edited scene and return its NodeData. An optional `size` (`"x,y,z"`) sets the box extents — each component is clamped to strictly positive (the engine default is 1,1,1). An optional `operation` applies the boolean operation at create time — most useful when this box is a child of a `CsgCombiner3D`. The new node's owner is the edited scene root; the scene is marked unsaved.

**Input:**

- `name` (optional) — name for the new box.
- `parent_node_path` (optional) — parent Node path relative to the edited scene root (default: root). Pass a `CsgCombiner3D`'s path to group this box into a boolean combination.
- `position` (optional) — `"x,y,z"` (Node3D origin; malformed → ignored).
- `size` (optional) — `"x,y,z"`. Each component clamped to `≥ 0.0001`.
- `operation` (optional) — `"union"` | `"intersection"` | `"subtraction"`. Omit to leave the engine default (union).
- `paths_hint` (required) — edited scene `res://` path.
- `gate` (optional, default `enforce`) — `"enforce"` | `"warn"` | `"off"`.

**Result:** NodeData for the new box.

**Errors:** `paths_hint_required`, `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_csg_sphere_create`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: mutating (gate `enforce`, `paths_hint` required)
- Live editor requirement: requires the bridge

Create a `CsgSphere3D` node in the edited scene and return its NodeData. Optional scalars tune the sphere: `radius` (default 0.5), `radial_segments` (default 12 — higher is smoother), `rings` (default 6), `smooth_faces` (default true). An optional `operation` applies the boolean operation at create time. The new node's owner is the edited scene root; the scene is marked unsaved.

**Input:**

- `name` (optional) — name for the new sphere.
- `parent_node_path` (optional) — parent Node path relative to the edited scene root (default: root). Pass a `CsgCombiner3D`'s path to group this sphere into a boolean combination.
- `position` (optional) — `"x,y,z"` (Node3D origin; malformed → ignored).
- `radius` (optional) — clamped to `≥ 0.0001`.
- `radial_segments` (optional) — clamped to `[3, 1000]`.
- `rings` (optional) — clamped to `[3, 1000]`.
- `smooth_faces` (optional) — bool.
- `operation` (optional) — `"union"` | `"intersection"` | `"subtraction"`. Omit to leave the engine default (union).
- `paths_hint` (required) — edited scene `res://` path.
- `gate` (optional, default `enforce`) — `"enforce"` | `"warn"` | `"off"`.

**Result:** NodeData for the new sphere.

**Errors:** `paths_hint_required`, `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_csg_cylinder_create`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: mutating (gate `enforce`, `paths_hint` required)
- Live editor requirement: requires the bridge

Create a `CsgCylinder3D` node in the edited scene and return its NodeData. Optional scalars tune the cylinder: `radius` (default 0.5), `height` (default 2.0), `sides` (default 8 — higher is smoother), `cone` (default false — set true for a cone), `smooth_faces` (default true). An optional `operation` applies the boolean operation at create time. The new node's owner is the edited scene root; the scene is marked unsaved.

**Input:**

- `name` (optional) — name for the new cylinder.
- `parent_node_path` (optional) — parent Node path relative to the edited scene root (default: root). Pass a `CsgCombiner3D`'s path to group this cylinder into a boolean combination.
- `position` (optional) — `"x,y,z"` (Node3D origin; malformed → ignored).
- `radius` (optional) — clamped to `≥ 0.0001`.
- `height` (optional) — clamped to `≥ 0.0001`.
- `sides` (optional) — clamped to `[3, 1000]`.
- `cone` (optional) — bool.
- `smooth_faces` (optional) — bool.
- `operation` (optional) — `"union"` | `"intersection"` | `"subtraction"`. Omit to leave the engine default (union).
- `paths_hint` (required) — edited scene `res://` path.
- `gate` (optional, default `enforce`) — `"enforce"` | `"warn"` | `"off"`.

**Result:** NodeData for the new cylinder.

**Errors:** `paths_hint_required`, `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_csg_combiner_create`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: mutating (gate `enforce`, `paths_hint` required)
- Live editor requirement: requires the bridge

Create a `CsgCombiner3D` node in the edited scene and return its NodeData. The combiner is Godot's boolean-group container: every child `CsgShape3D` (primitive or nested combiner) combines according to its own `Operation` property. Create primitives under the combiner by passing its path as `parent_node_path` to the primitive create tools, then call `csg_set_operation` on each child to define how it combines. The combiner has no primitive scalars of its own; the only knob is the optional `operation` (rarely needed — a combiner's own operation only matters when it is itself a child of another combiner). The new node's owner is the edited scene root; the scene is marked unsaved.

**Input:**

- `name` (optional) — name for the new combiner.
- `parent_node_path` (optional) — parent Node path relative to the edited scene root (default: root). May point at an existing `CsgCombiner3D` to nest this combiner as a boolean child.
- `position` (optional) — `"x,y,z"` (Node3D origin; malformed → ignored).
- `operation` (optional) — `"union"` | `"intersection"` | `"subtraction"`. Rarely needed — a combiner's own operation only matters when it is itself a child of another combiner. Omit to leave the engine default (union).
- `paths_hint` (required) — edited scene `res://` path.
- `gate` (optional, default `enforce`) — `"enforce"` | `"warn"` | `"off"`.

**Result:** NodeData for the new combiner.

**Errors:** `paths_hint_required`, `no_edited_scene`, `parent_not_found`, `create_failed`.

### `godot_open_mcp_csg_set_operation`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: mutating (gate `enforce`, `paths_hint` required)
- Live editor requirement: requires the bridge

Set the boolean `Operation` property on any CSG shape (`CsgBox3D` / `CsgSphere3D` / `CsgCylinder3D` / `CsgCombiner3D` — the property lives on the shared `CsgShape3D` base). The operation only takes effect when the shape is a child of a `CsgCombiner3D` (or another CSG shape): `union` merges the child's geometry with the sibling-before-it, `intersection` keeps only the overlap, `subtraction` removes the child's shape from the sibling-before-it (a cutter carves out its silhouette). Godot rebuilds the CSG mesh when the scene updates; the pack marks the scene dirty and does not force a manual rebuild.

**Input:**

- `node_path` (required) — scene-tree path of the CSG shape to mutate.
- `operation` (required) — `"union"` | `"intersection"` | `"subtraction"`.
- `paths_hint` (required) — edited scene `res://` path.
- `gate` (optional, default `enforce`) — `"enforce"` | `"warn"` | `"off"`.

**Result:** `{ nodePath, operation }`.

**Errors:** `paths_hint_required`, `missing_parameter` (`node_path` or `operation` absent/invalid), `no_edited_scene`, `node_not_found`, `wrong_node_type`.

### `godot_open_mcp_csg_get`

- Route: `live`
- Visibility group: `csg`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Read the scalar configuration of any CSG shape. Returns the resolved `type` (Godot class name) + `kind` (box / sphere / cylinder / combiner) + `operation` (union / intersection / subtraction) plus the kind-specific scalars (box: `size` x/y/z; sphere: `radius` / `radial_segments` / `rings` / `smooth_faces`; cylinder: `radius` / `height` / `sides` / `cone` / `smooth_faces`; combiner: none). No mesh vertex dump — a CSG mesh's vertex array is large and not what an agent tunes. Use this to verify a boolean setup or to read back what a create tool landed.

**Input:**

- `node_path` (required) — scene-tree path of the CSG shape to read.

**Result:** `{ nodePath, type, operation, kind, ...kind-specific scalars }`.

**Errors:** `missing_parameter`, `no_edited_scene`, `node_not_found`, `wrong_node_type`.

## Settings tools

Settings tools read and write `project.godot` sections via Godot's `ProjectSettings` API. They replace ad-hoc `project.godot` hand-edits through `filesystem_*` / `resource_*` tools (which are unsafe — no validation, can corrupt the project — and unverified — no gate). The read path enumerates a section's keys via `ProjectSettings.GetPropertyList()` and serializes each value with Godot's own `Json.Stringify` so every Variant type (Color, Vector2/3, Dictionary, Array) round-trips; the write path routes every key/value through `ProjectSettings.SetSetting` + `Save` (never raw text edits).

This is a **`settings` group** family — hidden from `ListTools` until an agent activates it via `godot_open_mcp_manage_tools({ action: "activate", group: "settings" })`. As with every group, hiding is a prompt-size control, not an authorization boundary — a hidden tool name still routes when called directly.

**Scope.** The seven writable sections are `rendering`, `physics`, `input`, `layer_names`, `autoload`, `application`, `display` — Godot's `project.godot` first path segments. `settings_get_project` additionally accepts `section:"all"` for a per-section key-count summary (a read-only switch, not a writable domain — `settings_set_project` rejects it). Unity-specific sections (PlayerSettings quality tiers, scripting backend enum) are intentionally NOT ported.

**Section allowlist.** Only known sections are writable; an unknown section (typo) surfaces `invalid_parameter`. A relative key (no `/`) is prefixed with the section's leading segment (e.g. key `run/main_scene` under section `application` → `application/run/main_scene`); an absolute key must stay inside the section (a cross-section key is skipped with a warning so the section arg and the key agree).

**Shared contracts.** `paths_hint` is `res://project.godot` (the single mutated file) — the one settings-family mutator whose scope is not a `.tscn`. `settings_set_project` is gated (`enforce` by default) and requires a non-empty `paths_hint` even when `gate` is `off`. `settings_get_project` is read-only and gate-free.

### `godot_open_mcp_settings_get_project`

- Route: `live`
- Visibility group: `settings`
- Read-only/mutating: read-only
- Live editor requirement: requires the bridge

Read one `project.godot` section and return its keys + current values. Values are serialized to JSON by Godot's own `Json.Stringify` so every Variant type an agent might read (Color, Vector2/3, Dictionary, Array, Packed*) round-trips. Pass `section:"all"` for a per-section key-count summary (no per-key values) so you can pick which section to read in full without dumping the whole file.

**Input:**

- `section` (required) — `"rendering"` | `"physics"` | `"input"` | `"layer_names"` | `"autoload"` | `"application"` | `"display"` | `"all"`.

**Result (single section):** `{ section, keyCount, values: { "<full property path>": <serialized value>, ... } }`.
**Result (`all`):** `{ section: "all", sections: [{ section, keyCount }, ...] }`.

**Errors:** `invalid_parameter` (unknown/absent section), `execution_error`.

### `godot_open_mcp_settings_set_project`

- Route: `live`
- Visibility group: `settings`
- Read-only/mutating: mutating (gate `enforce`, `paths_hint` required)
- Live editor requirement: requires the bridge

Write key/value pairs within one `project.godot` section via Godot's `ProjectSettings.SetSetting` + `Save` API (no raw text edits — the API validates the file's formatting/escaping). Pass a section plus a `fields[]` array of `{key, value}` patches; the handler applies each patch, persists once with `ProjectSettings.Save`, and returns the applied keys plus any per-key warnings (a bad key does NOT abort the batch — good entries still land). A null value clears the setting.

**Input:**

- `section` (required) — one writable section: `"rendering"` | `"physics"` | `"input"` | `"layer_names"` | `"autoload"` | `"application"` | `"display"`. (`"all"` is rejected — it is a read-only summary switch.)
- `fields` (required, non-empty) — array of `{ key, value? }` patches.
  - `key` (required) — the `ProjectSettings` property path. A relative key (no `/`) is prefixed with the section's leading segment; an absolute key must stay inside the section. Examples: `run/main_scene` under `application` → `application/run/main_scene`; `3d/physics/default_gravity` under `physics`.
  - `value` (optional) — any JSON type (string / number / boolean / null / object / array). Godot re-parses the token into a Variant and stores it via `SetSetting`, so a Color is an `{r,g,b[,a]}` object, a Vector3 is an `{x,y,z}` object, etc. Omit or pass `null` to clear the setting.
- `paths_hint` (required) — `["res://project.godot"]`.
- `gate` (optional, default `enforce`) — `"enforce"` | `"warn"` | `"off"`.

**Result:** `{ section, action: "set_project", applied: ["<full property path>", ...], warnings?: [...] }`.

**Errors:** `paths_hint_required`, `missing_parameter` (absent/empty `fields`), `invalid_parameter` (unknown section, or `"all"`), `no_applicable_keys` (every patch was skipped), `execution_error` (including `ProjectSettings.Save` failure — in-memory updates land but the file is not persisted).

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
