# Agent rules — `hub/`

Scoped rules for the **Godot Open MCP Hub** (Tauri 2 + SvelteKit + Svelte 5).
Root `AGENTS.md` still applies (master-only branching, Unity-first porting, no
migrations, docs ownership, clean user-visible strings). This file narrows those
rules for the Hub subtree.

## What the Hub is

A desktop **GUI over the `godot-open-mcp-cli` contracts**. It does not reinvent
install / setup-mcp / open / wait-for-ready logic — the Rust `config` modules
mirror the CLI's algorithms so the Hub produces the **same outcomes** the CLI
does. When the CLI changes behavior, the Hub must follow.

- **AI Setup wizard** — install the addon, write a stdio MCP client config,
  launch the editor, wait for the bridge to report ready.
- **Maintainer panel** — run `mcp-server` npm scripts (build / test / publish
  dry-run / publish) + version sync, for the tooling monorepo only.

## Hard rules

- **stdio only (ADR-001).** Never write an MCP entry with `url`, `type:"http"`,
  headers, or any cloud host. The merge strips those keys from our server entry.
- **MCP server key is `godot-open-mcp`.** Tools remain `godot_open_mcp_*`.
- **Env vars:** `GODOT_PROJECT_PATH` (required, absolute) and optional
  `GODOT_OPEN_MCP_BRIDGE_PORT`. Never emit `UNITY_*` or Godot-MCP cloud vars.
- **Bridge port is a three-way contract.** `src-tauri/src/config/bridge_port.rs`
  must agree byte-for-byte with the C# `InstancePortResolver` and the TS
  `instance-discovery.ts`: normalize (`\`→`/`, trim trailing `/`, no lowercase)
  → SHA-256 hex → `20000 + (first 16 hex chars as u64) % 10000`. The pinned
  fixtures in `bridge_port.rs` tests come from the shared formula — keep them in
  lockstep with the mcp-server / CLI tests.
- **No migrations.** An incompatible `settings.json` / `projects.json` version
  is backed up to `.json.corrupt` and reset to defaults — never migrated.
- **Maintainer npm cwd.** For an `OpenMcp` repo, npm commands run in
  `<root>/mcp-server` (`resolve_npm_cwd`). Version sync runs from the **repo
  root** (`resolve_version_sync_cwd`) — `scripts/sync-version.mjs` resolves its
  targets relative to the root and must not be forced through the npm cwd.
- **Publish is confirm-gated.** `npm publish` is never one-click — it goes
  through `ConfirmationModal` showing package name, version, cwd, and a registry
  warning.
- **Clean UI strings.** No `specs/` paths, phase IDs, or competitor names in any
  user-visible string. Tracked docs may name Unity Open MCP (root exception).

## MCP client catalog — single source of truth

The client roster lives in `src-tauri/src/config/mcp_config.rs::agent_registry`
and mirrors the CLI's `cli/src/utils/agents.ts`. The two MUST stay in lockstep:

- Same ids, same `bodyPath`, same envelope shape (bare / vscode / opencode),
  same `removeKeys`, same config-path resolution.
- The minimum shipped bar is `cursor` + `claude-desktop` + `claude-code`.
- A change to the CLI catalog is a change here too. The parity is guarded by
  `src/lib/services/mcp_entry.test.ts` (TS entry shape) and the Rust
  `mcp_config` tests (merge + strip-url + idempotency).

## Stack conventions

- Svelte 5 **runes** (`$state` / `$derived` / `$props` / `$effect`). State stores
  live in `src/lib/state/*.svelte.ts`; services (typed `invoke` wrappers) in
  `src/lib/services/*.ts`.
- SvelteKit + `@sveltejs/adapter-static`, `ssr = false` (SPA in the Tauri
  webview). Same stack as `validation-suite/`.
- Tauri command names are snake_case in Rust; the frontend passes camelCase args
  (Tauri auto-converts).
- Run `npm run check` (svelte-check) on every `.svelte` change; `npm test`
  (node) for the pure TS builders; `cargo test` in `src-tauri/` for the Rust
  ports.

## Config directory

| Platform | Path |
|----------|------|
| macOS / Linux | `~/.config/godot-open-mcp-hub/` |
| Windows | `%APPDATA%\godot-open-mcp-hub\` |

Files: `settings.json`, `projects.json` (atomic write + `.json.corrupt` backup).
The bridge lock dir stays `~/.godot-open-mcp/` (shared with the bridge/CLI).
