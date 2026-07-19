# Godot Open MCP

[![MCP](https://badge.mcpx.dev 'MCP Server')](https://modelcontextprotocol.io/introduction)
[![Godot](https://img.shields.io/badge/Godot-4.3%2B-478CBF?style=flat&logo=godotengine&logoColor=white 'Godot 4.3+, C# mono')](https://godotengine.org/)
[![License](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

**Godot Open MCP** is local-first AI tooling for Godot 4.3+ game projects. Agents connect via stdio MCP (Cursor, Claude, Copilot, and others) and drive the Godot editor through a loopback HTTP bridge with a safe mutation workflow.

> **Status:** Pre-alpha — bootstrap in progress. Core bridge, tools, and CLI are not yet shipped.

## Features

- **Gate + verify workflow** — automatic validation, checkpoints, deltas, and targeted fixes before and after mutations.
- **Native stdio MCP** — no HTTP proxy or cloud dependency for Cursor and other native MCP clients.
- **40+ tools across core families** — node, scene, resource, filesystem, editor, console, screenshot, and reflection tools, plus bridge health, capabilities, and tool-group management. See the [MCP tool catalog](docs/api/mcp-tools.md) for the complete inventory.
- **Offline reads** — partial scene and filesystem introspection without a running editor.
- **Tool groups + `manage_tools`** — keep the prompt surface small as tool count grows.
- **Open MIT stack** — fully self-hostable, no vendor lock-in.

Requires **Godot 4.3+ with C#/.NET 8 (mono)**.

## Architecture reference

This project ports the architecture of [Unity Open MCP](https://github.com/AlexeyPerov/Unity-Open-MCP) — stdio MCP server, loopback HTTP bridge, and gate/verify safety layer — adapted for Godot.

## Documentation

- [Architecture](docs/architecture.md) — repository boundaries and runtime flow.
- [Manual setup](docs/manual-setup.md) — install the editor addon and configure an MCP client by hand.
- [API index](docs/api.md) — contract documentation map.
- [MCP tool catalog](docs/api/mcp-tools.md) — every shipped tool, its route policy, visibility group, inputs, results, and errors.
- [Bridge HTTP contract](docs/api/bridge-http.md) — `/ping`, `/tools/*`, `/events`, envelopes, and errors.
- [Agent skills](docs/skills.md) — the canonical agent playbook and install-target map.
- [Porting principles](docs/porting-principles.md) — Unity-first porting protocol for contributors.
- CLI — `godot-open-mcp-cli` (`cli/`) for install, setup, open, and status. See [`cli/README.md`](cli/README.md) for the command matrix and `cli/AGENTS.md` for the package boundary.

## Contributing

PRs welcome. See the docs above and package-level `AGENTS.md` files for local development rules.

**License:** MIT — see [LICENSE](LICENSE).
