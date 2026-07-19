# godot-open-mcp

Model Context Protocol (stdio) server that routes tool calls to a live Godot Open MCP bridge running inside the Godot Editor.

The same `godot-open-mcp` binary serves two roles:

- **MCP client mode (default).** Invoked with no command (e.g. `node dist/index.js`), it runs the stdio MCP server an AI client connects to. The project root comes from `GODOT_PROJECT_PATH`.
- **CLI mode.** Invoked with a recognized subcommand, it runs a one-shot command and exits — useful for CI, scripting, and the Validation Suite's `mcp_tool` actions.

## Install / build

```bash
npm install
npm run build      # compiles TypeScript to dist/
```

To put the `godot-open-mcp` binary on your `PATH` during development, run `npm link` in this directory (or invoke `node dist/index.js …` directly).

## CLI

```
godot-open-mcp <command> [options]
```

| Command | Purpose |
|---|---|
| `run-tool <name>` | Invoke an MCP tool by name and print its JSON result. |
| `--help`, `-h` | Show help. |
| `--version`, `-V` | Print the package version. |

Options: `--json` (machine output), `--project <path>` / `-P` (Godot project path; overrides `GODOT_PROJECT_PATH`), `--port <n>` / `-p` (bridge port; overrides `GODOT_OPEN_MCP_BRIDGE_PORT`), `--args '<json>'` (tool args as a JSON object), `--arg key=value` (one tool arg, repeatable).

### `run-tool`

Resolves the bridge for `--project`, dispatches through the same tool router an MCP client uses, prints the result as JSON, and sets the exit code:

```bash
# with the Godot editor open on the project and the bridge running
godot-open-mcp run-tool godot_open_mcp_ping --project /path/to/demo --json
```

Success:

```json
{ "command": "run-tool", "tool": "godot_open_mcp_ping", "isError": false, "result": { "...": "..." } }
```

Exit codes: `0` success (`isError: false`), `1` tool error (`isError: true`), `2` usage error (unknown tool, bad args, or missing project path). An unknown tool prints a structured `{ "error": { "code": "unknown_tool", "available": [...] } }`.

## Tests

```bash
npm test
```
