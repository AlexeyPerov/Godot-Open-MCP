# Navigation domain pack

Phase 12.2 — seven typed tools for Godot 4.3+ navigation nodes (`NavigationRegion2D/3D`, `NavigationAgent2D/3D`, `NavigationLink2D/3D`). Mirrors the P12.1 tilemap pack's folder layout, registration shape, gate policy, and group assignment.

## Group + tool roster

Group id: **`navigation`** (default-on: false — activate via `godot_open_mcp_manage_tools`).

| Tool | Mutating | Gate | Purpose |
|---|---|---|---|
| `godot_open_mcp_navigation_defaults` | no | off | Recommended starter scalars for a 2D/3D agent (pure helper, no scene) |
| `godot_open_mcp_navigation_region_create` | yes | enforce | Create a `NavigationRegion2D` or `NavigationRegion3D` node |
| `godot_open_mcp_navigation_region_set_mesh` | yes | enforce | Assign a `NavigationPolygon` (2D) or `NavigationMesh` (3D) resource |
| `godot_open_mcp_navigation_agent_create` | yes | enforce | Create a `NavigationAgent2D` or `NavigationAgent3D` node |
| `godot_open_mcp_navigation_agent_configure` | yes | enforce | Patch clamped scalar properties on an agent |
| `godot_open_mcp_navigation_link_create` | yes | enforce | Create a `NavigationLink2D` or `NavigationLink3D` with start/end |
| `godot_open_mcp_navigation_get` | no | off | Read scalar config (+ type + dimension) for any navigation node |

## Scope

- Built-in Godot navigation nodes only (`NavigationRegion2D/3D`, `NavigationAgent2D/3D`, `NavigationLink2D/3D`).
- No third-party nav addons.
- No bake / modifier-volume surface in v1 — the pack is create + assign resource + configure + inspect. Baking a `NavigationPolygon` / `NavigationMesh` from geometry is deferred (agents supply a pre-authored resource, or use the editor's bake UI).
- No separate NuGet/extension package — embedded in the main bridge addon.

## Dimension selection

Every create tool (and `defaults`) takes a `dimension` arg (`"2d"` | `"3d"`). The body parser maps it to a `NavDimension` enum; an absent or unrecognized token becomes `invalid_parameter` at the handler. `set_mesh` / `agent_configure` / `get` infer the dimension from the resolved node's class.

## Godot API notes

- `new NavigationRegion2D()` / `new NavigationRegion3D()` — concrete engine nodes present in every 4.3+ build; no `ClassDB.ClassExists` guard needed.
- Region resource assignment: `NavigationRegion2D.NavigationPolygon` (a `NavigationPolygon`) and `NavigationRegion3D.NavigationMesh` (a `NavigationMesh`). Loaded via typed `ResourceLoader.Load<T>` — a type mismatch returns `resource_load_failed`.
- Agent scalars: `Radius`, `Height`, `MaxSpeed`, `PathDesiredDistance`, `TargetDesiredDistance`, `AvoidanceEnabled` — same property names on both `NavigationAgent2D` and `NavigationAgent3D`. `agent_configure` clamps: radius > 0, height ≥ 0, max_speed ≥ 0, distances > 0.
- Link positions: `NavigationLink.StartPosition` / `EndPosition` are LOCAL to the link node (Godot exposes them in local space). `Bidirectional` controls traversal direction.
- Owner assignment to the edited scene root makes the new node persist in the `.tscn` on save (Godot-specific; Unity has no equivalent).

## Gate contract

The five mutators register `defaultGate: "enforce"` and validate `paths_hint` at the handler level (the dispatch layer also rejects an empty hint when the effective gate is not `off`; the handler-level guard ALSO fires when an agent overrides with `gate: "off"`). `paths_hint` is scoped to the edited scene resource path (`res://…tscn`). The read-only `defaults` and `get` have no gate surface.

## Error codes

Reuses the shared Phase 12 vocabulary (`no_edited_scene`, `node_not_found`, `wrong_node_type`, `missing_parameter`, `invalid_parameter`, `paths_hint_required`, `resource_not_found`, `resource_load_failed`, `parent_not_found`, `create_failed`). No pack-specific codes in v1 — `invalid_parameter` covers bad dimension tokens and malformed position strings.
