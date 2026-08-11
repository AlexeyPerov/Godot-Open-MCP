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
import { tilemapCreate } from "./tilemap-create.js";
import { tilemapSetTileset } from "./tilemap-set-tileset.js";
import { tilemapSetCell } from "./tilemap-set-cell.js";
import { tilemapEraseCell } from "./tilemap-erase-cell.js";
import { tilemapGetUsedCells } from "./tilemap-get-used-cells.js";
import { tilemapClear } from "./tilemap-clear.js";
import { navigationDefaults } from "./navigation-defaults.js";
import { navigationRegionCreate } from "./navigation-region-create.js";
import { navigationRegionSetMesh } from "./navigation-region-set-mesh.js";
import { navigationAgentCreate } from "./navigation-agent-create.js";
import { navigationAgentConfigure } from "./navigation-agent-configure.js";
import { navigationLinkCreate } from "./navigation-link-create.js";
import { navigationGet } from "./navigation-get.js";
import { particlesDefaults } from "./particles-defaults.js";
import { particlesCreate } from "./particles-create.js";
import { particlesConfigure } from "./particles-configure.js";
import { particlesSetEmitting } from "./particles-set-emitting.js";
import { particlesGet } from "./particles-get.js";
import { animationDefaults } from "./animation-defaults.js";
import { animationPlayerCreate } from "./animation-player-create.js";
import { animationLibraryAdd } from "./animation-library-add.js";
import { animationCreate } from "./animation-create.js";
import { animationAddTrack } from "./animation-add-track.js";
import { animationInsertKey } from "./animation-insert-key.js";
import { animationGet } from "./animation-get.js";
import { csgDefaults } from "./csg-defaults.js";
import { csgBoxCreate } from "./csg-box-create.js";
import { csgSphereCreate } from "./csg-sphere-create.js";
import { csgCylinderCreate } from "./csg-cylinder-create.js";
import { csgCombinerCreate } from "./csg-combiner-create.js";
import { csgSetOperation } from "./csg-set-operation.js";
import { csgGet } from "./csg-get.js";
import { settingsGetProject } from "./settings-get-project.js";
import { settingsSetProject } from "./settings-set-project.js";
import { materialCreate } from "./material-create.js";
import { materialGetProperties } from "./material-get-properties.js";
import { materialSetProperty } from "./material-set-property.js";
import { materialSetShader } from "./material-set-shader.js";
import { shaderGetData } from "./shader-get-data.js";
import { lightCreate } from "./light-create.js";
import { lightSet } from "./light-set.js";
import { lightModify } from "./light-modify.js";
import { environmentSet } from "./environment-set.js";
import { audioStreamPlayerCreate } from "./audio-stream-player-create.js";
import { audioStreamPlayerSetStream } from "./audio-stream-player-set-stream.js";
import { audioBusSetVolume } from "./audio-bus-set-volume.js";
import { controlCreate } from "./control-create.js";
import { controlModify } from "./control-modify.js";
import { containerAdd } from "./container-add.js";
import { containerSetLayout } from "./container-set-layout.js";
import { themeApply } from "./theme-apply.js";
import { spatialQuery } from "./spatial-query.js";
import { bridgeStatus } from "./bridge-status.js";
import { pullEvents } from "./pull-events.js";
import { readCompileErrors } from "./read-compile-errors.js";
import { findReferences } from "./find-references.js";
import { dependencies } from "./dependencies.js";
import { manageTools } from "./manage-tools.js";
import { baselineCreate } from "./baseline-create.js";
import { regressionCheck } from "./regression-check.js";
import { restartEditor } from "./restart-editor.js";
import { resourcePressure } from "./resource-pressure.js";
import { generateSkill } from "./generate-skill.js";

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
  // P12.1 — tilemap domain pack: six TileMapLayer tools (group `tilemap`, hidden until activated
  // via manage_tools). create makes a Godot 4.3+ TileMapLayer node; set_tileset assigns an existing
  // TileSet resource; set_cell / erase_cell paint/remove single cells via Godot's atlas addressing
  // quadruple; get_used_cells lists used cells (read-only, bounded); clear empties every cell while
  // keeping the TileSet. The five mutators default to gate "enforce" and require paths_hint scoped
  // to the edited scene. First Phase 12 domain pack and the reference implementation for P12.2+.
  tilemapCreate,
  tilemapSetTileset,
  tilemapSetCell,
  tilemapEraseCell,
  tilemapGetUsedCells,
  tilemapClear,
  // P12.2 — navigation domain pack: seven navigation tools (group `navigation`, hidden until
  // activated via manage_tools). defaults returns recommended starter scalars for a 2D/3D agent
  // (read-only helper); region_create makes a NavigationRegion2D/3D node; region_set_mesh assigns
  // its navigation resource (NavigationPolygon 2D / NavigationMesh 3D); agent_create makes a
  // NavigationAgent2D/3D node; agent_configure patches clamped scalar properties on an agent;
  // link_create makes a NavigationLink2D/3D off-mesh connection with start/end; get reads any
  // navigation node's scalar config (read-only). The five mutators default to gate "enforce" and
  // require paths_hint scoped to the edited scene. The two read-only tools (defaults + get) are
  // gate-free. Second Phase 12 domain pack.
  navigationDefaults,
  navigationRegionCreate,
  navigationRegionSetMesh,
  navigationAgentCreate,
  navigationAgentConfigure,
  navigationLinkCreate,
  navigationGet,
  // P12.3 — particles domain pack: five GpuParticles tools (group `particles`, hidden until
  // activated via manage_tools). defaults returns recommended starter scalars for a 2D/3D emitter
  // (read-only helper); create makes a GpuParticles2D/3D node with optional initial properties +
  // an optional process_material_path (ParticleProcessMaterial) assignment; configure patches
  // clamped scalar properties on an emitter (allow-list + centralized clamps; emitting is excluded
  // — use set_emitting); set_emitting starts/stops emission with an optional restart that clears
  // existing particles; get reads an emitter's scalar config + type/dimension + process material
  // path (read-only). The three mutators default to gate "enforce" and require paths_hint scoped
  // to the edited scene. The two read-only tools (defaults + get) are gate-free. Third Phase 12
  // domain pack.
  particlesDefaults,
  particlesCreate,
  particlesConfigure,
  particlesSetEmitting,
  particlesGet,
  // P12.4 — animation domain pack: seven AnimationPlayer tools (group `animation`, hidden
  // until activated via manage_tools). defaults returns recommended starter length + loop
  // mode (read-only helper); player_create makes an AnimationPlayer node; library_add
  // registers an empty AnimationLibrary under a name on a player; animation_create creates
  // an Animation clip in a named library (auto-creating the library when missing);
  // add_track adds a value / position_3d / rotation_3d / scale_3d track and returns its
  // index; insert_key inserts a keyframe on a track and returns the key index; get reads
  // the player's libraries / animations / tracks (bounded; keys opt-in). The five mutators
  // default to gate "enforce" and require paths_hint scoped to the edited scene. The two
  // read-only tools (defaults + get) are gate-free. Fourth Phase 12 domain pack.
  animationDefaults,
  animationPlayerCreate,
  animationLibraryAdd,
  animationCreate,
  animationAddTrack,
  animationInsertKey,
  animationGet,
  // P12.5 — CSG domain pack: seven CSG primitive tools (group `csg`, hidden until
  // activated via manage_tools). defaults returns recommended starter scalars for a
  // kind (read-only helper); box_create / sphere_create / cylinder_create /
  // combiner_create make the corresponding Csg*3D node (with optional kind-specific
  // scalars + operation); set_operation sets the boolean operation (union /
  // intersection / subtraction) on any CSG shape; get reads a shape's scalar config
  // (read-only). The five mutators default to gate "enforce" and require paths_hint
  // scoped to the edited scene. The two read-only tools (defaults + get) are
  // gate-free. Fifth and final Phase 12 domain pack — fills the last reserved stub.
  csgDefaults,
  csgBoxCreate,
  csgSphereCreate,
  csgCylinderCreate,
  csgCombinerCreate,
  csgSetOperation,
  csgGet,
  // P16.1 — project-settings pack: two typed tools for project.godot sections (group `settings`,
  // hidden until activated via manage_tools). get_project reads one section (rendering / physics /
  // input / layer_names / autoload / application / display) or a per-section summary ("all");
  // read-only, gate-free. set_project writes key/value pairs within one section via Godot's
  // ProjectSettings.SetSetting + Save API (never raw text edits), gated, paths_hint res://project.godot.
  // The section allowlist rejects unknown sections and "all" (write); per-key failures accumulate as
  // warnings so a batch's good entries still land. First Phase 16 typed-editor-breadth family.
  settingsGetProject,
  settingsSetProject,
  // P16.2 — materials/shaders pack: five typed tools for material + shader inspection/mutation
  // (group `materials`, hidden until activated via manage_tools). material_create instantiates a
  // StandardMaterial3D / ORMMaterial3D / ShaderMaterial and saves it as a .tres; get_properties
  // lists a material's properties + values (read-only); set_property sets one property and saves;
  // set_shader assigns a .gdshader to a ShaderMaterial and saves (uniforms become settable
  // properties); shader_get_data enumerates a .gdshader's uniforms + types (read-only). The three
  // mutators default to gate "enforce" and require paths_hint scoped to the .tres path (or the
  // .tres + .gdshader for set_shader). The two read-only tools are gate-free. Second Phase 16
  // typed-editor-breadth family.
  materialCreate,
  materialGetProperties,
  materialSetProperty,
  materialSetShader,
  shaderGetData,
  // P16.3 — lighting pack: four typed tools for lights + scene environment
  // (group `lighting`, hidden until activated via manage_tools). light_create
  // makes a DirectionalLight3D / OmniLight3D / SpotLight3D / DirectionalLight2D
  // / PointLight2D node with optional starter scalars applied through the same
  // allow-listed + clamped path light_set / light_modify use; light_set patches
  // one light scalar; light_modify bulk-patches multiple; environment_set
  // creates or replaces the WorldEnvironment node's Environment resource. All
  // four mutators default to gate "enforce" and require paths_hint scoped to the
  // edited scene path. Third Phase 16 typed-editor-breadth family.
  lightCreate,
  lightSet,
  lightModify,
  environmentSet,
  // P16.4 — audio pack: three typed tools for audio players + the project bus
  // layout (group `audio`, hidden until activated via manage_tools).
  // audio_stream_player_create makes an AudioStreamPlayer (non-positional) /
  // AudioStreamPlayer2D / AudioStreamPlayer3D node with optional stream + bus +
  // starter scalars applied through the same allow-listed + clamped path;
  // audio_stream_player_set_stream assigns an AudioStream resource to an
  // existing player; audio_bus_set_volume writes a bus volume via AudioServer
  // (native dB, with optional linear→dB conversion). All three mutators
  // default to gate "enforce"; the player tools require paths_hint scoped to the
  // edited scene path, the bus tool to res://project.godot. Fourth Phase 16
  // typed-editor-breadth family.
  audioStreamPlayerCreate,
  audioStreamPlayerSetStream,
  audioBusSetVolume,
  // P16.5 — UI pack: five typed tools for UI controls, containers, and themes
  // (group `ui`, hidden until activated via manage_tools). control_create makes
  // a Button / Label / LineEdit / ... Control subclass by `type` with starter
  // full-rect anchors so the control is visible without manual layout;
  // control_modify bulk-patches allow-listed control scalars (text /
  // tooltip_text / disabled / color / offsets / size_flags / value);
  // container_add makes a VBoxContainer / HBoxContainer / GridContainer /
  // MarginContainer / ScrollContainer; container_set_layout patches container
  // layout properties (separation / columns / alignment / margins); theme_apply
  // loads a Theme .tres and assigns it to a Control subtree (optionally
  // recursive). All five mutators default to gate "enforce" and require
  // paths_hint scoped to the edited scene path. Fifth and final Phase 16
  // typed-editor-breadth family.
  controlCreate,
  controlModify,
  containerAdd,
  containerSetLayout,
  themeApply,
  // P16.6 — spatial_query pack: one read-only typed tool for physics world
  // queries (group `spatial`, hidden until activated via manage_tools).
  // spatial_query dispatches three query kinds (ray / shape / point) across two
  // dimensions (2d / 3d) against the edited scene's PhysicsDirectSpaceState2D/3D.
  // ray returns the single closest hit along a from→to segment; shape overlaps a
  // circle / sphere / rectangle / box / capsule at a position; point reports
  // bodies containing a position. Collision mask + node-path exclude + max_results
  // bounding are honored; results are bounded + truncated. Read-only (gate-free)
  // and live-only — an inactive / locked physics space surfaces no_active_space.
  // Sixth and final Phase 16 typed-editor-breadth family.
  spatialQuery,
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
  // P7.4 — read_compile_errors: offline, filesystem-only diagnostic. Reads a bounded tail of the
  // project's configured Godot log file and extracts structured C#/GDScript/plugin-load diagnostics.
  // The one recovery channel that works when the bridge addon itself failed to compile or load —
  // console_get_logs depends on the bridge-fed addon collector and stops accumulating in that state.
  // Always-offline route: the dispatcher special-cases the name and never calls the bridge or spawns
  // Godot. Resolves the log path from project.godot's debug/file_logging/* settings + platform user-
  // data-dir defaults + the operator GODOT_OPEN_MCP_LOG_FILE env override (no per-call log_path —
  // no arbitrary file-read surface). Read-only, gate-free.
  readCompileErrors,
  // P13.1 — find_references: offline reverse dependency lookup. Scans
  // `.tscn`/`.tres` for `[ext_resource]` / `uid://` references to a target
  // path or uid. Group `asset-intelligence` (default-off); always-offline
  // route — never probes the bridge. Profile + paging via output-profile.ts.
  findReferences,
  // P13.2 — dependencies: offline forward + reverse edges, cycles, impact.
  // Group `asset-intelligence` (default-off); always-offline route.
  dependencies,
  // P8.3 — manage_tools: per-session tool-group visibility mutator. Activates / deactivates /
  // resets / lists groups in the per-session ToolSessionState that ListTools consults to filter
  // tools. Always visible (capabilities + this tool + ping + bridge_status + pull_events +
  // read_compile_errors survive any group teardown). Local-only — no POST /tools/manage_tools
  // endpoint on the bridge. The router special-cases the name and mutates the shared session
  // store; P8.4 wires the tools/list_changed notification that follows a visibility change.
  manageTools,
  // P15.1 — CI regression baseline + check (group `core`, always-offline route).
  // baseline_create runs the offline whole-project scan and writes a schema-v1 baseline JSON
  // (severity summary + per-rule issue keys + ciExcludedRules); regression_check compares the
  // current scan against that baseline by error-count delta and returns the exit-code contract
  // (0 no regression / 1 regression / 2 baseline missing / 3 baseline invalid). Both reuse the
  // offline disk scanner — Godot has no headless editor, so there is no editor spawn.
  baselineCreate,
  regressionCheck,
  // P15.3 — restart_editor: terminate a wedged Godot editor process. Acts on the OS process via
  // process.kill (SIGTERM → SIGKILL on macOS/Linux) or taskkill /T /F on Windows — no bridge
  // round-trip, no Godot spawn. Requires explicit `confirm: true` (dry-run by default); refuses
  // when the Godot hang signature (crash marker OR frozen main thread: live PID + unreachable
  // /ping + stale log) is absent — never restarts on a fixable compile failure. Relaunch is NOT
  // automatic; the response carries "relaunch via the Hub/CLI" guidance. Always-visible meta-tool
  // (no group); local route — the bridge is the thing that dies on a hang, so the tool may not
  // depend on it for its primary path (it consults the bridge only opportunistically for the
  // active-scene-dirty signal before the kill).
  restartEditor,
  // P15.4 — resource_pressure: proactive resource-exhaustion prediction. Samples the live Godot
  // process's fd/handle count server-side (macOS `lsof`; Linux `/proc/<pid>/fd`; Windows
  // `Get-Process.HandleCount` — approximate) and reports headroom + trend. Companion to
  // restart_editor (reactive) + read_compile_errors (diagnosis): catches a slow fd/handle leak
  // across recompiles/reloads BEFORE the editor wedges. Ceiling is probed per-OS (Linux
  // `/proc/<pid>/limits`; macOS `launchctl limit maxfiles`; Windows none) — the actionable signal
  // is the trend (rising/leaking), not the absolute count. Session-scoped sample ring, no disk
  // cache. Always-visible meta-tool (no group); local route — the bridge is the thing that dies
  // on resource exhaustion, so the tool may not depend on it.
  resourcePressure,
  // P15.5 — generate_skill: emit a project-specific SKILL.md that reflects the actual project
  // state (Godot version, enabled plugins, autoloads, available verify rules, key class_name /
  // Node-Resource subclasses) and MERGE it with the canonical playbook. write:false (default)
  // returns the content as a string (preview); write:true persists to one or more client skill
  // dirs via skills/client-paths.json. The canonical playbook stays hand-authored and is never
  // overwritten — the generator appends a `# Project inventory` section. Always-visible meta-tool
  // (no group); local route — no bridge round-trip (reads project.godot + the catalog + the
  // project type scan entirely in the MCP process).
  generateSkill,
];
