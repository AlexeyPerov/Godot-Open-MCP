# Particles domain pack

Phase 12.3 — five typed tools for Godot 4.3+ GPU particle emitters (`GpuParticles2D`, `GpuParticles3D`). Mirrors the P12.1 tilemap / P12.2 navigation packs' folder layout, registration shape, gate policy, and group assignment.

## Group + tool roster

Group id: **`particles`** (default-on: false — activate via `godot_open_mcp_manage_tools`).

| Tool | Mutating | Gate | Purpose |
|---|---|---|---|
| `godot_open_mcp_particles_defaults` | no | off | Recommended starter scalars for a 2D/3D emitter (pure helper, no scene) |
| `godot_open_mcp_particles_create` | yes | enforce | Create a `GpuParticles2D` or `GpuParticles3D` node (+ optional initial scalars + process material) |
| `godot_open_mcp_particles_configure` | yes | enforce | Patch clamped scalar properties on an emitter |
| `godot_open_mcp_particles_set_emitting` | yes | enforce | Start/stop emission; optional `restart` clears existing particles |
| `godot_open_mcp_particles_get` | no | off | Read scalar config (+ type + dimension + process material path) |

## Scope

- `GpuParticles2D` and `GpuParticles3D` only — **not** `CPUParticles2D/3D`, **not** `GPUParticlesAttractor*` / `GPUParticlesCollision*`.
- Scalar / enum configure surface only in v1 — **not** full `ParticleProcessMaterial` graph editing. Material authoring is out of scope; `create` / `configure` accept an optional `process_material_path` pointing at a pre-existing `ParticleProcessMaterial` (loaded via `ResourceLoader.Load<Material>` — Godot 4.3 types `process_material` as `Material`, so `ShaderMaterial` is also accepted; `ParticleProcessMaterial` is the typical choice).
- No particle-instance arrays in `get` v1 — only the scalar config snapshot.
- No third-party particle addons.
- No separate NuGet/extension package — embedded in the main bridge addon.

## Scalar allow-list

`configure` (and the initial `properties` object on `create`) accept exactly these scalars. Unknown keys are rejected by the schema (`additionalProperties:false`); the body parser only extracts the fixed known set. Clamping is centralized in `ParticlesPropertyClamp` (unit-tested without the editor).

| Property (schema key) | Godot property | Type | Valid range | Clamp |
|---|---|---|---|---|
| `amount` | `Amount` | int | ≥ 1 | `[1, 100000]` |
| `lifetime` | `Lifetime` | float | > 0 | `≥ 0.0001` |
| `one_shot` | `OneShot` | bool | — | pass-through |
| `preprocess` | `Preprocess` | float | ≥ 0 | `≥ 0` |
| `speed_scale` | `SpeedScale` | float | ≥ 0 | `≥ 0` |
| `explosiveness` | `Explosiveness` | float | 0..1 | `[0, 1]` |
| `randomness` | `Randomness` | float | 0..1 | `[0, 1]` |
| `fixed_fps` | `FixedFps` | int | ≥ 0 | `≥ 0` (0 = render frame rate) |
| `interpolate` | `Interpolate` | bool | — | pass-through |
| `fract_delta` | `FractDelta` | bool | — | pass-through |
| `local_coords` | `LocalCoords` | bool | — | pass-through |

`emitting` is intentionally **not** in the allow-list — use the dedicated `set_emitting` tool instead (it pairs the toggle with an optional `restart`). `visibility_aabb` / `visibility_rect` are deferred (v1 skips the AABB/rect surface).

## Dimension selection

`create` and `defaults` take a `dimension` arg (`"2d"` | `"3d"`). The body parser maps it to a `ParticlesDimension` enum; an absent or unrecognized token becomes `invalid_parameter` at the handler. `configure` / `set_emitting` / `get` infer the dimension from the resolved node's class.

## Godot API notes

- `new GpuParticles2D()` / `new GpuParticles3D()` — concrete engine nodes present in every 4.3+ build; no `ClassDB.ClassExists` guard needed. (C# PascalCase is `GpuParticles2D` / `GpuParticles3D`; the engine/GDScript identifier is `GPUParticles2D` / `GPUParticles3D`.)
- Scalar properties (`Amount`, `Lifetime`, `OneShot`, `Preprocess`, `SpeedScale`, `Explosiveness`, `Randomness`, `FixedFps`, `Interpolate`, `FractDelta`, `LocalCoords`, `Emitting`) are shared by both classes.
- `Restart()` clears existing particles and restarts the emission cycle — called by `set_emitting` when `restart:true` (before flipping `Emitting`).
- Process material: `GpuParticles2D.ProcessMaterial` / `GpuParticles3D.ProcessMaterial` (Godot 4.3 types this as `Material`; `ParticleProcessMaterial` is the typical concrete choice, `ShaderMaterial` is also accepted). Loaded via typed `ResourceLoader.Load<Material>` — a type mismatch returns `resource_load_failed`.
- Owner assignment to the edited scene root makes the new node persist in the `.tscn` on save (Godot-specific; Unity has no equivalent).

## Gate contract

The three mutators (`create` / `configure` / `set_emitting`) register `defaultGate: "enforce"` and validate `paths_hint` at the handler level (the dispatch layer also rejects an empty hint when the effective gate is not `off`; the handler-level guard ALSO fires when an agent overrides with `gate: "off"`). `paths_hint` is scoped to the edited scene resource path (`res://…tscn`). The read-only `defaults` and `get` have no gate surface.

## Error codes

Reuses the shared Phase 12 vocabulary (`no_edited_scene`, `node_not_found`, `wrong_node_type`, `missing_parameter`, `invalid_parameter`, `paths_hint_required`, `resource_not_found`, `resource_load_failed`, `parent_not_found`, `create_failed`). No pack-specific codes in v1 — `invalid_parameter` covers bad dimension tokens and malformed position strings.
