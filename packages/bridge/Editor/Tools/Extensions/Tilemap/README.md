# Tilemap domain pack

Phase 12.1 — six typed tools for Godot 4.3+ `TileMapLayer`. The first domain pack and the reference implementation for P12.2–P12.5 (same folder layout, registration shape, gate policy, and group assignment).

## Group + tool roster

Group id: **`tilemap`** (default-on: false — activate via `godot_open_mcp_manage_tools`).

| Tool | Mutating | Gate | Purpose |
|---|---|---|---|
| `godot_open_mcp_tilemap_create` | yes | enforce | Create a `TileMapLayer` node under a parent (default: edited scene root) |
| `godot_open_mcp_tilemap_set_tileset` | yes | enforce | Assign an existing `TileSet` resource (`res://…tres/.res`) to a layer |
| `godot_open_mcp_tilemap_set_cell` | yes | enforce | Paint one cell (map coords + source/atlas/alternative) |
| `godot_open_mcp_tilemap_erase_cell` | yes | enforce | Erase one cell |
| `godot_open_mcp_tilemap_get_used_cells` | no | off | List used cells (bounded by `max_results`) |
| `godot_open_mcp_tilemap_clear` | yes | enforce | Clear all cells; keep the TileSet |

## Scope

- `TileMapLayer` only (Godot 4.3+). The deprecated `TileMap` multi-layer node is **not** supported; a non-`TileMapLayer` target returns `wrong_node_type`.
- No GridMap, no RuleTile, no tile-asset authoring in v1 (agents point `set_tileset` at an existing `.tres`/`.res` TileSet).
- No separate NuGet/extension package — embedded in the main bridge addon.

## Godot API notes

- `new TileMapLayer()` — the node is a concrete engine class present in every 4.3+ build; no `ClassDB.ClassExists` guard needed (unlike the generic `node_create` path).
- `SetCell(Vector2I coords, int sourceId, Vector2I atlasCoords, int alternativeTile)` — Godot's atlas addressing quadruple (NOT a Unity Tile asset path).
- `GetUsedCells()` → `Godot.Collections.Array<Vector2I>`; read-back of the addressing quadruple via `GetCellSourceId` / `GetCellAtlasCoords` / `GetCellAlternativeTile`.
- `EraseCell(Vector2I)` and `Clear()` are the cell-emptying primitives; `Clear` keeps the TileSet.
- Owner assignment to the edited scene root makes the new node persist in the `.tscn` on save (Godot-specific; Unity has no equivalent).

## Gate contract

The five mutators register `defaultGate: "enforce"` and validate `paths_hint` at the handler level (the dispatch layer also rejects an empty hint when the effective gate is not `off`; the handler-level guard ALSO fires when an agent overrides with `gate: "off"`). `paths_hint` is scoped to the edited scene resource path (`res://…tscn`). The read-only `get_used_cells` has no gate surface.

## Error codes

Reuses the shared Phase 12 vocabulary (`no_edited_scene`, `node_not_found`, `wrong_node_type`, `missing_parameter`, `invalid_parameter`, `paths_hint_required`, `resource_not_found`, `resource_load_failed`) plus one pack-specific code:

| Code | When |
|---|---|
| `tileset_required` | `set_cell` called on a layer with no TileSet assigned (Godot's `SetCell` silently no-ops in that state; the guard surfaces it) |
