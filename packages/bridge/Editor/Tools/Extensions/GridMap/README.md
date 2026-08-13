# GridMap domain pack

Six typed tools for Godot 4.3+ `GridMap` (3D grid-based level construction).

- **Group id:** `gridmap` (hidden from `ListTools` until activated via `manage_tools`).
- **Route:** `live` (needs the bridge + an edited scene).
- **Compile gate:** none — `GridMap` is an engine API present in every 4.3+ build.

## Tool roster

| Tool | Mutating | Purpose |
|---|---|---|
| `godot_open_mcp_gridmap_create` | yes | Create a `GridMap` node (Node3D) in the edited scene. |
| `godot_open_mcp_gridmap_set_mesh_library` | yes | Assign an existing `MeshLibrary` (`res://`) to a `GridMap`. |
| `godot_open_mcp_gridmap_set_cell` | yes | Paint one cell (x, y, z, item, orientation). |
| `godot_open_mcp_gridmap_erase_cell` | yes | Erase one cell (sets item to `INVALID_CELL_ITEM`, -1). |
| `godot_open_mcp_gridmap_get_used_cells` | no | List used cells (bounded by `max_results`). |
| `godot_open_mcp_gridmap_clear` | yes | Clear all cells; keep the MeshLibrary. |

## Godot API notes

- A `GridMap` is a `Node3D` subclass; `position` is an `"x,y,z"` string.
- Cells are addressed by a 3D integer coordinate (`Vector3I`) plus a
  `MeshLibrary` item id and an orientation (0–23 Godot orthonormal rotations).
- Godot has **no dedicated erase method** — erasing is `SetCellItem` with the
  `INVALID_CELL_ITEM` sentinel (-1), the same value `GetCellItem` returns for an
  empty cell.
- `MeshLibrary.GetItemList()` returns the valid item ids; `set_cell` rejects an
  id that is not present with `invalid_parameter` (Godot's `SetCellItem` would
  otherwise silently no-op).
- `set_cell` on a layer with no MeshLibrary returns `mesh_library_required`
  (Godot's `SetCellItem` would otherwise silently no-op).
