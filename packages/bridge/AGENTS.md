# Bridge package rules

## Scope

Rules for `packages/bridge/` — the Godot Editor HTTP bridge (`addons/godot_open_mcp/`). Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- Godot 4.3+ C# mono addon. Editor code lives under `Editor/`; runtime-safe code under `Runtime/` wrapped in `#if TOOLS` where editor APIs are required.
- Hard dependency on the verify addon — the gate flow calls into verify for checkpoint/validate/delta. Do not break this dependency direction.
- Single C# assembly — use `#if TOOLS` for editor-only code. Runtime must not leak `EditorInterface` or other editor APIs.
- Tests live under `Tests/`.

## Tool registration

- Tools are registered with the HTTP server (`BridgeHttpServer.cs`) and dispatched via `POST /tools/{name}`.
- Every new tool must declare:
  - A unique `Name` (the MCP tool name, `godot_open_mcp_*`).
  - Whether it is mutating (changes Godot project/editor state).
  - Default gate mode for mutating tools (`Enforce` / `Warn` / `Off`).
  - Tool group id from the canonical catalog in `mcp-server/src/capabilities/tool-groups.ts`.
- Mutating tools must accept and honor the request-level `gate` value.
- When adding/removing/renaming a tool, update the MCP-side tool definition (`mcp-server/src/tools/`) in the same task.

## Tool-group visibility

- Sessions start with only the `core` group visible in `ListTools`; other groups are hidden until activated via `godot_open_mcp_manage_tools`.
- The bridge does NOT track session state — the MCP server owns it. The bridge's role is compiled-state reporting only.
- `GET /tools` returns the tool inventory plus the group→tools map for capability probes.

## Gate policy

- The gate flow (checkpoint → mutate → validate → delta) is the bridge's core safety contract. Do not add a mutating dispatch path that bypasses `GatePolicy.Execute`.
- `paths_hint` is mandatory for mutating tool calls — there is no whole-project fallback. Do not add one.
- Gate precedence: request `gate` → project default → tool default.

## Transport

- `BridgeHttpServer` binds `127.0.0.1` by default. Remote bind is opt-in and requires auth when enabled.
- All `EditorInterface` / `Node` API calls happen on the main thread via `MainThreadDispatcher`. Never call editor APIs from the HTTP listener worker thread.
- Paths are `res://`; scenes are `.tscn`; resources are `.tres`/`.res`.

## Auth

- A per-session bearer token is minted into the instance lock on bridge start and mirrored as `authToken` in the lock JSON. The TS-side `InstanceLock` interface must carry the same field.
- Enforcement is opt-in via `authMode` in project settings (`"none"` default | `"required"`).
- Token comparison must be constant-time.

## Multi-instance port + discovery

- The bridge port is **deterministic per project**: `20000 + (sha256(projectPath) % 10000)`, implemented in `InstancePortResolver`. Must match `mcp-server/src/instance-discovery.ts` byte-for-byte.
- `GODOT_OPEN_MCP_BRIDGE_PORT` (env) overrides the deterministic default.
- Each running bridge writes a lock file at `~/.godot-open-mcp/instances/<sha256(projectPath)>.json` via `BridgeInstanceLock`.
- Stale locks (crashed editor) are swept on `Acquire` by PID-liveness. The MCP server is read-only on the lock.
- Lock retention on domain reload: do not release the lock on assembly reload — stale heartbeat + live PID signals a dead bridge to the MCP server.

## Verification

- C# changes: add or update the narrowest test in `Tests/`.
- Tool contract changes: update the MCP-side tool definition in the same task.
- Gate flow changes: verify delta math in integration tests.
