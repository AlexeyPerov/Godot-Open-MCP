# `godot-open-mcp-cli`

Command-line installer and operator tool for **Godot Open MCP** — install the
editor addon, write a stdio MCP client config, build and launch the editor, and
probe bridge health. The CLI is the automated alternative to
[the manual setup guide](../docs/manual-setup.md): every command below does
what that guide walks through by hand, idempotently and with structured output.

> The CLI never starts a stdio MCP server itself. It is a separate package from
> `mcp-server/`. Every invocation is handled (a command, help, or an error) and
> the process exits — there is no stdio fallthrough.

## Requirements

- **Node.js 18 or newer.** The CLI is plain TypeScript ESM with zero runtime
  dependencies — install nothing beyond Node itself.
- **.NET 8 SDK** and the **Godot 4.3+ mono** editor for any command that builds
  or launches the editor (`open`, manual `dotnet build`).

## Install

The CLI is published as `godot-open-mcp-cli`. Run ad-hoc with `npx`, or install
globally:

```bash
npm install -g godot-open-mcp-cli
godot-open-mcp-cli --version
```

Contributors can build straight from the checkout — the CLI has no runtime
dependencies, so the build is just TypeScript:

```bash
cd cli
npm ci
npm run build      # → dist/
node dist/index.js --help
```

The CLI reads its version at runtime via `readPackageVersion()` so `--version`
never lies after a version bump. Version sync is wired through
`scripts/sync-version.mjs` alongside the MCP server and the addon.

## Quick start

The one-command developer loop from "project on disk" to "bridge accepting
tools", run from a checkout:

```bash
cd cli
npm ci && npm run build

node dist/index.js install-plugin \
  --source /absolute/path/to/Godot-Open-MCP/packages/bridge \
  /absolute/path/to/MyGame

node dist/index.js setup-mcp cursor /absolute/path/to/MyGame --use-local
node dist/index.js open        /absolute/path/to/MyGame
node dist/index.js wait-for-ready /absolute/path/to/MyGame
```

## Commands

| Command | What it does |
|---|---|
| `install-plugin [path] [--source <dir>] [--json]` | Materialize the bridge addon + bundled verify source into `addons/godot_open_mcp/` and enable the plugin in `project.godot`. Idempotent. |
| `setup-mcp <agent-id> [path] [--list] [--use-local] [--config-path <file>] [--json]` | Write a **stdio** MCP client config for the named agent. Idempotent. |
| `open [path] [--editor-path <bin>] [--no-build] [--build-configuration <cfg>] [--wait] [--json]` | Optional pre-open `dotnet build`, resolve the Godot binary, launch `--editor --path <project>` detached. |
| `wait-for-ready [path] [--timeout-ms <n>] [--json]` | Poll the bridge `/ping` until it answers, with structured status tokens. |
| `ping [path] [--json]` | One-shot bridge `/ping` probe. |
| `status [path] [--json]` | Detect a running Godot editor for the project, probe bridge health, fold the signals into a coarse `status` token aligned with the MCP `godot_open_mcp_bridge_status` tool. |
| `configure [path] [--list] [--get <key>] [--set key=value]... [--json]` | Read / write the bridge's project-local settings (`authMode`, `bindAddress`) at `<project>/.godot-open-mcp/settings.json`. |
| `verify [path] [--fail-on error\|warn\|none] [--json]` | Offline scan for broken references + missing scripts; exit `0` clean / `1` issues at/above `--fail-on`. No editor needed. |
| `baseline create\|update [path] [--baseline-path <file>] [--platform-profile <p>] [--json]` | Offline scan → write a schema-v1 regression baseline JSON (default `CI/godot-open-mcp-baseline.json`). No editor needed. |
| `regression check [path] [--baseline-path <file>] [--threshold <n>] [--per-category-threshold <ruleId=N>]... [--json]` | Compare the current scan against the baseline; exit `0` ok / `1` regression / `2` baseline missing / `3` baseline invalid. No editor needed. |

Every command accepts `--json` for machine-readable output and exits `0` on
success (including idempotent `changed: false` results), `1` on errors, and `3`
on timeout. Failure payloads carry a stable `error.code` so CI can branch
without parsing prose. `regression check` adds two regression-specific codes:
`2` (baseline missing) and `3` (baseline invalid) — see the
[CI templates](../docs/ci/README.md) for the full contract.

## `install-plugin`

The CLI installer is the supported way to land the addon in a project. It
copies `packages/bridge` (plus the bundled verify source — see below) into
`<project>/addons/godot_open_mcp/` and toggles the plugin into
`project.godot`. It is **idempotent**: a re-run that finds the addon present and
the plugin already enabled reports `changed: false` and writes nothing.

```bash
# From a checkout (canonical contributor path):
node dist/index.js install-plugin \
  --source /absolute/path/to/Godot-Open-MCP/packages/bridge \
  /absolute/path/to/MyGame

# Or pointing at any directory that IS or CONTAINS addons/godot_open_mcp/:
node dist/index.js install-plugin --source /path/to/addon-bundle /path/to/MyGame
```

The installer:

1. **Resolves the source.** `--source <dir>` wins; otherwise it falls back to
   the monorepo default (`packages/bridge`) when running from a checkout. There
   is no release-zip download path in v1.
2. **Copies the addon tree** into `addons/godot_open_mcp/` — `plugin.cfg`,
   `Editor/**`, `Runtime/**`. The bridge's `Tests/`, `obj/`, `bin/`, `.godot/`
   subtrees and the repo-dev files (`.gitkeep`, `AGENTS.md`) are excluded.
3. **Bundles the verify source.** The bridge editor code references
   `GodotOpenMcp.Verify.*`, so the installer copies the sibling verify
   package's `Editor/**` into `addons/godot_open_mcp/Verify/`. One plugin, one
   C# assembly. If the verify source cannot be located, the install still
   succeeds but emits a warning — the bridge's verify-coupled code paths will
   not compile until verify source is bundled.
4. **Enables the plugin** in `project.godot` via a pure text transform that
   preserves unrelated sections, comments, and ordering.

Materialization is staged in a temp sibling and swapped atomically, so a
mid-copy failure leaves the existing addon untouched.

The CLI does **not** build the C# project — `open` does that, or run
`dotnet build <project>.csproj -c Debug` by hand. See the
[manual setup guide](../docs/manual-setup.md) for why the pre-open build
matters.

## `setup-mcp`

Writes a **stdio** MCP client config so an AI client (Cursor, Claude, VS Code
Copilot, …) can spawn the local `godot-open-mcp` server. Transport is
stdio-only — the writer never emits a `url` / `type:"http"` entry.

```bash
node dist/index.js setup-mcp --list                       # list every supported agent id
node dist/index.js setup-mcp cursor /absolute/path/MyGame # write Cursor's .cursor/mcp.json
node dist/index.js setup-mcp cursor /absolute/path/MyGame --use-local   # node + monorepo build (contributors)
```

The default spawn descriptor is `npx -y godot-open-mcp@<version>` (version
pinned from the CLI's own package version); `--use-local` switches to
`node <monorepo>/mcp-server/dist/index.js` for contributors and CI. `GODOT_PROJECT_PATH`
is always absolute — relative inputs are rejected up-front.

The roster covers `cursor`, `claude-code`, `claude-desktop`, `vscode-copilot`,
`vs-copilot`, `opencode`, `gemini`, `cline`, `kilo-code`, `github-copilot-cli`,
and a generic `custom` target. Each agent knows its config-file path and its
envelope shape:

- **`mcpServers` clients** (Cursor, Claude, Cline, Gemini, Kilo Code, GitHub
  Copilot CLI, custom): bare `{ command, args, env }`.
- **VS Code / Visual Studio Copilot**: `servers` + `type: "stdio"`.
- **OpenCode**: `mcp` + `command` (array) + `environment` + `type: "local"`.

The writer merges the `godot-open-mcp` entry into the existing config,
preserving sibling servers and unrelated top-level keys, and strips any foreign
HTTP/transport keys (`url`, `headers`, `type:"http"`, …) from our entry so a
prior Godot-MCP HTTP config cannot linger.

## `open` + `wait-for-ready` + `ping` — the developer launch loop

`open` launches the Godot editor on a project. Flow: validate `project.godot`
→ optional pre-open `dotnet build` (skipped for GDScript-only projects or with
`--no-build`; a failed build aborts before launch so the editor never hits the
disable-addon failure) → resolve the editor binary → spawn
`<godot> --editor --path <project>` detached + unref'd so the CLI exits
immediately.

`open` resolves the Godot binary in this order: `--editor-path`, the `GODOT` /
`GODOT_EDITOR` (and `GODOT_BIN` / `GODOT4_BIN`) env vars, `PATH`, then per-OS
common install roots (`/Applications`, `/opt`, Program Files, Downloads, …)
including version-stamped release names, preferring the mono build and the
newest version.

`--wait` chains into `wait-for-ready` after a successful launch.
`wait-for-ready` polls the bridge `/ping` until it answers and returns one of
the structured status tokens: `ready`, `compiling`, `offline`, `dead_bridge`,
`timeout`.

## `status`

Folds the addon presence, plugin-enabled state, instance-lock classification,
resolved port, and a single `/ping` probe into a coarse `status` token aligned
with the MCP `godot_open_mcp_bridge_status` tool: `running`, `compiling`,
`stopped`, `unreachable`, `dead_bridge`. The CLI has no MCP client in process —
it hits the bridge endpoint directly with the discovered bearer token
attached. Use this for pre-commit checks and CI smoke tests.

## `configure`

Reads or writes the bridge's project-local settings at
`<project>/.godot-open-mcp/settings.json`:

```bash
node dist/index.js configure /absolute/path/MyGame --list
node dist/index.js configure /absolute/path/MyGame --get authMode
node dist/index.js configure /absolute/path/MyGame --set authMode=required --set bindAddress=0.0.0.0
```

The CLI only writes keys the bridge reads (`authMode`, `bindAddress`) and
validates them against the bridge's valid-value sets, so it never writes
garbage the bridge would fail-closed on. The bridge refuses a remote bind
(`0.0.0.0`) unless `authMode` is `required` — the CLI surfaces the same
invariant. Tool enable/disable is **not** here; that is the MCP
`godot_open_mcp_manage_tools` tool.

## `verify` + `baseline` + `regression check` — the CI gate

These three commands wrap the **offline disk scanner** — they parse `.tscn` /
`.tres` files directly and need **no Godot editor and no running bridge**. Only
Node.js is required, which keeps CI fast and license-free. Ready-to-use
[GitHub Actions and GitLab CI templates](../docs/ci/README.md) wire them into a
verify-on-PR + regression-on-main pipeline.

```bash
# Verify — scan now, fail on errors (default --fail-on error).
node dist/index.js verify /absolute/path/MyGame --fail-on error --json

# Baseline — snapshot the current issue set as the regression reference.
node dist/index.js baseline create /absolute/path/MyGame --json
# baseline update is an alias (overwrite the baseline after a clean main).

# Regression — compare the current scan against the committed baseline.
node dist/index.js regression check /absolute/path/MyGame \
  --baseline-path CI/godot-open-mcp-baseline.json \
  --threshold 0 --json
```

The offline scanner covers **`broken_references`** (an `[ext_resource]` whose
`path=` and `uid=` both fail to resolve) and **`missing_scripts`** (a node's
`script = ExtResource(...)` pointing at nothing). The richer verify rules
(scene structure, materials/shaders, script audit, …) run through the **live**
`validate_edit` surface inside the editor and are CI-excluded from the offline
baseline; the baseline records them in `ciExcludedRules` so "absent offline" is
never mistaken for "clean". See the [CI README](../docs/ci/README.md) for
threshold semantics and the full exit-code contract.

## Library API

Every command is a thin wrapper over a library-safe function in `src/lib/`
that returns a `{ kind: "success" | "failure" }` union and never throws past
the public boundary. Embedders (a future Hub app, CI scripts) can import the
library directly:

```ts
import { installPlugin, setupMcp, assembleStatus } from "godot-open-mcp-cli";
```

Each function performs only the I/O its name implies, returns a structured
result, and threads warnings through the union so callers can decide how to
surface them.

## Development

```bash
cd cli
npm ci
npm run build        # tsc → dist/
npm run typecheck
npm test             # node --test over the dist-test build
```

The argv parser is hand-rolled (zero deps). Tests run on `node --test` with a
separate `tsconfig.test.json` build — same pattern as `mcp-server/`. See
[`AGENTS.md`](AGENTS.md) for the package boundary and the rules that govern
adding commands.
