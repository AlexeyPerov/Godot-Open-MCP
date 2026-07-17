# API and Protocol Surfaces

This file is the index for external interfaces and protocol contracts exposed by this repository.

## Domain reference

| Document | Covers | Status |
|---|---|---|
| `api/bridge-http.md` | Godot bridge HTTP endpoints (`/ping`, `/tools/*`), envelopes, and errors. | Shipped (P2.1) |
| `api/mcp-tools.md` | MCP tool catalog, tool families, route policy (live / local / offline / live-first), `capabilities` surface, rule + fix catalog contract. | Shipped |
| `api/resources.md` | MCP resource URIs, payload shapes, and resource router behavior. | TBD |

## Contract boundaries

- Bridge HTTP contract source: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- MCP server routing/registry source: `mcp-server/src/index.ts` (registration validation + dispatch), `mcp-server/src/tool-router.ts` (route authority: live/offline/local selection + `_source` / `_route` metadata), `mcp-server/src/router.ts` (Router seam), `mcp-server/src/live-client.ts` (live transport)
- MCP capabilities surface (local rule/fix catalog + builder): `mcp-server/src/capabilities/`
- MCP tool definitions source: `mcp-server/src/tools/`
- MCP resources source: `mcp-server/src/resources/` (when shipped)
- Phase 1 parity smoke: `mcp-server/src/integration.test.ts` (in-process, runs on every `npm test`) and `mcp-server/scripts/p1-parity-smoke.mjs` (`npm run smoke:p1`, real stdio child). Pinned end-to-end route: MCP client → `godot_open_mcp_ping` → bridge `GET /ping`.

## Contract documentation guidance

- Prefer documenting behavior and payload shapes over implementation details.
- Call out breaking changes explicitly.
- Keep examples minimal and representative.
- Godot paths use `res://`; no batch route — document live / offline / local / live-first only. The shipped route policies and per-tool overrides live in `mcp-server/src/capabilities/route-policy.ts`; see `api/mcp-tools.md` §Route policy for the catalog.

## Update triggers

Update this index when:

- a new API/protocol doc is added,
- contract ownership moves to new modules,
- endpoint or resource domains are reorganized.
