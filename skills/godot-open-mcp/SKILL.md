# Godot Open MCP — agent skill

Operational playbook for agents driving **Godot 4.3+ (C#/.NET 8 mono)** projects via Godot Open MCP.
All MCP tools use the `godot_open_mcp_*` prefix. Exhaustive schemas live in `docs/api/mcp-tools.md`;
this file owns safe decision-making, not argument tables.

## Preconditions

- A Godot project with `project.godot` and the `godot_open_mcp` editor addon enabled.
- The MCP server was started with `GODOT_PROJECT_PATH` pointing at that project (absolute path).
- For live tools: the Godot editor is open on the project and the bridge addon is running. The
  bridge port is deterministic per project (`20000 + sha256(path) % 10000`); never hardcode it.
- Offline and local tools (`godot_open_mcp_read_compile_errors`,
  `godot_open_mcp_find_references`, `godot_open_mcp_capabilities`,
  `godot_open_mcp_manage_tools`, `godot_open_mcp_bridge_status`, `godot_open_mcp_pull_events`,
  `godot_open_mcp_ping`) work without a live editor.

## Non-negotiable rules

1. **Discover first.** Call `godot_open_mcp_capabilities` on a fresh session before any other call.
2. **Sessions start with `core` only.** Activate `typed-editor` via `godot_open_mcp_manage_tools`
   before any node/scene/resource/filesystem/editor/selection/console/screenshot/reflection tool.
3. **Scope every mutation.** Every mutating call requires a complete, non-empty `paths_hint` array
   of `res://` paths. There is no whole-project fallback. Default to `gate: "enforce"`.
4. **Read the gate.** After every mutation under `enforce`/`warn`, read the `gate` block
   (mode, outcome, the delta sub-block, agentNextSteps) and act on it. Mutation success ≠ project
   is healthy.
5. **No second editor.** Never spawn a second Godot editor for the same project. If the bridge is
   dead, recover via `godot_open_mcp_read_compile_errors`, then ask the operator.
6. **Prefer typed tools to reflection.** Use `godot_open_mcp_reflection_*` only when no typed tool
   covers the case.
7. **No batch route.** Godot has no headless editor batch mode. Do not invent one.

## Fast start

1. `godot_open_mcp_capabilities` — discover tools, verify rules, fixes, route policies, tool groups.
2. `godot_open_mcp_manage_tools` with `action: "list_groups"` — see which groups exist and which are active.
3. `godot_open_mcp_manage_tools` with `action: "activate", group: "typed-editor"` when you need typed tools.
4. `godot_open_mcp_ping` (lightweight live check) or `godot_open_mcp_bridge_status` (triage snapshot).
5. Call the typed tool with full `paths_hint` and `gate: "enforce"`.
6. Read `gate.delta` and `agentNextSteps`; if new errors appear, fix or revert.

## Tool groups

Sessions start with **`core`** enabled (ping + the gate/verify surface:
`godot_open_mcp_validate_edit`, `godot_open_mcp_checkpoint_create`, `godot_open_mcp_delta`,
`godot_open_mcp_apply_fix`). Activate **`typed-editor`** to add the full typed surface (nodes,
scenes, resources, filesystem, editor state/selection, console, screenshots, reflection). Activate
**`asset-intelligence`** for offline reverse lookup (`godot_open_mcp_find_references`) before a
move/delete. Domain pack groups (`tilemap`, `navigation`, `particles`, `animation`, `csg`) each
carry their pack tools. `godot_open_mcp_manage_tools` actions: `list_groups`, `activate`,
`deactivate`, `reset`. State is per-session and ephemeral; it resets to `core` only when the MCP
server restarts.

The meta-tools (`godot_open_mcp_capabilities`, `godot_open_mcp_manage_tools`, `godot_open_mcp_ping`,
`godot_open_mcp_bridge_status`, `godot_open_mcp_pull_events`, `godot_open_mcp_read_compile_errors`,
`godot_open_mcp_restart_editor`, `godot_open_mcp_resource_pressure`) are always visible — they
survive any group teardown. `restart_editor` terminates a wedged editor (requires `confirm: true`);
`resource_pressure` samples fd/handle pressure + trend to warn of a leak before a wedge.

## Bridge triage

Call `godot_open_mcp_bridge_status` and branch on `status`:

| Status | Action |
|---|---|
| `running` | Proceed with live tools. |
| `compiling` | Wait and retry; do not stack mutations. |
| `stopped` | Godot not running OR addon disabled. Ask the operator to open the project / enable the addon, or use offline tools. |
| `unreachable` | Transient (listener mid-reload). Retry once or twice before concluding the bridge is down. |
| `dead_bridge` | Godot process is alive but the addon is not running its listener. Call `godot_open_mcp_read_compile_errors` and read the failure from the Godot log on disk. Fix the C#/GDScript/addon-load error, then re-check status. Do **not** launch a second editor. |

`recoveryHint` is non-null only for `dead_bridge` and points at `godot_open_mcp_read_compile_errors`.
A semantic live error from a tool is **not** a bridge-down signal — keep using live tools.

## Gate workflow

For every mutating call:

1. Pass `paths_hint` — every `res://` path the mutation can touch. Required even when `gate: "off"`.
2. Default `gate: "enforce"`. Modes: `enforce` (roll back on new errors), `warn` (report but keep),
   `off` (no checkpoint/validate cycle).
3. Inspect the returned `gate` block: `mode`, `outcome`, `ran`, `failed`, the delta sub-block
   (newErrors / newWarnings / resolvedErrors / resolvedWarnings), and `agentNextSteps`.
4. If `agentNextSteps` names a fix or a path, follow it.

Multi-step refactor across separate calls — capture a baseline, mutate, then delta:

```text
godot_open_mcp_capabilities
godot_open_mcp_manage_tools(action=activate, group=typed-editor)
godot_open_mcp_checkpoint_create(paths=["res://Scenes/Main.tscn"])
godot_open_mcp_node_create(..., paths_hint=["res://Scenes/Main.tscn"], gate="enforce")
godot_open_mcp_delta(checkpoint_id=<id>)   # read newErrors / resolvedErrors
```

If `godot_open_mcp_delta` returns `unavailable:true` (checkpoint cleared on reload/restart), fall
back to `godot_open_mcp_validate_edit` for a direct current-state scan.

## Fix workflow

`godot_open_mcp_apply_fix` resolves a single issue from its canonical
`{ruleId}|{severity}|{assetPath}|{issueCode}` key (copy it verbatim from a
`godot_open_mcp_validate_edit` / `godot_open_mcp_delta` / gate issue).

- Discover fix ids + their `Safe` flag via `godot_open_mcp_capabilities` (`kind: "fixes"`).
- `dry_run` defaults to **true** — preview the description + `Safe` flag before applying.
- Auto-apply only fixes advertised as **Safe** (today: `remove_missing_script`). For unsafe fixes,
  preview, surface the trade-off to the operator, and apply only with explicit approval.
- A non-dry-run apply runs through the gate with safe auto-fix rollback: if it fails or introduces
  new errors under `enforce`, the project is restored to its pre-fix state and the response carries
  a top-level `rollback` block (`rolledBack`, `reason`, `restoredPaths`).
- Re-run `godot_open_mcp_validate_edit` or `godot_open_mcp_delta` after a fix to confirm.

## Routing and offline limits

| Route | Meaning |
|---|---|
| `live` | Requires the bridge + live editor state. The default for most tools. |
| `local` | Resolved in the MCP process (`godot_open_mcp_capabilities`, `godot_open_mcp_manage_tools`, `godot_open_mcp_bridge_status`, `godot_open_mcp_pull_events`). `godot_open_mcp_bridge_status` and `godot_open_mcp_pull_events` may probe the live transport, but the call is synthesized locally. |
| `offline` | Reads project/log files without probing the bridge. Only `godot_open_mcp_read_compile_errors`. |
| `live-first` | Prefers live editor state; falls back to disk only when the bridge is classified unavailable (`godot_open_mcp_scene_get_data`, `godot_open_mcp_filesystem_list`). A semantic live error does **not** trigger the fallback. |

There is **no `batch` route** and **no headless editor fallback** in Godot. Offline reads cannot
see unsaved editor state or live node instance ids; acknowledge that gap when you choose the
offline branch.

## Key workflows

**Safe mutate.** capabilities → activate `typed-editor` → checkpoint → mutate with `paths_hint` +
`gate: "enforce"` → read `gate.delta` → fix or revert.

**Dead bridge after a C# / addon edit.** `godot_open_mcp_bridge_status` returns `dead_bridge` →
`godot_open_mcp_read_compile_errors` to read structured diagnostics from the Godot log → fix the
load error → re-open or reload the editor → re-check status. Never spawn a second editor.

**Read a scene with the editor closed.** `godot_open_mcp_scene_get_data` is `live-first`: it falls
back to disk when the bridge is unreachable. The disk view does not reflect unsaved edits and
carries no live node instance ids — say so when reporting.

**Read project state safely.** `godot_open_mcp_resource_get_data`, `godot_open_mcp_scene_get_data`,
`godot_open_mcp_node_find`, and `godot_open_mcp_reflection_method_find` are read-only and gate-free.
Use them to plan a mutation before committing `paths_hint`.

## Checklist

Before mutating:
- [ ] `godot_open_mcp_capabilities` called this session.
- [ ] `typed-editor` active (or the typed tool you need is visible).
- [ ] `paths_hint` enumerates every `res://` path the call can touch.
- [ ] `gate: "enforce"` (or an explicit reason for `warn` / `off`).
- [ ] Checkpoint captured for multi-step work.

After mutating:
- [ ] `gate.delta` inspected; no new Error-severity issues remain.
- [ ] `agentNextSteps` followed (fix, validate, revert) until clean.
- [ ] For `godot_open_mcp_apply_fix`: confirm with `godot_open_mcp_validate_edit` or
  `godot_open_mcp_delta`; inspect `rollback` if present.
