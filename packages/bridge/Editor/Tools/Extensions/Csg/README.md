# CSG domain pack

Phase 12.5 — seven typed tools for Godot 4.3+ constructive-solid geometry primitives (`CsgBox3D`, `CsgSphere3D`, `CsgCylinder3D`, `CsgCombiner3D`) and their boolean operations. Mirrors the P12.1–P12.4 packs' folder layout, registration shape, gate policy, and group assignment.

## Group + tool roster

Group id: **`csg`** (default-on: false — activate via `godot_open_mcp_manage_tools`).

| Tool | Mutating | Gate | Purpose |
|---|---|---|---|
| `godot_open_mcp_csg_defaults` | no | off | Recommended starter scalars for a kind (pure helper, no scene) |
| `godot_open_mcp_csg_box_create` | yes | enforce | Create a `CsgBox3D` node (optional `size` + `operation`) |
| `godot_open_mcp_csg_sphere_create` | yes | enforce | Create a `CsgSphere3D` node (optional `radius` / `radial_segments` / `rings` / `smooth_faces` + `operation`) |
| `godot_open_mcp_csg_cylinder_create` | yes | enforce | Create a `CsgCylinder3D` node (optional `radius` / `height` / `sides` / `cone` / `smooth_faces` + `operation`) |
| `godot_open_mcp_csg_combiner_create` | yes | enforce | Create a `CsgCombiner3D` container (optional `operation`) |
| `godot_open_mcp_csg_set_operation` | yes | enforce | Set a CSG shape's boolean operation (union / intersection / subtraction) |
| `godot_open_mcp_csg_get` | no | off | Read a CSG shape's scalar config (+ type + kind + operation) |

## Scope

- `CsgBox3D`, `CsgSphere3D`, `CsgCylinder3D`, `CsgCombiner3D` only — **not** `CsgTorus3D`, **not** `CsgPolygon3D`, **not** `CsgMesh3D`.
- Scalar / enum configure surface only at create time in v1 — **not** a `csg_configure` mutator (the boolean `set_operation` covers the only mutable runtime-relevant property; shape primitives' sizes are set at create and Godot rebuilds the mesh on change). An agent wanting to change a primitive's size after create uses `node_modify` on the relevant property.
- No face-editing API (no equivalent of ProBuilder's extrude / delete_faces / set_face_material — CSG has no face API).
- No full mesh vertex dump in `get` v1 — only the scalar config snapshot.
- No 2D CSG — Godot has no 2D CSG primitives (CSGPolygon2D does not exist).
- No separate NuGet/extension package — embedded in the main bridge addon.

## Scalar allow-list

The create tools accept kind-specific scalars. Unknown keys are rejected by the schema (`additionalProperties:false`); the body parser only extracts the fixed known set. Clamping is centralized in `CsgPropertyClamp` (unit-tested without the editor).

| Kind | Property (schema key) | Godot property | Type | Valid range | Clamp |
|---|---|---|---|---|---|
| box | `size` | `Size` | Vector3 ("x,y,z") | > 0 (per component) | `≥ 0.0001` per component |
| sphere | `radius` | `Radius` | float | > 0 | `≥ 0.0001` |
| sphere | `radial_segments` | `RadialSegments` | int | ≥ 3 | `[3, 1000]` |
| sphere | `rings` | `Rings` | int | ≥ 3 | `[3, 1000]` |
| sphere | `smooth_faces` | `SmoothFaces` | bool | — | pass-through |
| cylinder | `radius` | `Radius` | float | > 0 | `≥ 0.0001` |
| cylinder | `height` | `Height` | float | > 0 | `≥ 0.0001` |
| cylinder | `sides` | `Sides` | int | ≥ 3 | `[3, 1000]` |
| cylinder | `cone` | `Cone` | bool | — | pass-through |
| cylinder | `smooth_faces` | `SmoothFaces` | bool | — | pass-through |

`operation` (union / intersection / subtraction) is shared by every kind and applies via Godot's `CsgShape3D.OperationEnum`. The combiner accepts no primitive scalars — its only knob is `operation`.

## Boolean workflow

Godot's CSG combiner is implicit: a `CsgCombiner3D` parent collects its child `CsgShape3D` children, and each child's `Operation` property defines how it combines with the sibling-before-it. The pack surfaces this with three steps:

1. `csg_combiner_create` to make the parent (or use an existing combiner).
2. Create primitives with `parent_node_path` = the combiner.
3. `csg_set_operation` on each child (e.g. subtraction on a cutter carves out the cutter's shape).

Godot rebuilds the CSG mesh when the scene updates; the pack marks the scene dirty and does not force a manual rebuild.

## Kind + operation selection

`defaults` takes a `kind` arg ("box" | "sphere" | "cylinder" | "combiner"). The body parser maps it to a `CsgKind` enum; an absent or unrecognized token becomes `invalid_parameter` at the handler. The four create tools infer the kind from the tool name (no `kind` arg on creates).

`set_operation` takes an `operation` arg ("union" | "intersection" | "subtraction"). The body parser maps it to a `CsgOperation` enum; an absent or unrecognized token becomes `missing_parameter` at the handler.

## Godot API notes

- `new CsgBox3D()` / `new CsgSphere3D()` / `new CsgCylinder3D()` / `new CsgCombiner3D()` — concrete engine nodes present in every 4.3+ build; no `ClassDB.ClassExists` guard needed. (C# PascalCase is `CsgBox3D` etc.; the engine/GDScript identifier is `CSGBox3D` etc.)
- The base class `CsgShape3D` carries the `Operation` property — primitives and combiner all inherit it, so `set_operation` accepts any CSG shape.
- Godot's `CsgShape3D.OperationEnum` has three members: `Union` (0, default), `Intersection` (1), `Subtraction` (2).
- Engine defaults: box size `(1, 1, 1)`; sphere radius `0.5` / radial_segments `12` / rings `6` / smooth_faces `true`; cylinder radius `0.5` / height `2.0` / sides `8` / cone `false` / smooth_faces `true`.
- Owner assignment to the edited scene root makes the new node persist in the `.tscn` on save (Godot-specific; Unity has no equivalent).

## Gate contract

The five mutators (four creates + set_operation) register `defaultGate: "enforce"` and validate `paths_hint` at the handler level (the dispatch layer also rejects an empty hint when the effective gate is not `off`; the handler-level guard ALSO fires when an agent overrides with `gate: "off"`). `paths_hint` is scoped to the edited scene resource path (`res://…tscn`). The read-only `defaults` and `get` have no gate surface.

## Error codes

Reuses the shared Phase 12 vocabulary (`no_edited_scene`, `node_not_found`, `wrong_node_type`, `missing_parameter`, `invalid_parameter`, `paths_hint_required`, `parent_not_found`, `create_failed`). No pack-specific codes in v1 — `invalid_parameter` covers a bad `kind` token; `missing_parameter` covers a missing/invalid `operation` or `node_path`.
