// `godot_open_mcp_spatial_query` tool definition (P16.6).
//
// Read-only physics query against the edited scene's physics world. One tool
// dispatches three query kinds (ray / shape / point) across two dimensions
// (2d / 3d) via Godot's PhysicsDirectSpaceState2D/3D. The handler lives in the
// bridge (POST /tools/godot_open_mcp_spatial_query); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's `unity_senses_spatial_query`
// (Spatial/ManageSpatialQueryTool.cs — adapt fidelity): same query-kind +
// shape-enum + collision-mask + bounded-results contract and the same hit-result
// shape (collider / point / normal / distance). Intentional deltas:
//   - Godot PhysicsDirectSpaceState2D/3D intersect_* instead of Unity
//     Physics/Physics2D (greenfield dispatch).
//   - `ray` uses Godot-native `from`/`to` (two endpoints) rather than Unity's
//     origin/direction/max_distance.
//   - `exclude` takes collider NODE PATHS (resolved to physics RIDs inside the
//     handler) — Godot RIDs do not serialize to round-trippable JSON, and node
//     paths are stable + human-readable. Hit results echo `node_path` so an agent
//     can feed a prior hit straight back into `exclude`.
//   - `size` (rectangle/box) uses Godot's full-extent convention, not half-extents.
//   - Live-only: queries target the edited scene's viewport physics space
//     (edit-time transforms). An inactive / locked space surfaces `no_active_space`;
//     a playing scene is the most reliable source of a stepped physics world.
//
// Read-only (gate-free) — no paths_hint, no gate. Like screenshots, it creates no
// project state. This is a `spatial` group tool — it is hidden from ListTools
// until an agent activates the group via `godot_open_mcp_manage_tools({
// action: "activate", group: "spatial" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const spatialQuery: Tool = {
  name: "godot_open_mcp_spatial_query",
  description:
    "Probe the edited scene's physics world with a ray / shape / point query (2D or 3D) and " +
    "return bounded, structured hit results. Picks one of three query kinds via `query_type` " +
    "(ray / shape / point) and one of two physics worlds via `dimension` (2d / 3d).\n\n" +
    "ray: casts a segment from `from` to `to`; returns the single closest hit (collider node " +
    "path, position, normal, shape) or hit:false. shape: overlaps a `shape` (circle / sphere / " +
    "rectangle / box / capsule) centered at `position` (+ optional `radius` / `size` / `height` / " +
    "`rotation`); returns up to `max_results` hits (truncated:true if more existed). point: reports " +
    "which bodies contain `position`; same bounded multi-hit shape.\n\n" +
    "circle / rectangle are 2D only; sphere / box are 3D only; capsule works in both. `mask` is a " +
    "collision bitmask (default all layers); `exclude` is a list of collider node paths to skip; " +
    "`collide_with_bodies` (default true) and `collide_with_areas` (default false) select what the " +
    "query registers.\n\n" +
    "Live-only: needs an active physics world (a playing scene is most reliable). An inactive or " +
    "locked space returns no_active_space. Read-only (gate-free). This is a `spatial` group tool — " +
    "activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["query_type", "dimension"],
    properties: {
      query_type: {
        type: "string",
        enum: ["ray", "shape", "point"],
        description:
          "Query kind. 'ray': closest hit along the from→to segment. 'shape': all bodies overlapping " +
          "a shape at `position`. 'point': all bodies containing `position`.",
      },
      dimension: {
        type: "string",
        enum: ["2d", "3d"],
        description:
          "Which physics world to query. '2d' resolves PhysicsDirectSpaceState2D (Vector2 inputs); " +
          "'3d' resolves PhysicsDirectSpaceState3D (Vector3 inputs).",
      },
      from: {
        type: "string",
        description:
          "Ray origin (query_type 'ray'). 'x,y' for 2d, 'x,y,z' for 3d. Required when query_type is " +
          "'ray'.",
      },
      to: {
        type: "string",
        description:
          "Ray end point (query_type 'ray'). 'x,y' for 2d, 'x,y,z' for 3d. Required when query_type " +
          "is 'ray'.",
      },
      shape: {
        type: "string",
        enum: ["circle", "sphere", "rectangle", "box", "capsule"],
        description:
          "Overlap shape (query_type 'shape'). 'circle' / 'rectangle' are 2d only; 'sphere' / 'box' " +
          "are 3d only; 'capsule' works in both. Required when query_type is 'shape'.",
      },
      position: {
        type: "string",
        description:
          "Shape center or probe point (query_type 'shape' / 'point'). 'x,y' for 2d, 'x,y,z' for 3d.",
      },
      radius: {
        type: "number",
        exclusiveMinimum: 0,
        description:
          "Radius for circle / sphere / capsule shapes. Godot rejects a zero/negative radius; use a " +
          "small positive value for a thin probe.",
      },
      size: {
        type: "string",
        description:
          "Full extent (NOT half-extent) for rectangle / box shapes — matches Godot's RectangleShape2D.Size / " +
          "BoxShape3D.Size. 'x,y' for 2d, 'x,y,z' for 3d. e.g. size '2,4' makes a 2×4 rectangle.",
      },
      height: {
        type: "number",
        exclusiveMinimum: 0,
        description: "Total height for capsule shapes (along the capsule's local Y axis).",
      },
      rotation: {
        type: "string",
        description:
          "Optional shape rotation in degrees. 3d: Euler 'x,y,z'. 2d: a single degree value 'd'. Default " +
          "identity. Ignored for ray / point queries.",
      },
      mask: {
        type: "integer",
        minimum: 0,
        description:
          "Collision bitmask selecting which physics layers the query registers. Default: all layers " +
          "(every layer enabled). Godot layer names map to bit positions in project.godot.",
      },
      exclude: {
        type: "array",
        items: { type: "string" },
        description:
          "Collider node paths to skip, resolved to physics RIDs inside the handler. Pass a hit's " +
          "returned `node_path` to filter it out. Unknown paths are ignored.",
      },
      collide_with_bodies: {
        type: "boolean",
        default: true,
        description: "Register physics bodies (RigidBody / StaticBody / CharacterBody / AnimatableBody).",
      },
      collide_with_areas: {
        type: "boolean",
        default: false,
        description: "Register Area2D / Area3D detection zones (off by default).",
      },
      max_results: {
        type: "integer",
        minimum: 1,
        default: 32,
        description:
          "Cap on returned hits for shape / point queries (ray returns at most one). When more hits " +
          "exist, the result is truncated to this count and reports truncated:true.",
      },
    },
    additionalProperties: false,
  },
};
