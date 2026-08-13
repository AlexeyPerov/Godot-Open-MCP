# PhantomCamera domain pack

Six typed tools for the third-party **phantom-camera** GDScript addon
(Cinemachine-style virtual cameras by ramokz).

- **Group id:** `phantom_camera` (hidden from `ListTools` until activated via
  `manage_tools`).
- **Route:** `live` (needs the bridge + an edited scene).
- **Compile gate:** runtime — the addon classes (`PhantomCamera2D` /
  `PhantomCamera3D`) are GDScript and register into ClassDB at runtime when the
  addon is enabled. Every handler detects them via `ClassDB.ClassExists`; if the
  addon is not enabled, every tool surfaces `addon_not_found` (the
  architecture-faithful equivalent of a compile gate — Godot Open MCP has no
  per-pack compile inventory).

## Tool roster

| Tool | Mutating | Purpose |
|---|---|---|
| `godot_open_mcp_phantom_camera_create` | yes | Create a `PhantomCamera3D` (default) / `PhantomCamera2D` node. |
| `godot_open_mcp_phantom_camera_set_target` | yes | Set the follow target node (`follow_target`). |
| `godot_open_mcp_phantom_camera_set_priority` | yes | Set the priority int (higher wins). |
| `godot_open_mcp_phantom_camera_set_follow` | yes | Set the follow mode ordinal (+ optional follow target). |
| `godot_open_mcp_phantom_camera_set_look_at` | yes | Set the look-at target node (+ optional look-at mode). |
| `godot_open_mcp_phantom_camera_get` | no | Read the camera's scalar config (read-only). |

## Phantom-camera addon API notes

- The classes are GDScript — they are **not** statically available to the C#
  bridge. Handlers operate via duck-typed `GodotObject.Set/Get` against the
  addon's `@export` property names. An addon version that renamed a property
  surfaces `execution_error` with the addon's own message rather than crashing.
- `dimension` on `create` selects `PhantomCamera3D` (`"3d"`, default) vs
  `PhantomCamera2D` (`"2d"`).
- `follow_mode` is the addon's `FollowMode` enum ordinal: 0 none / 1 glued / 2
  simple_follow / 3 group_follow / 4 path_follow / 5 framed / 6 third_person.
- `look_at_mode` is the addon's `LookAtMode` enum ordinal: 0 none / 1 mimic / 2
  simple / 3 group. It is optional on `set_look_at` (absent = leave the mode
  unchanged; only the target is set).
- A PhantomCamera is inert until a `PhantomCameraHost` exists under a
  `Camera3D`/`Camera2D` in the scene and the camera has a priority. This pack
  does **not** auto-create a host — ensure one exists, then call `set_priority`.
- Target properties (`follow_target` / `look_at_target`) are set to the resolved
  scene Node; `get` reads them back as scene paths (or `null` when unassigned).
