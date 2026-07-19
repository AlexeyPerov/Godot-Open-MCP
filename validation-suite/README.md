# Validation Suite

Standalone desktop app that guides manual validation as repeatable scenario runs against an engine MCP toolkit. This build targets **Godot Open MCP** against a Godot project (e.g. the bundled `demo/` project).

The app replaces long, copy-heavy manual checklists with a guided operator workflow: filesystem + MCP/CLI actions automate setup, MCP-client verification stays human-driven (copy a prompt → run in the agent → paste the result), and progress + evidence persist as project-local data.

This app is a **standalone Tauri app**. It invokes the engine via the MCP CLI (`godot-open-mcp run-tool`) as a subprocess, so it stays decoupled from the MCP server's internals.

Its architecture is adapted from [Unity Open MCP](https://github.com/AlexeyPerov/Unity-Open-MCP)'s validation suite; the engine-specific parts (profile, project markers, companion rules, CLI binary, tool prefix) are Godot's.

## Stack

Tauri 2 + SvelteKit + Svelte 5. Engine-neutral orchestration is framework-free TypeScript in `packages/core/`.

## Repository layout

```
validation-suite/
  src/                         SvelteKit UI
  src/lib/state/               Svelte 5 runes app state (+ per-step action log)
  src/lib/services/            Tauri IPC wrappers (+ action backend adapter)
  src/lib/components/          UI components (project bar, test nav, step renderer, action log)
  src-tauri/                   Rust shell (sandboxed fs ops, MCP CLI runner, manifests, persistence)
  packages/core/               scenario DTOs, loader, state, action runner, patch transform (engine-neutral TS)
  engine-profiles/
    godot.json                 Godot profile (paths, CLI, companions, markers)
  scenarios/
    godot/sample/*.json        shipped sample scenario definitions
```

## Running

```bash
cd validation-suite
npm install
npm run tauri dev
```

Then use **Open project…** to select a Godot project folder (e.g. `../demo`). The suite validates the folder against the engine profile's project markers (a `project.godot` file) and scopes all state + fixtures under that project.

## Tests

```bash
# Engine-neutral core (node:test, TS)
npm run test:core

# UI type-check
npm run check

# Rust backend
cd src-tauri && cargo test
```

## Running an MCP tool from the command line

The suite's `mcp_tool` actions shell out to the MCP server's CLI. You can run the same command directly:

```bash
# with the Godot editor open on the project and the bridge running
godot-open-mcp run-tool godot_open_mcp_ping --project "$PWD/demo" --json
```

`run-tool` resolves the bridge for `--project`, invokes the same tool router an MCP client would, prints the JSON result (`{ command, tool, isError, result }`) to stdout, and exits non-zero on a fatal/transport error. See `mcp-server/README.md` for the full CLI surface.

## Setup actions and reset

Scenario `setup` steps run **declarative actions** through an engine-neutral runner (`packages/core/src/actions.ts`) that delegates to a Rust backend. Every fs action is **sandboxed to the project root** — traversal outside the project is rejected.

| Action | Executor | Behavior |
|---|---|---|
| `fs_copy` | Rust | Copies a file or directory tree; auto-tracks a companion sidecar (`.import` / `.uid`) when the source companion exists. |
| `fs_patch` | Rust | Applies the pinned patch-op vocabulary (`replace_line_contains`, `insert_after_line_contains`, `insert_before_line_contains`, `trim_trailing_whitespace`); snapshots the pre-patch file for reset. |
| `fs_delete` | Rust | Deletes manifest-listed paths (used by reset; no heuristic deletes). |
| `mcp_tool` | Rust subprocess | Runs an MCP tool via `godot-open-mcp run-tool --json`; surfaces `isError` and the tool body in the action log. |
| `manual` | UI gate | Records an info log; the operator confirms the action. |

Patch ops are validated at scenario-load time, so an unknown op never reaches the executor. Each mutating step records a **manifest** (created/modified artifacts + snapshots); the state file keeps only the blob id per step.

**Reset** walks a step's manifest in reverse order: modified files restore from their snapshot, created artifacts are deleted. Missing/incomplete manifest metadata warns and continues (best-effort) rather than crashing. Run setup from a step's **Run setup** button; re-run or reset with **Re-run setup** / the test-level **Reset test**.

## Where data lives

Per active project + Godot profile:

- **Fixtures:** `_ValidationSuite/<test-id>/` — staged and reverted per scenario.
- **State file:** `.godot-open-mcp/ValidationSuite/.state.json` — atomic read/write; survives app restart.
- **Actuals:** `.godot-open-mcp/ValidationSuite/actuals/`.
- **Exports:** `.godot-open-mcp/ValidationSuite/exports/` — run-summary markdown.

State lives under `.godot-open-mcp/` (not `.godot/`, which the editor may wipe). State is **not migrated** between versions: a version mismatch produces a warning with reset guidance.

### Demo project hygiene

The suite stages disposable fixtures and writes local operator state inside the target project. The bundled `demo/` project's ignore rules exclude both so they never get committed:

- `demo/_ValidationSuite/` — staged and reverted per scenario by the suite.
- `demo/.godot-open-mcp/ValidationSuite/` — operator state, manifests, actuals, exports.

Companion sidecars follow Godot conventions: `.import` for imported assets (`.png`, `.jpg`, `.svg`, `.wav`, `.ogg`) and `.uid` for `.tres` / `.tscn` / `.gd` / `.cs` resources. `.uid` files exist on Godot 4.4+; on 4.3 they may be absent, so companion copy is best-effort (copied only when the source companion exists).

## Requirement tiers and optional scenarios

Every scenario declares a `requirementLevel`:

- **required-core** — the closeout gate. Use the **Required · core** filter to isolate these.
- **required-extended** — a recommended confidence pass.
- **optional** — runnable; usually shows automated coverage. Collapsed into a default-closed "Optional" subsection within each milestone group, with an **Auto** badge when `automatedCoverage` references exist.

Optional scenarios carry an `automatedCoverage` array naming the tests that already cover the behavior; they stay runnable so an operator can do a live confidence pass even when automation exists.

## Export (run summary)

Use **Export…** in the top bar to produce a sign-off markdown summary of the current run:

- **Copy summary to clipboard** — markdown ready to paste into a checklist or changelog.
- **Save summary as file…** — writes a timestamped `.md` under `.godot-open-mcp/ValidationSuite/exports/`.

The summary includes the project path, engine profile id, timestamp, a requirement-tier breakdown, one status table per milestone (required grouped, optional folded under an "Optional" subheading), and the closeout-gate verdict (passes only when every `required-core` scenario is `done`). The builder is engine-neutral (`packages/core/src/export.ts`) and unit-tested.

## Status

This build ships the suite shell, the engine-neutral core (DTOs, loader, state, action runner, patch transform, export), the Godot engine profile, the MCP CLI subprocess runner, and sample smoke scenarios. Core scenario authoring for Godot tooling is ongoing.
