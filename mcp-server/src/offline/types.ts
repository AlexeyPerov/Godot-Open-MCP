// Internal parsed model for the offline `.tscn` reader (P7.2).
//
// These types are deliberately SEPARATE from the public `NodeData`-shaped
// result envelope. The parser/hierarchy layers work in raw Godot-text terms
// (section headers, `ext_resource` IDs, `parent` node paths); the public
// builder in `scene-get-data.ts` is the single place that normalizes them to
// the live `scene_get_data` shape an agent already knows.
//
// Module shape mirrors Unity Open MCP's offline split (types → parse →
// hierarchy → api) but the parsed model itself is greenfield — Godot's
// `.tscn` INI-style grammar is unrelated to Unity's YAML document stream
// (fileID/classID/component-list). A leaf type-only module with no runtime
// imports of its own, so the type-stripping-safe import graph stays static.
//
// Intentional deltas from Unity's `offline/types.ts`:
//   - No GUID/`.meta` model — Godot identifies resources by `res://` paths +
//     `uid://` handles, not 32-hex GUIDs. We keep only the `ext_resource` id
//     (the local string token used inside one scene file).
//   - No `componentIDs` / `fatherTransformID` — Godot nodes carry parent via a
//     node path on the `[node ... parent=...]` header, not a Transform ref.
//   - No class-ID → class-name tables — the node `type` is the Godot class
//     name written verbatim in the header.

/**
 * An `ext_resource` declaration: a resource referenced by id from node bodies.
 * For P7.2 the only consumed kind is `type="Script"` (the attached-script
 * path); other ext-resource types are collected but not resolved.
 */
export interface ExternalResource {
  /** Local id token used inside this scene (e.g. `"1_harness"`). */
  id: string;
  /** `type=` attribute verbatim (e.g. `"Script"`, `"PackedScene"`, `"Texture2D"`). */
  type: string;
  /** `path=` attribute verbatim (e.g. `res://player.gd`). */
  path: string;
  /** 1-based line number of the header (for warnings/errors). */
  line: number;
}

/**
 * A parsed `[node ...]` section header, in declaration order. Raw fields are
 * kept verbatim — parent-link tree reconstruction happens in
 * `scene-hierarchy.ts`.
 */
export interface ParsedSceneNode {
  /** `name=` attribute (already unescaped). */
  name: string;
  /** `type=` attribute, or `null` when the node omits it (an instanced root or
   *  an inherited-scene node that takes its type from the source). */
  type: string | null;
  /**
   * `parent=` attribute verbatim (already unescaped), or `null` when the node
   * has no parent header (= a scene root). `"."` means the scene root itself.
   * Any other value is a Godot node path relative to the root
   * (e.g. `"Player"`, `"Player/Sprite"`).
   */
  parent: string | null;
  /** `instance=ExtResource("id")` resolved id, or `null` when not instanced. */
  instanceRef: string | null;
  /** `owner=` attribute verbatim, or `null`. Kept for fidelity; not used in
   *  the P7.2 public tree. */
  owner: string | null;
  /** `script = ExtResource("id")` resolved from the node body, or `null`. */
  scriptRef: string | null;
  /** Declaration order (0-based) — preserves sibling ordering from the file. */
  order: number;
  /** 1-based line number of the header. */
  line: number;
}

/**
 * A non-fatal machine-readable warning. Warnings surface degraded-but-parseable
 * constructs (an inherited scene we do not expand, an unknown property we
 * ignore) so an agent knows the offline read is partial without it being an
 * error. Never blocks the response.
 */
export interface OfflineWarning {
  code: string;
  message: string;
  /** 1-based line number when the warning ties to a specific line, else omitted. */
  line?: number;
}

/**
 * The complete parse of one `.tscn` file. Produced by `scene-parser.ts`;
 * consumed by `scene-hierarchy.ts` and `scene-get-data.ts`. Carries no public
 * result-envelope fields — those are layered on by the builder.
 */
export interface ParsedTscn {
  /** `format=` from the `[gd_scene]` header, or `null` when absent/malformed. */
  format: number | null;
  /** `load_steps=` from the `[gd_scene]` header, or `null`. */
  loadSteps: number | null;
  /** `uid=` from the `[gd_scene]` header (the `uid://...` string), or `null`. */
  uid: string | null;
  /** `[gd_scene ...]` header attributes that mark this as an inherited scene
   *  (`load_steps` + an `instance`-style reference), or `null` for a plain scene. */
  instanceRef: string | null;
  /** ext_resource declarations keyed by id. */
  extResources: Map<string, ExternalResource>;
  /** `[node ...]` sections in declaration order. */
  nodes: ParsedSceneNode[];
  /** Non-fatal warnings collected during parse. */
  warnings: OfflineWarning[];
}
