# Godot Open MCP

[![MCP](https://badge.mcpx.dev 'MCP Server')](https://modelcontextprotocol.io/introduction)
[![Godot](https://img.shields.io/badge/Godot-4.3%2B-478CBF?style=flat&logo=godotengine&logoColor=white 'Godot 4.3+, C# mono')](https://godotengine.org/)
[![Status](https://img.shields.io/badge/status-under%20development-orange 'Under development')](#)
[![License](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

> 🚧 **Under development** — pre-alpha. Bridge, tools, and CLI are still landing; APIs and docs may change.

Godot Open MCP gives AI agents a typed, safety-gated tool surface for Godot
4.3+ projects (C# / .NET mono).

It is based on the architecture and product concept of
[Unity Open MCP](https://github.com/AlexeyPerov/Unity-Open-MCP) — stdio MCP
server, loopback HTTP bridge, and gate/verify safety layer — adapted for Godot.

The MCP server exposes **40+ tools** across node, scene, resource, filesystem,
editor, console, screenshot, and reflection workflows, plus bridge health,
capabilities, and tool-group management.

---
Part of Open MCP toolset
---
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

## Key features

### Gate + verify workflow

Automatic validation, checkpoints, deltas, and targeted fixes before and after
mutations — so agents can stop before a “successful” edit leaves the project
broken.

> **Example:** "Delete that node, but only if the scene stays valid — show me
> the gate delta first."

### Live bridge + offline reads

Prefer the live Godot editor over a loopback HTTP bridge; read scenes and
filesystem state from disk when the editor is closed.

> **Example:** "Bridge is offline — list the scenes under `res://levels/`."

### Typed editor workflows

Nodes, scenes, resources, filesystem, editor, console, screenshots, and
reflection tools with a consistent MCP surface.

> **Example:** "Open `main.tscn`, find the Player node, and set its position."

### Session tool groups

Default groups stay small; activate domains on demand with `manage_tools` as
the tool count grows.

> **Example:** "Reset tool groups, then activate only core editor and scene
> tools."

### CLI installer and operator

`godot-open-mcp-cli` installs the editor addon, writes MCP client config, and
checks bridge status — optional if you prefer manual setup.

> **Example:** use `godot-open-mcp-cli` to install the addon and wire Cursor
> (see [`cli/README.md`](cli/README.md)).

More details: [MCP tool catalog](docs/api/mcp-tools.md).

## Quick setup

Requires **Godot 4.3+ with C# / .NET 8 (mono)**.

1. **Manual:** install the editor addon and configure your MCP client with
   [Manual setup](docs/manual-setup.md).
2. **CLI:** automate install and MCP client wiring with
   [`godot-open-mcp-cli`](cli/README.md).

## Documentation

For users:

- [Manual setup](docs/manual-setup.md) — install the editor addon and configure an MCP client by hand.
- [API index](docs/api.md) — contract documentation map.
- [MCP tool catalog](docs/api/mcp-tools.md) — every shipped tool, route policy, visibility group, inputs, results, and errors.
- [Bridge HTTP contract](docs/api/bridge-http.md) — `/ping`, `/tools/*`, `/events`, envelopes, and errors.
- [Agent skills](docs/skills.md) — the canonical agent playbook and install-target map.
- [CLI](cli/README.md) — `godot-open-mcp-cli` command matrix.

For contributors:

- [Architecture](docs/architecture.md) — repository boundaries and runtime flow.
- [Porting principles](docs/porting-principles.md) — Unity-first porting protocol.
- [Demo project](demo/README.md) — Godot C# integration fixture for bridge, verify, CLI, and MCP smoke.
- [Hub app](hub/README.md) — desktop guided AI setup + maintainer panel (GUI over the CLI contracts).

## Hub app

The [Hub](hub/README.md) is an optional desktop app (Tauri + SvelteKit) that
turns the CLI into a clickable path: add a Godot project, then run **AI Setup**
to install the addon, write your MCP client config, launch the editor, and wait
for the bridge — no terminal required. It also has a maintainer panel for the
npm package. It's a GUI over the CLI contracts and never diverges from them; the
MCP path does not depend on it. Concept mirrors
[Unity Hub Pro](https://github.com/AlexeyPerov/Unity-Open-MCP).

## Contributing

PRs welcome. See the docs above and package-level `AGENTS.md` files for local
development rules.

**License:** MIT — see [LICENSE](LICENSE).
