# Godot Open MCP Demo

Minimal Godot 4.3+ C# integration fixture for **Godot Open MCP**. This is not
a product sample — it is a deterministic integration fixture used by
`scripts/prepare-demo-addon.mjs`, `scripts/demo-smoke.mjs`, and the
`demo-build` / `demo-smoke` CI jobs to prove that a real Godot project can
materialize the addon, build the combined assembly, expose the offline MCP
tools, and reproduce the verify issue-code fixtures.

For the ownership rules that govern this directory, see
[`AGENTS.md`](AGENTS.md). For the per-fixture expected outcomes, see
[`Fixtures/README.md`](Fixtures/README.md).

## Requirements

- **Godot 4.3 or newer, the .NET / C# (mono) build** — verify with
  **Help → About Godot** (must list ".NET").
- **.NET 8 SDK** — verify with `dotnet --version`.
- **Node.js 18+** — only needed for the prepare/smoke scripts and the MCP
  server. Verify with `node --version`.
- A checkout of **Godot Open MCP** (this repository) — the prepare script
  materializes the addon from `packages/bridge/` + `packages/verify/`.

## Local build / open / smoke

Run from the repository root.

### 1. Materialize the addon (idempotent)

```bash
node scripts/prepare-demo-addon.mjs
```

Copies `packages/bridge/` (+ the bundled verify source) into
`demo/addons/godot_open_mcp/` via the CLI installer and enables the plugin in
`demo/project.godot`. Idempotent — a re-run that finds the addon present and
the plugin already enabled reports `changed: false` and writes nothing.

### 2. Build the demo assembly

```bash
dotnet restore demo/GodotOpenMcp.Demo.csproj
```

`dotnet restore` proves the `Godot.NET.Sdk/4.3.0` pin + `net8.0` target
resolve cleanly against NuGet. The actual C# *compile* of the combined
assembly (demo scripts + materialized bridge + verify source) happens
INSIDE the Godot editor via its bundled GodotSharp / GodotSharpEditor. A raw
`dotnet build` against NuGet GodotSharp 4.3.0 is **not** the compile host —
the bridge C# uses APIs that exist in the editor's in-tree bindings but not
in the published C# bindings package, so `dotnet build` against NuGet fails
by design. The editor's first-open compile is what produces
`GodotOpenMcp.Demo.dll`; doing it at least once before the first launch
avoids the `Unable to load addon script … Disabling the addon` failure on a
fresh project (see [the manual setup guide](../docs/manual-setup.md)).

### 3. Open in the Godot mono editor

Open `demo/` as a Godot project. The plugin is already enabled by step 1;
once the editor finishes loading, the bridge HTTP listener starts on the
project's deterministic port (`20000 + sha256(projectPath) % 10000`,
overridable via `GODOT_OPEN_MCP_BRIDGE_PORT`).

### 4. Run the offline smoke

```bash
# Build the MCP server first (one time):
cd mcp-server && npm ci && npm run build && cd ..

node scripts/demo-smoke.mjs --project demo
```

Spawns the stdio MCP server pointed at `demo/`, drives the MCP wire flow, and
asserts the offline readers return deterministic fixture content. Exits `0` on
pass, `1` on failure. No Godot editor required — the offline path is the
deterministic CI smoke.

### 5. Verify fixture shapes (no editor, no MCP server)

```bash
node scripts/prepare-demo-addon.mjs --check
```

Asserts the materialized addon tree contains `plugin.cfg`, `Editor/**`,
`Runtime/**`, `Verify/**`, that a second materialize is a no-op, and that the
broken `.tscn`/`.tres`/`.import` fixtures exhibit the exact patterns the live
verify rules flag (`broken_scene_reference`, `missing_script`,
`orphan_import`, `duplicate_uid`). Runs in milliseconds.

## Manual verification checklist

The offline smoke covers the deterministic contracts. The live-bridge path
(`ping` round-trip against a running editor, gated mutation delta, exact
issue-code emission per fixture) requires a real Godot editor and is not
exercised in headless CI. Run it manually:

1. Fresh clone; no `demo/addons/godot_open_mcp/` tree exists.
2. Run `node scripts/prepare-demo-addon.mjs` — first run reports
   `changed: true`; a second run reports `changed: false` (no-op).
3. `dotnet restore demo/GodotOpenMcp.Demo.csproj` succeeds (NuGet SDK resolution).
4. Open `demo/` in Godot 4.3+ mono. The editor's first-open compile produces
   `GodotOpenMcp.Demo.dll` (the combined assembly). No
   `Unable to load addon script` / assembly-load / disabled-plugin error.
5. The plugin stays enabled; the bridge window (or
   `node cli/dist/index.js status demo --json`) reports `running`.
6. Start the local MCP server with
   `GODOT_PROJECT_PATH=<absolute path to demo>`:
   ```bash
   node mcp-server/dist/index.js
   ```
7. From an MCP client (or a direct `tools/call`), call:
   - `godot_open_mcp_ping` — returns `connected:true` with the demo project
     path and Godot version.
   - `godot_open_mcp_capabilities` — lists the `core` and `gate-and-verify`
     groups, the three verify rule ids (`broken_references`,
     `missing_scripts`, `import_health`), and the available fix ids.
   - `godot_open_mcp_manage_tools activate typed-editor` — flips the
     typed-editor group visibility; `notifications/tools/list_changed` fires.
   - `godot_open_mcp_scene_get_data` with `path:"res://Main.tscn"` — returns
     the `Main` root + known children (`Player2D`, `Player3D`,
     `ValidFixtureChild`).
8. Scan each broken fixture via `godot_open_mcp_validate_edit` (or the gate
   flow) and compare the exact issue code against
   [`Fixtures/README.md`](Fixtures/README.md):
   - `res://Fixtures/BrokenReference/BrokenReference.tscn` →
     `broken_references|broken_scene_reference`
   - `res://Fixtures/MissingScript/MissingScript.tscn` →
     `missing_scripts|missing_script`
   - `res://Fixtures/ImportHealth/OrphanTexture.png.import` →
     `import_health|orphan_import`
   - `res://Fixtures/ImportHealth/DupA.txt.import` (+ `DupB.txt.import`) →
     `import_health|duplicate_uid`
9. Copy a healthy scene into `res://SmokeScratch/`, run one gated mutation
   (e.g. `node_create` with `gate:"enforce"` and a complete `paths_hint`),
   inspect the gate delta, and assert `newErrors == 0`. Delete the scratch
   copy at the filesystem level after the editor exits (or via a gated delete
   included in the same smoke).
10. Stop the editor and call `godot_open_mcp_bridge_status` — should report
    `stopped` (no live Godot process). Then call
    `godot_open_mcp_read_compile_errors` — should read the project's Godot log
    from disk and report no C#/GDScript/plugin-load diagnostics for a clean
    close.
11. `git status` shows no generated files (`demo/addons/`, `demo/.godot/`,
    `demo/bin/`, `demo/obj/`, `demo/.godot-open-mcp/`, `demo/SmokeScratch/`,
    `*.sln`, `*.csproj.user`).

## What is NOT here

- No runtime MCP / cloud transport harness (v1 non-goal — see
  `specs/porting-map.md`).
- No GPU screenshot assertion in headless CI (no reliable render device).
- No release-zip download path for the addon — the prepare script always
  materializes from `packages/bridge/` in this checkout.
- No new typed tools or verify rules — this fixture exercises what already
  ships.
