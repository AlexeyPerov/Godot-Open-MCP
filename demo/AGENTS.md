# Demo project rules

## Scope

Rules for `demo/` — the default Godot C# integration fixture for Godot Open
MCP. Used by `scripts/prepare-demo-addon.mjs`, `scripts/demo-smoke.mjs`, and
the `demo-build` / `demo-smoke` CI jobs to prove that a real Godot project can
materialize the addon, build the combined assembly, expose the offline MCP
tools, and reproduce the verify issue-code fixtures. Inherits root
`AGENTS.md`; deeper rules win on overlap.

User quick start: [`README.md`](README.md). Fixture index:
[`Fixtures/README.md`](Fixtures/README.md).

## Fixture role

1. **Demo is an integration fixture, not a product sample.** Do not ship
   product-specific game content, branding, tutorial levels, downloaded art,
   runtime MCP, or a game loop here. Every file pins a deterministic contract
   the smoke or the manual checklist asserts.
2. **No generated editor/build/addon output is committed.** `addons/`,
   `.godot/`, `bin/`, `obj/`, `.mono/`/`mono/`, `.godot-open-mcp/` (bridge
   settings scratch), `SmokeScratch/`, `*.sln`, and `*.csproj.user` are
   gitignored (see root `.gitignore`).
3. **Addon source is materialized through the canonical script.** Run
   `node scripts/prepare-demo-addon.mjs` (or `--check`) — it wraps the CLI
   installer so there is one canonical path. Never vendor a stale addon tree
   by hand; never copy `packages/bridge/**` into `demo/addons/` directly.
4. **Fixture paths/names are stable public test contracts.** Renaming or
   moving a file under `Fixtures/`, `Resources/`, `Scenes/`, or `Scripts/`
   breaks the smoke and the manual checklist — update both in the same task.
5. **Intentional broken fixtures are indexed with expected issue codes.**
   Every broken fixture has a row in [`Fixtures/README.md`](Fixtures/README.md)
   with the exact `ruleId|issueCode` it must produce. The issue code is the
   stable API surface, not the fixture text.
6. **CI mutations use scratch copies only.** No smoke step mutates a tracked
   fixture in place; everything mutating lands under `res://SmokeScratch/`
   (gitignored) and is cleaned up on success AND failure.
7. **New tool families add the smallest representative fixture.** When a new
   typed-tool family or verify rule lands, add one minimal fixture here and
   update the catalog in the same task. Do not add empty placeholder assets.
8. **`project.godot` and `.csproj` engine/assembly names stay synchronized.**
   `[dotnet] project/assembly_name` in `project.godot` must match
   `<AssemblyName>` in `GodotOpenMcp.Demo.csproj`. The SDK pin
   (`Godot.NET.Sdk/4.3.0`) must match the addon's engine floor.
9. **Do not add runtime MCP/autoload behavior.** This fixture exercises the
   editor addon + the offline MCP path. No `[autoload]` entries, no runtime
   bridge client, no SignalR/cloud transport — those are v1 non-goals (see
   `specs/porting-map.md` and ADR-004).

## Healthy default

The default `run/main_scene` (`res://Main.tscn`) must load with no warnings or
errors unrelated to the test being run. Intentionally broken fixtures live
under `res://Fixtures/` and are never instantiated or autoloaded.

## Hygiene

- See [`Fixtures/README.md`](Fixtures/README.md) for the per-fixture expected
  outcomes and the live-vs-offline split.
- See [`README.md`](README.md) for local build / open / smoke steps and the
  manual verification checklist.
