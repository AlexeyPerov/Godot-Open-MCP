# Godot Open MCP Hub

A desktop app for setting up Godot Open MCP without the terminal. It's a GUI over
the [`godot-open-mcp-cli`](../cli) contracts: it produces the same outcomes the
CLI does (install the addon, write a stdio MCP client config, launch the editor,
wait for the bridge), plus a maintainer panel for the npm package.

Built with Tauri 2 + SvelteKit + Svelte 5 — the same stack as the
[Validation Suite](../validation-suite).

## Features

- **Projects** — add a folder that contains a `project.godot`; the list persists
  across restarts.
- **AI Setup wizard** — a clickable path that:
  1. detects project validity, addon state, Node, and existing MCP config;
  2. installs `addons/godot_open_mcp/` and enables the plugin in `project.godot`;
  3. writes a stdio `godot-open-mcp` entry into your AI client's config
     (Cursor, Claude Desktop/Code, and more) — never an HTTP `url` entry;
  4. launches the Godot editor and polls the bridge `/ping` until it's ready.
- **Maintainer panel** (tooling repo only) — run `mcp-server` npm scripts
  (build, test, publish dry-run, publish behind a confirmation) and version sync.

## Develop

```bash
cd hub
npm install
npm run tauri dev      # launches the "Godot Open MCP Hub" window
```

Requires the Rust toolchain + the Tauri 2 prerequisites for your OS
(see https://v2.tauri.app/start/prerequisites/).

### Checks

```bash
npm run check          # svelte-check (frontend types)
npm test               # node --test — pure MCP-entry builder + CLI parity
cd src-tauri && cargo test   # Rust: bridge-port fixtures, MCP merge, install, resolve_npm_cwd
```

## Config

Settings and the project inventory live under the OS config dir:

| Platform | Path |
|----------|------|
| macOS / Linux | `~/.config/godot-open-mcp-hub/` |
| Windows | `%APPDATA%\godot-open-mcp-hub\` |

- `settings.json` — Godot editor path, MCP source, default client.
- `projects.json` — the project inventory.

A corrupt or version-incompatible file is backed up to `<name>.json.corrupt` and
reset to defaults (no migrations).

## MCP config shape

The wizard writes a stdio entry, e.g. for Cursor (`.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "godot-open-mcp": {
      "command": "npx",
      "args": ["-y", "godot-open-mcp@0.0.1"],
      "env": { "GODOT_PROJECT_PATH": "/abs/path/to/project" }
    }
  }
}
```

For a local monorepo checkout it writes `command: "node"` with
`<repo>/mcp-server/dist/index.js`. A pinned bridge port adds
`GODOT_OPEN_MCP_BRIDGE_PORT` to `env`. The Hub never writes a `url` / HTTP entry.

## Relationship to the CLI

The Hub does not diverge from the CLI. The Rust `config` modules mirror the CLI
algorithms (install, setup-mcp, editor discovery, ping, the deterministic
bridge-port formula), and a parity test pins the Hub's MCP entry to the CLI's
`setup-mcp` output. See [`AGENTS.md`](./AGENTS.md) for the catalog + port rules.
