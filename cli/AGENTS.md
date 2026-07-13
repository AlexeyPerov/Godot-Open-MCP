# CLI rules

## Scope

Rules for `cli/` — the `godot-open-mcp-cli` package. Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- TypeScript ESM project (`"type": "module"`). Source under `src/`, built to `dist/`.
- Node 18+. Zero runtime dependencies — do not add Commander, chalk, or any other dep without strong justification. The argv parser is hand-rolled (adapted from Unity Open MCP's `mcp-server/src/cli/args.ts`).
- Tests use `node --test` with `tsc -p tsconfig.test.json` build (matches `mcp-server/`).

## Package boundary

- The CLI is **separate** from `mcp-server/`. It never starts a stdio MCP server. Every invocation is handled (help, version, or an error) and the process exits with the dispatcher's exit code.
- `runCli` always returns `handled: true`. There is no Unity-style `handled: false` → stdio fallthrough.

## Command surface

- Commands register in `KNOWN_COMMANDS` (`src/args.ts`) as their plans land:
  - `install-plugin` (P6.2)
  - `setup-mcp` (P6.3)
  - `open` + `wait-for-ready` (P6.4)
  - `status` + `configure` (P6.5)
- Shipped help text only advertises implemented commands. Unimplemented commands listed in help carry a `(coming soon)` tag.
- Each command is a plain async function returning `CliCommandResult` (`exitCode` / `json` / `human` / `errorLabel`). The dispatcher owns stdout/stderr/exit-code; commands stay pure and unit-testable.

## Shared flags

`--json`, `--project`/`-P`, `--port`/`-p`, `--timeout-ms`, `--interval-ms`, `--help`/`-h`, `--version`/`-V`. Env: `GODOT_PROJECT_PATH`, `GODOT_OPEN_MCP_BRIDGE_PORT` (mirror `mcp-server/src/instance-discovery.ts`).

## Versioning

`cli/package.json` is synced from `version.json` by `scripts/sync-version.mjs`. The CLI reads its version at runtime via `readPackageVersion()` (`src/package-version.ts`).

## Verification

- `npm run typecheck` and `npm test` after changes.
- Argv parsing and dispatcher behavior are unit-tested without spawning Godot.
