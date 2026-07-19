# Manual setup

Install **Godot Open MCP** by hand: the editor addon on the Godot side and a
stdio MCP entry on the AI side. This is the path for users who prefer direct
file and config control, or who cannot run the CLI.

> **Automated alternative.** The `godot-open-mcp-cli` package automates
> everything below — install the addon, write the MCP client config, build the
> C# project, and launch the editor in one command each. See
> [`cli/README.md`](../cli/README.md) when you want the install automated.
>
> **Contributors.** If you are working on Godot Open MCP itself, use the
> [local-checkout](#local-checkout-server-configuration) section — the release
> path below assumes you obtained a complete addon bundle from elsewhere.

## How Godot Open MCP fits together (two halves)

Godot Open MCP has two independent halves that talk to each other over your
machine's loopback network:

| Half | Runs where | Installed from | Role |
|---|---|---|---|
| **AI side** | A small Node program (the MCP server) launched as a child process by your AI client | npm (`npx`) or a [local build](#local-checkout-server-configuration) | Exposes the stdio MCP surface and routes each tool call to the Godot side |
| **Godot side** | A Godot C# editor addon inside your project | A complete addon bundle or the [local checkout](#install-from-a-local-checkout-contributors) | Hosts the loopback HTTP bridge, the editor tools, and the gate/verify safety layer |

You need **both** halves. The AI side never touches the Godot editor directly —
it asks the Godot side over loopback HTTP. You configure the AI side over
**stdio**; you never configure a bridge URL in the AI client.

## Requirements

- **Godot 4.3 or newer, the .NET / C# (mono) build.** A GDScript-only editor
  cannot compile the addon. The macOS app is conventionally named
  `Godot_mono.app`; on Windows the executable ends in `_mono_win64.exe`. Verify
  in Godot with **Help → About Godot** — the dialog must list ".NET" support.
- **.NET 8 SDK.** Verify with `dotnet --version`.
- **Node.js 18 or newer.** The MCP server is a small Node program your AI client
  launches in the background; you never write JavaScript interactively. Verify
  with `node --version`.
- **An MCP client that supports stdio MCP servers** — Cursor, Claude Desktop,
  Claude Code, VS Code Copilot, OpenCode, Cline, Gemini, Kilo Code, GitHub
  Copilot CLI, or any compatible client. The exact snippets for the required
  clients are in [Configure the MCP client](#configure-the-mcp-client).

Preflight — confirm all three runtimes are on your PATH:

```bash
node --version      # v18 or newer
dotnet --version    # 8.x or newer
```

If the Godot mono editor is not on your PATH, note its absolute path — the CLI
`open` step and any manual launch will need it.

## Install the Godot addon

Pick **one** of the two paths below. The end-user path is for installing into a
real game project; the contributor path is for working on Godot Open MCP itself.

### End-user install (release bundle)

Godot Open MCP ships the bridge and verify source as a single addon at
`addons/godot_open_mcp/`. One plugin, one C# assembly, one enable step.

1. **Acquire the addon bundle** for the version you are targeting. The bundle
   must contain `plugin.cfg` at its root and the full `Editor/**`,
   `Runtime/**`, and `Verify/**` source subtrees. The version number is shared
   across the npm server, the CLI, and the addon — see
   [`version.json`](../version.json) at the root of the repository for the
   current version (`0.0.1` at the time of writing).
2. **Copy the bundle** into your project at
   `<project>/addons/godot_open_mcp/` so that the path
   `<project>/addons/godot_open_mcp/plugin.cfg` exists exactly.
3. **Confirm the layout.** The addon dir must contain:
   - `plugin.cfg` (the plugin manifest),
   - `Editor/**` (the bridge + tool handlers),
   - `Runtime/**` (main-thread dispatcher, log collector, screenshot math),
   - `Verify/**` (the verify package's `Editor/` source — required because the
     bridge references `GodotOpenMcp.Verify.*` namespaces; without it the addon
     does not compile).
4. **Build the C# project before the first editor open.** Godot instantiates an
   enabled addon's `EditorPlugin` the moment the editor loads. On a *fresh*
   first open of a C# project that has never been built, no assembly exists
   yet, so Godot fails with
   `Unable to load addon script '...GodotOpenMcpPlugin.cs' … Disabling the addon`
   and silently disables the plugin. Build first:

   ```bash
   cd /absolute/path/to/MyGame
   dotnet build MyGame.csproj -c Debug
   ```

5. **Enable the plugin.** Open the project in the Godot mono editor and go to
   **Project → Project Settings → Plugins**, then tick **Godot Open MCP**. Or
   edit `project.godot` directly so the `[editor_plugins]` section lists the
   plugin:

   ```ini
   [editor_plugins]

   enabled=PackedStringArray("res://addons/godot_open_mcp/plugin.cfg")
   ```

   When enabling by hand, leave any other enabled plugins in the array
   untouched.

6. **Open the project** (or restart the editor) and wait for the assembly to
   load. The addon starts the loopback bridge on enable.

> **Pre-open build is the #1 manual-setup gotcha.** A fresh C# project that has
> never been built will silently disable the addon on first open. If the plugin
> shows up disabled after launch, run `dotnet build`, fix any compile errors,
> and re-enable it under **Project Settings → Plugins**.

### Install from a local checkout (contributors)

If you are developing Godot Open MCP itself, install straight from the monorepo
with the CLI. The installer copies `packages/bridge` (plus the sibling verify
package) into the target project and enables the plugin:

```bash
cd /absolute/path/to/Godot-Open-MCP/cli
npm ci
npm run build

node dist/index.js install-plugin \
  --source /absolute/path/to/Godot-Open-MCP/packages/bridge \
  /absolute/path/to/MyGame
```

The CLI installer is idempotent: a re-run that finds the addon present and the
plugin already enabled reports `changed: false` and writes nothing. It bundles
the verify package's `Editor/` source into `addons/godot_open_mcp/Verify/` so
the addon compiles as a single C# assembly. Re-run it after pulling changes to
the bridge or verify source.

You still need the pre-open `dotnet build` step on first launch.

## Configure the MCP client

The AI side is a stdio MCP server your client spawns as a child process. Merge
the `godot-open-mcp` server entry into your client's config — **never replace
the whole config file**, and never delete sibling MCP servers.

The canonical server entry, version-pinned from
[`version.json`](../version.json):

```json
{
  "command": "npx",
  "args": ["-y", "godot-open-mcp@0.0.1"],
  "env": {
    "GODOT_PROJECT_PATH": "/absolute/path/to/MyGame"
  }
}
```

Rules that apply to every client below:

- Replace `0.0.1` with the current version from `version.json` if it has
  changed. The server version, the CLI version, and the addon version move
  together — bump all three when upgrading.
- `GODOT_PROJECT_PATH` must be the **absolute** path to the directory that
  contains your `project.godot`. A relative path makes the server start against
  the wrong project (or exit immediately), and the bridge will not be
  discoverable.
- **Do not add a `url`, an HTTP `type`, a `headers` map, or an auth token.**
  Transport is stdio-only; the bridge URL is discovered automatically.
- Omit `GODOT_OPEN_MCP_BRIDGE_PORT` normally. The bridge port is derived
  deterministically from the project path; pinning it is for advanced
  multi-project setups only. If you do pin it, the value must match on both
  sides (set it in the env entry and in
  `<project>/.godot-open-mcp/settings.json`).

### Cursor (project-local)

Merge into `<project>/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "godot-open-mcp": {
      "command": "npx",
      "args": ["-y", "godot-open-mcp@0.0.1"],
      "env": {
        "GODOT_PROJECT_PATH": "/absolute/path/to/MyGame"
      }
    }
  }
}
```

### Claude Code (project-local)

Merge into `<project>/.mcp.json`. The envelope is identical to Cursor's:

```json
{
  "mcpServers": {
    "godot-open-mcp": {
      "command": "npx",
      "args": ["-y", "godot-open-mcp@0.0.1"],
      "env": {
        "GODOT_PROJECT_PATH": "/absolute/path/to/MyGame"
      }
    }
  }
}
```

### Claude Desktop (user-global)

Claude Desktop lives in a user-global location, not the project. Merge the same
`mcpServers.godot-open-mcp` entry into:

- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
- **Linux:** `~/.config/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "godot-open-mcp": {
      "command": "npx",
      "args": ["-y", "godot-open-mcp@0.0.1"],
      "env": {
        "GODOT_PROJECT_PATH": "/absolute/path/to/MyGame"
      }
    }
  }
}
```

### Other supported clients

Cursor and Claude are the required clients. The CLI's `setup-mcp` command
writes the correct envelope for every client in its registry — VS Code Copilot,
Visual Studio Copilot, OpenCode, Cline, Gemini, Kilo Code, GitHub Copilot CLI,
and a generic custom target. Instead of duplicating each shape here, run:

```bash
node dist/index.js setup-mcp --list
node dist/index.js setup-mcp vscode-copilot /absolute/path/to/MyGame
```

The CLI writes the right envelope (e.g. `servers` with `type: "stdio"` for
VS Code, `mcp` with a `command` array + `environment` for OpenCode) into the
right path and preserves sibling servers. See
[`cli/README.md`](../cli/README.md) for the full command matrix.

## Local-checkout server configuration

Contributors and CI runs that want to spawn the in-tree server build instead of
`npx` should configure the AI client to launch the built `mcp-server` entry
directly. Build the MCP server once:

```bash
cd /absolute/path/to/Godot-Open-MCP/mcp-server
npm ci
npm run build
```

Then point the AI client at the built entry. Same envelope as the public
snippet, with `node` instead of `npx` and an absolute path to the built entry:

```json
{
  "command": "node",
  "args": ["/absolute/path/to/Godot-Open-MCP/mcp-server/dist/index.js"],
  "env": {
    "GODOT_PROJECT_PATH": "/absolute/path/to/MyGame"
  }
}
```

The CLI's `setup-mcp --use-local` switch writes this shape for you.

Restart the AI client whenever you switch between the `npx` and `node` shapes,
or after rebuilding the server — the client only re-reads MCP config on
restart.

## Verify end-to-end

Run these checks in order. The first half proves the Godot side is healthy;
the second proves the AI side can reach it.

1. **Build succeeds with zero errors.**

   ```bash
   cd /absolute/path/to/MyGame
   dotnet build MyGame.csproj -c Debug
   ```

   Compile errors here mean the addon will be disabled on the next editor
   open. Fix them before continuing.

2. **Open the Godot mono editor on the same project** and confirm the addon is
   still enabled under **Project → Project Settings → Plugins**. If it was
   silently disabled, the pre-open build was missing or stale — rebuild and
   re-enable.

3. **Confirm the bridge started.** The bridge writes an instance lock on
   enable; the CLI reads it. From the repository checkout:

   ```bash
   node /absolute/path/to/Godot-Open-MCP/cli/dist/index.js status /absolute/path/to/MyGame
   ```

   A healthy bridge reports `"status": "running"`. Do **not** compute the port
   by hand — the CLI resolves it from the instance lock.

4. **Restart your AI client** so it re-reads the MCP config from the previous
   step.

5. **Call `godot_open_mcp_capabilities`.** Ask your AI client to run it, or use
   the CLI:

   ```bash
   node /absolute/path/to/Godot-Open-MCP/cli/dist/index.js run-tool godot_open_mcp_capabilities /absolute/path/to/MyGame --json
   ```

   Expect a JSON payload listing tools, verify rules, fixes, and tool groups.

6. **Call `godot_open_mcp_ping`.** A successful ping returns
   `"connected": true`.

7. **Activate the `typed-editor` group and run a read-only call** (e.g.
   `godot_open_mcp_scene_list_opened`) to confirm the live route works against
   your project.

The guide intentionally avoids a full JSON snapshot — the high-level fields
(`connected`, `status`, the tool list) are stable; the rest of the payload
evolves with the capability surface.

## Troubleshooting

Branch on the structured status token the CLI `status` command (or the
`godot_open_mcp_bridge_status` MCP tool) returns.

| Symptom | Check | Recovery |
|---|---|---|
| The MCP server does not start | `node --version`, the `GODOT_PROJECT_PATH` is absolute and points at a real `project.godot`, and the version pin in `args` matches `version.json` | Fix the executable/path/version, then restart the AI client. |
| The addon is silently disabled on open | The pre-open `dotnet build` did not run, or it failed | Build the project (`dotnet build`), fix any compile errors, then re-enable under **Project Settings → Plugins**. |
| `status` is `stopped` | No live bridge listener — the editor is closed, or the addon is disabled | Open the same project in the Godot mono editor and confirm the plugin is enabled. |
| `status` is `unreachable` | Transient — the editor is reloading assemblies or you pinned a stale `GODOT_OPEN_MCP_BRIDGE_PORT` | Wait a few seconds and retry. If you set `GODOT_OPEN_MCP_BRIDGE_PORT`, remove it from both sides and let discovery work. |
| `status` is `dead_bridge` | The Godot process is alive but the bridge heartbeat is stale, usually because the addon assembly failed to compile | Run `dotnet build` again; fix any compile errors; reopen the editor. The offline `read_compile_errors` reader can read `Editor.log` without a live editor. |
| `status` is `compiling` | The editor is mid-rebuild | Wait for the build to finish and re-check status. |
| MCP tools are missing | The tool group is not active in this session | Call `godot_open_mcp_manage_tools` with `action: "list_groups"`, then `action: "activate"` for the group you need (typically `typed-editor`). |
| Offline reads return partial data (no live node ids, no unsaved edits) | The route is offline because no editor is running | Open the editor on the same project; the router picks the live route automatically when the bridge is reachable. |
| Auth failure when `authMode: "required"` is set | The instance-lock token is not being forwarded | Confirm the AI client did not override `env`; the bridge mints a per-session token into the instance lock and the MCP server reads it from there. Do **not** disable required auth on a remote bind. |

If a step produces a status or error token not listed here, run
`godot_open_mcp_read_compile_errors` to read the most recent C# compiler
output from `Editor.log` without needing a live editor — the same reader the
bridge uses to report `dead_bridge`.

## Updates and uninstall

- **Update.** Bump the npm pin in your MCP config, the CLI version, and the
  addon bundle together — the three share a version number. Re-run the CLI
  installer (it is idempotent) or replace the addon bundle outright; rebuild
  the C# project; restart the editor and the AI client.
- **Uninstall.** Disable the plugin in **Project Settings → Plugins** (or
  remove the entry from `[editor_plugins] enabled`) **before** deleting the
  addon directory, so Godot does not try to load a missing script on next
  launch. Then remove `addons/godot_open_mcp/` and the `godot-open-mcp` entry
  from your MCP client config.

There are no data migrations or compatibility shims — a clean reinstall is the
supported upgrade path.

## Related docs

- [Architecture](architecture.md) — repository boundaries and runtime flow.
- [MCP tool catalog](api/mcp-tools.md) — every shipped tool, its route policy,
  visibility group, inputs, results, and errors.
- [Bridge HTTP contract](api/bridge-http.md) — the loopback protocol between
  the AI side and the Godot side.
- [Agent skills](skills.md) — the canonical agent playbook installed into game
  projects.
- [`cli/README.md`](../cli/README.md) — the automated alternative to this
  guide (`godot-open-mcp-cli`).
