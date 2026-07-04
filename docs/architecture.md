# Architecture

Godot Open MCP has four runtime parts:

- **Godot project** with bridge and verify addons installed (`addons/godot_open_mcp/`).
- **Bridge** — C# EditorPlugin, loopback HTTP, main-thread dispatch.
- **MCP server** — TypeScript stdio server, tool registry, routing.
- **CLI** — install, setup-mcp, open, wait-for-ready.

A desktop **Hub** app for guided setup is planned but deferred.

## Repository map

- `mcp-server/` — MCP stdio server, tool registry, routing.
- `packages/bridge/` — Godot HTTP bridge and typed tool handlers (shipped as `addons/godot_open_mcp/`).
- `packages/verify/` — validation rules and fixes used by gate flows.
- `cli/` — `godot-open-mcp-cli` command-line tooling.
- `skills/` — agent playbooks (`SKILL.md`).
- `demo/` — Godot C# demo project with fixtures.
- `scripts/` — version sync and maintenance scripts.

## Runtime flow

1. AI client calls an MCP tool.
2. MCP server chooses route policy.
3. Call goes to:
   - live bridge (preferred), or
   - offline/local readers (supported tools), or
   - local-only handlers (capabilities, manage_tools).
4. Response includes route metadata.

```mermaid
flowchart LR
  AIClient[AI_client] -->|stdio_MCP| McpServer[mcp_server]
  McpServer -->|live_HTTP| Bridge[godot_bridge_addon]
  McpServer -->|offline| DiskReaders[res_parsers]
  Bridge --> Verify[verify_addon]
  Bridge --> GodotEditor[EditorInterface]
  Cli[godot_open_mcp_cli] --> McpServer
  Cli --> Bridge
```

## Route types

- `live` — Godot Editor bridge is running and reachable.
- `offline` — disk readers for selected scene/resource/project operations (no editor required).
- `local` — no Godot call required (catalog-style operations).

Godot has no headless editor batch mode — there is no `batch` route.

## Godot-specific constraints

- Single C# assembly — use `#if TOOLS` for editor-only code. Runtime must not leak editor APIs.
- Paths are `res://`; scenes are `.tscn`; resources are `.tres`/`.res`.
- All `EditorInterface` / `Node` API calls run on the main thread via a dispatcher.
- v1 requires **Godot 4.3+ mono (C#/.NET 8)**.

## Core source files (planned)

- `mcp-server/src/index.ts`
- `mcp-server/src/tool-router.ts`
- `mcp-server/src/instance-discovery.ts`
- `packages/bridge/plugin.cfg` — addon metadata; installed as `addons/godot_open_mcp/plugin.cfg`.
- `packages/bridge/Editor/GodotOpenMcpPlugin.cs` — editor entry point; owns bridge enable/disable lifecycle.
- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- `packages/bridge/Editor/Bridge/BridgeInstanceLock.cs`

## Versioning

The repo tracks a shared version for the npm MCP server, bridge addon, and verify addon from `version.json`. Generated version strings are synced by `scripts/sync-version.mjs`.

## Related docs

- [API index](api.md)
- [Porting principles](porting-principles.md)
- Detailed API docs (TBD): `api/mcp-tools.md`, `api/bridge-http.md`, `api/resources.md`
