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

## Runtime / Editor boundary

Godot compiles the entire bridge addon into **one** C# assembly — there is no per-folder
assembly boundary the way Unity's asmdefs provide. What keeps editor-only code out of a
shipped game build is the `TOOLS` compilation symbol, which Godot defines **only** for the
editor (`Debug`) configuration and leaves undefined for `ExportDebug` / `ExportRelease`.
Code wrapped in `#if TOOLS … #endif` is compiled into the editor and stripped from an
exported game.

The bridge mirrors that split at the folder level so the boundary is visible at a glance:

- `packages/bridge/Runtime/` — ships into a game build. Must not reference any editor-only
  Godot API in real code.
- `packages/bridge/Editor/` — editor-only (`EditorPlugin`, the HTTP bridge, tool handlers).
  Always wrapped in `#if TOOLS`.

The load-bearing invariant is one-directional: **Editor code may reference Runtime code;
Runtime code may NEVER reference Editor code.** A `Runtime/` file that referenced an
editor-only type (`EditorInterface`, `EditorPlugin`, `EditorFileSystem`, `EditorScript`)
in shipping code would either fail the `ExportRelease` compile or drag editor behaviour
into a context where no editor exists.

### CI boundary guard

`scripts/check-runtime-boundary.py` enforces the rule on every PR (wired into
`.github/workflows/ci.yml` as the fast `Runtime/Editor boundary guard` job, which runs
before any .NET build). It scans `packages/bridge/Runtime/**/*.cs` and **exits non-zero**
if any file references an editor-only API in real code that is:

- **not** inside a `#if TOOLS … #endif` guard, and
- **not** inside a comment or string literal, and
- **not** explicitly suppressed.

The check is comment- and string-aware on purpose: runtime files mention editor type names
heavily in doc-comments and `[Description("…")]` strings to *explain* the boundary, and
those are not violations — only un-guarded code is. This mirrors exactly what the
`ExportRelease` (TOOLS-undefined) compile strips, but runs in milliseconds with no build.

A `#if TOOLS` block in `Runtime/` is allowed only as a narrow, documented editor-coupling
shim (the guarded body is stripped from the game build, so it cannot leak). Such shims are
reported as a warning under `--verbose`, not a failure.

Run it locally any time:

```bash
python scripts/check-runtime-boundary.py            # 0 = boundary holds, 1 = violation
python scripts/check-runtime-boundary.py --verbose  # also lists #if TOOLS shims + suppressions
```

This guard is a fast pre-filter; the authoritative proof that the boundary holds is that
the `ExportRelease` (TOOLS-undefined) configuration **compiles** and the resulting assembly
contains the runtime types and none of the editor types.

### Suppressions (justified exceptions)

The default policy is "no editor APIs in `Runtime/` real code outside `#if TOOLS`." For the
rare case where a `Runtime/` file must reference an editor type in shipping code, append a
trailing `// boundary:allow <audit-note>` line comment on the **same line** as the token:

```csharp
var name = nameof(EditorInterface); // boundary:allow ADR-XYZ forwarded type name
```

The audit note (anything non-empty after `boundary:allow`) is mandatory so the exception is
visible in code review and under `--verbose` in CI output. This is the **only** suppression
path — do not silence the guard by other means.

## Multi-instance port + discovery

Multiple Godot projects can run bridges simultaneously without port collisions. The bridge port is **deterministic per project**: `20000 + (sha256(normalizedProjectPath) % 10000)`, where the hash uses the first 8 bytes of SHA256 as a big-endian `UInt64` so the C# bridge and the TypeScript MCP server agree byte-for-byte. The `GODOT_OPEN_MCP_BRIDGE_PORT` env var overrides the deterministic default.

Each running bridge owns a lock file at `~/.godot-open-mcp/instances/<sha256(projectPath)>.json` carrying the PID, port, project path/hash, and editor state (idle/compiling/playing/...). The MCP server reads these to discover the right port per project without an HTTP round-trip. Stale locks (from a crashed editor) are swept on the next `Acquire` by PID-liveness; the MCP server is read-only on the lock.

## Core source files (planned)

- `mcp-server/src/index.ts` — stdio MCP bootstrap; wires the SDK `Server` to a `StdioServerTransport`, registers `ListTools` / `CallTool` against the tool registry, exits cleanly on transport close.
- `mcp-server/src/tools/index.ts` — tool registry; `godot_open_mcp_ping` is the first entry (P1.7). Subsequent phases append editor / gate / capability tools.
- `mcp-server/src/tools/ping.ts` — `godot_open_mcp_ping` tool definition (catalog metadata only; the call path lives in `live-client.ts`).
- `mcp-server/src/live-client.ts` — live bridge client; routes registered tool calls into bridge HTTP. P1.7 wires the ping round-trip (`GET /ping`); mutating tool dispatch arrives in later phases.
- `mcp-server/src/results.ts` — shared `CallToolResult` error factory (`{ error: { code, message } }` envelope); reused by every error path so the wire shape stays consistent.
- `mcp-server/src/instance-discovery.ts` — per-project bridge port + auth token resolution from instance locks; mirrors `InstancePortResolver.cs` byte-for-byte.
- `mcp-server/src/tool-router.ts` — live/offline/local route selection (planned).
- `packages/bridge/plugin.cfg` — addon metadata; installed as `addons/godot_open_mcp/plugin.cfg`.
- `packages/bridge/Editor/GodotOpenMcpPlugin.cs` — editor entry point; owns bridge enable/disable lifecycle (installs the dispatcher, caches session state, starts/stops the HTTP listener).
- `packages/bridge/Runtime/MainThread/MainThreadDispatcher.cs` — pumps off-thread work onto the editor main thread via a long-lived `Node._Process` tick; the single dispatch path all editor API calls route through.
- `packages/bridge/Editor/Bridge/BridgeHttpServer.cs` — loopback `HttpListener` + listener thread; serves `GET /ping` (P1.3) and will host tool dispatch / events / auth in later phases.
- `packages/bridge/Editor/Bridge/BridgeSession.cs` — process-wide session state read by `/ping` (project path, Godot version, connected/compiling/playing flags, bridge version).
- `packages/bridge/Editor/Bridge/BridgeBindAddress.cs` — loopback-only bind decision (P1.3); widens to remote+auth in P5.2.
- `packages/bridge/Editor/Bridge/InstancePortResolver.cs` — per-project deterministic port (`20000 + sha256(path) % 10000`) and `~/.godot-open-mcp/instances/<hash>.json` lock path (P1.4). Mirrors `mcp-server/src/instance-discovery.ts` byte-for-byte.
- `packages/bridge/Editor/Bridge/BridgeInstanceLock.cs` — instance lock + heartbeat file lifecycle (Acquire/UpdateState/Release, stale-lock sweep by PID liveness) for multi-instance port discovery (P1.4).

## Versioning

The repo tracks a shared version for the npm MCP server, bridge addon, and verify addon from `version.json`. Generated version strings are synced by `scripts/sync-version.mjs`.

## Related docs

- [API index](api.md)
- [Porting principles](porting-principles.md)
- Detailed API docs (TBD): `api/mcp-tools.md`, `api/bridge-http.md`, `api/resources.md`
