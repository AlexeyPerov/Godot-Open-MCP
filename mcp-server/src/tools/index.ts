// Tool registry — single export point for the stdio MCP server.
//
// Every MCP tool is defined in `src/tools/{tool-name}.ts` and added to the
// `ALL_TOOLS` array below. The registry started empty in the P1.5 scaffold;
// the first tool (`godot_open_mcp_ping`) lands here in P1.7 alongside the
// live bridge client. Subsequent phases (P2.x editor tools, P3.x gate, ...)
// append their tools to this array.
//
// Per mcp-server/AGENTS.md:
//   - tool names follow the `godot_open_mcp_*` convention,
//   - every tool definition carries `name`, `description`, `inputSchema`,
//     and a handler (the handler lives in LiveClient / tool-router; the
//     definition here is catalog metadata only),
//   - the bridge-side C# handler must stay in sync when a schema changes.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { ping } from "./ping.js";
import { nodeFind } from "./node-find.js";
import { nodeCreate } from "./node-create.js";
import { nodeModify } from "./node-modify.js";
import { nodeSetParent } from "./node-set-parent.js";
import { nodeDuplicate } from "./node-duplicate.js";
import { nodeDelete } from "./node-delete.js";
import { sceneOpen } from "./scene-open.js";
import { sceneSave } from "./scene-save.js";
import { sceneListOpened } from "./scene-list-opened.js";
import { sceneGetData } from "./scene-get-data.js";
import { sceneCreate } from "./scene-create.js";
import { validateEdit } from "./validate-edit.js";
import { checkpointCreate } from "./checkpoint-create.js";
import { delta } from "./delta.js";
import { applyFix } from "./apply-fix.js";
import { capabilities } from "./capabilities.js";
import { resourceFind } from "./resource-find.js";
import { resourceGetData } from "./resource-get-data.js";
import { resourceCreate } from "./resource-create.js";
import { resourceModify } from "./resource-modify.js";
import { resourceMove } from "./resource-move.js";
import { resourceDelete } from "./resource-delete.js";
import { filesystemList } from "./filesystem-list.js";
import { filesystemReimport } from "./filesystem-reimport.js";
import { editorApplicationGetState } from "./editor-application-get-state.js";
import { editorApplicationSetState } from "./editor-application-set-state.js";
import { editorSelectionGet } from "./editor-selection-get.js";
import { editorSelectionSet } from "./editor-selection-set.js";
import { consoleGetLogs } from "./console-get-logs.js";
import { consoleClearLogs } from "./console-clear-logs.js";
import { screenshotViewport } from "./screenshot-viewport.js";
import { screenshotCamera } from "./screenshot-camera.js";
import { screenshotIsolated } from "./screenshot-isolated.js";
import { reflectionMethodFind } from "./reflection-method-find.js";
import { reflectionMethodCall } from "./reflection-method-call.js";
import { bridgeStatus } from "./bridge-status.js";
import { pullEvents } from "./pull-events.js";

/** Ordered list of every tool exposed over stdio MCP. */
export const ALL_TOOLS: Tool[] = [
  ping,
  nodeFind,
  nodeCreate,
  nodeModify,
  nodeSetParent,
  nodeDuplicate,
  nodeDelete,
  sceneOpen,
  sceneSave,
  sceneListOpened,
  sceneGetData,
  sceneCreate,
  // P3.6 — gate meta-tools (read-only, group core). The explicit checkpoint →
  // mutate → delta workflow surface: validate_edit for a scoped health check,
  // checkpoint_create to capture a baseline, delta to compare post-mutation.
  validateEdit,
  checkpointCreate,
  delta,
  // P3.7 — apply_fix: apply (or preview) a structured fix for a verify issue. Mutating
  // (non-dry-run applies run through the gate with safe auto-fix rollback); a dry-run
  // apply bypasses the gate. The initial Safe:true provider is remove_missing_script.
  applyFix,
  // P3.8 — capabilities: discover the full capability surface (tools + verify rules + fixes).
  // Built locally in the MCP server (no bridge hop); the CallTool handler special-cases the name.
  capabilities,
  // P4.1 — resource read tools: resource_find (exact path/UID lookup + indexed type search) and
  // resource_get_data (bounded, cycle-safe property inspection). Read-only, group resource. Live
  // route (POST /tools/{name}); offline resource reads arrive in Phase 7.
  resourceFind,
  resourceGetData,
  // P4.2 — resource mutation tools: resource_create (instantiate a Resource subclass + save) and
  // resource_modify (apply validated property-path patches + save). Mutating, group resource,
  // default gate enforce. Both write .tres/.res files to disk through ResourceSaver.
  resourceCreate,
  resourceModify,
  // P4.3 — resource file lifecycle tools: resource_move (relocate a .tres/.res file + .import
  // sidecar via DirAccess) and resource_delete (remove a .tres/.res file + .import sidecar).
  // Mutating, group resource, default gate enforce. Both handle the .import sidecar explicitly and
  // reconcile the editor filesystem after the operation. No reference rewriting.
  resourceMove,
  resourceDelete,
  // P4.4 — filesystem tools: filesystem_list (indexed res:// directory listing, read-only) and
  // filesystem_reimport (exact-file reimport or full scan, mutating). List reads the editor
  // filesystem index without loading resources; reimport maps AssetDatabase.Refresh to Godot's
  // ReimportFiles/Scan with a bounded, truthful settle status.
  filesystemList,
  filesystemReimport,
  // P4.5 — editor application-state tools: editor_application_get_state (read-only play-process
  // snapshot) and editor_application_set_state (start main/current/custom scene or stop play, gated).
  // Get-state reports isPlaying + playingScene + editorVersion + observedAt (no Unity pause/compile);
  // set-state observes the requested transition with a bounded deadline and never claims an unobserved
  // state. Godot launches the game as a separate OS process, so the state model is start/stop only.
  editorApplicationGetState,
  editorApplicationSetState,
  // P4.6 — editor selection tools: editor_selection_get (read-only node selection snapshot) and
  // editor_selection_set (replace/clear the selection, gated). Get reports the selected nodes as
  // shallow NodeData + the active (last-selected) node + count + scene path; set resolves every ref
  // before clearing (all-or-nothing) and returns the observed post-change selection. Godot selection
  // is node-only — no asset/component/global-object fields.
  editorSelectionGet,
  editorSelectionSet,
  // P4.7 — console log tools: console_get_logs (read-only query of the bounded collector, newest-first,
  // with severity/age/max-entries filters and capture-capability metadata) and console_clear_logs
  // (gate-free direct — clears only the ephemeral addon collector, never the native Output panel).
  // The collector is fed by the bridge's own logging path; Godot exposes no managed global log hook at
  // 4.3, so this is the addon's captured activity, not the entire editor Output. Both tools are
  // gate-free (get is read-only; clear mutates only ephemeral state — no checkpoint/delta coverage).
  consoleGetLogs,
  consoleClearLogs,
  // P4.8 — screenshot tools: screenshot_viewport (active editor 2D/3D viewport), screenshot_camera
  // (off-screen capture from a Camera2D/Camera3D), and screenshot_isolated (render a Node3D in an
  // isolated world from six views). All three are read-only (gate-free) and return the PNG as an MCP
  // image content block (image/png) plus a short text metadata block. The bridge image envelope
  // (mediaType + base64 data + metadata) is unwrapped by live-client.ts — the base64 payload never
  // appears inside a text JSON block on success. Temporary render nodes (SubViewport, clone camera,
  // light) are freed on every path; no project files are written.
  screenshotViewport,
  screenshotCamera,
  screenshotIsolated,
  // P5.1 — reflection tools: reflection_method_find (read-only member discovery across loaded
  // Godot/.NET assemblies) and reflection_method_call (gated method invoke). Find is gate-free and
  // returns bounded, structured member entries (returnType/parameters[]/isStatic/isGeneric/
  // genericParameters[] for methods) so an agent can plan an invoke without hallucinating signatures;
  // call resolves a method by type+name (overload + generic disambiguation), targets an instance via
  // node_path from the edited scene (Godot-native; replaces Unity's object_id-first targeting), and
  // serializes the return value with depth/cycle guards. Godot.Object subclasses require an explicit
  // node_path — Activator is used only for pure POCOs.
  reflectionMethodFind,
  reflectionMethodCall,
  // P5.3 — bridge_status: operator-oriented health snapshot. Composes the instance-lock classifier
  // (instance-discovery.ts#classifyInstance) with one /ping probe and returns a coarse status token
  // (running | compiling | stopped | unreachable | dead_bridge) + classification + recoveryHint.
  // Local/live hybrid route: the CallTool dispatcher special-cases the name and calls
  // LiveClient.routeBridgeStatus (no POST /tools/bridge_status endpoint on the bridge). Read-only,
  // gate-free, never spawns Godot. recoveryHint is non-null only for dead_bridge.
  bridgeStatus,
  // P5.4 — pull_events: drain incremental bridge events (console logs + editor-state transitions)
  // from the per-process BridgeEventStream SSE subscription. First call opens the subscription;
  // later calls return only new events. Local-drains-live-stream route: the dispatcher special-cases
  // the name and calls BridgeEventStream.pull directly (no POST /tools/pull_events endpoint). Read-
  // only, gate-free, live (requires a connected bridge). Returns connected:false + lastError on an
  // offline bridge instead of throwing.
  pullEvents,
];
