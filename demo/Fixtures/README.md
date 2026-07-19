# Demo fixtures

Indexed broken and edge fixtures for the Godot Open MCP demo project. Each
fixture is the smallest reproducible case for a verify rule or a typed-tool
behavior, and carries a deterministic expected outcome so `scripts/demo-smoke.mjs`
and the manual checklist can assert it byte-stable across runs.

> **Role.** These fixtures are intentional integration-test material. They are
> NOT instantiated or autoloaded by the default startup (`res://Main.tscn` is
> the only healthy startup scene); they live under `res://Fixtures/` so the
> editor's default import pass and a normal play run never trip on them.

## Catalog

| Fixture id | Path | Expected tool/rule | Expected outcome | Safe to mutate |
|---|---|---|---|---|
| `healthy-main` | `res://Main.tscn` | `scene_get_data` (offline) | stable `Main` root + `Player2D`, `Player3D`, `ValidFixtureChild` children | copy first |
| `healthy-2d` | `res://Scenes/Healthy2D.tscn` | `scene_get_data` (offline) | stable `Healthy2D` root + `Player2D` subtree | copy first |
| `healthy-3d` | `res://Scenes/Healthy3D.tscn` | `scene_get_data` (offline) | stable `Healthy3D` root + `MeshInstance3D` + `Camera3D` | copy first |
| `nested-fixture` | `res://Scenes/NestedFixture.tscn` | `scene_get_data` (offline) | nested `Outer/Inner/Deep` hierarchy | copy first |
| `healthy-resource` | `res://Resources/DemoData.tres` | resource read/modify | `title`, `count`, `ratio`, `enabled`, `tint`, `position`, `tags` | copy first |
| `valid-gdscript` | `res://Scripts/ValidFixture.gd` | script validate | no diagnostics | no |
| `valid-csharp` | `res://Scripts/DemoRoot.cs` | `dotnet build` | clean compile | no |
| `invalid-gdscript` | `res://Fixtures/ScriptValidation/invalid.gd.fixture` | script validate | deterministic parse error (after copy-to-`.gd`) | no |
| `broken-reference` | `res://Fixtures/BrokenReference/BrokenReference.tscn` | `broken_references` rule | `broken_scene_reference` (Error) | no |
| `missing-script` | `res://Fixtures/MissingScript/MissingScript.tscn` | `missing_scripts` rule | `missing_script` (Error) | no |
| `orphan-import` | `res://Fixtures/ImportHealth/OrphanTexture.png.import` | `import_health` rule | `orphan_import` (Warning) | cleanup allowed |
| `duplicate-uid` | `res://Fixtures/ImportHealth/DupA.txt.import` + `DupB.txt.import` | `import_health` rule | `duplicate_uid` (Error) | no |

## Live vs offline

`validate_edit`, `scan_paths`, and the verify rules run on the **live bridge**
(`POST /tools/godot_open_mcp_validate_edit` etc.) — there is no offline
equivalent. The CI smoke (`scripts/demo-smoke.mjs`) cannot drive those routes
without a running Godot editor, so it asserts:

- the **offline readers** (`scene_get_data`, `filesystem_list`,
  `read_compile_errors`) return deterministic content for the healthy fixtures;
- a **static fixture-shape check** (`scripts/prepare-demo-addon.mjs --check`)
  parses the broken `.tscn`/`.tres`/`.import` files and confirms they exhibit
  the exact broken pattern the live rule would flag (a non-existent
  `[ext_resource]` path for the broken-reference fixture; a dangling script
  attachment for the missing-script fixture; a `source=` pointing at a missing
  file for the orphan; a shared `uid=` across the duplicate pair).

The exact issue-code mapping (`broken_scene_reference`, `missing_script`,
`orphan_import`, `duplicate_uid`) is then asserted **manually** by the
[`demo/README.md` manual checklist](../README.md#manual-verification-checklist)
against a real running editor, since headless CI cannot bind the bridge.

## Fixture hygiene

- Do not add empty placeholder assets — every fixture must pin a deterministic
  contract.
- Do not instantiate broken fixtures in `Main.tscn` or any scene reachable from
  `run/main_scene`.
- New tool families add the **smallest** representative fixture here and update
  the catalog table in the same task.
- See [`demo/AGENTS.md`](../AGENTS.md) for the ownership rules that govern
  this directory.
