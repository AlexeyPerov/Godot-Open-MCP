# API and Protocol Surfaces

This file is the index for external interfaces and protocol contracts exposed by this repository.

## Domain reference

| Document | Covers | Status |
|---|---|---|
| `api/bridge-http.md` | Godot bridge HTTP endpoints (`/ping`, `/tools/*`, `/events`), envelopes, and errors. | Shipped |
| `api/mcp-tools.md` | Complete MCP tool catalog: canonical inventory table, per-tool detail sections, shared route/group/gate/error contracts, and offline fidelity notes. | Shipped |
| `api/resources.md` | MCP resource URIs, payload shapes, and resource router behavior. | TBD |

## Related surfaces

- [Manual setup](manual-setup.md) — install the editor addon and configure an MCP client by hand (the CLI-free path). Required stdio config snippets for Cursor, Claude Desktop, and Claude Code live there.
- [Agent skills](skills.md) — operational playbook installed into game projects (`skills/godot-open-mcp/SKILL.md`) and the per-client install-target manifest. Indexes the agent-facing guidance that complements the API tables above.

## Contract boundaries

- Bridge HTTP contract source: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- MCP server routing/registry source: `mcp-server/src/index.ts` (registration validation + dispatch), `mcp-server/src/tool-router.ts` (route authority: live/offline/local selection + `_source` / `_route` metadata), `mcp-server/src/router.ts` (Router seam), `mcp-server/src/live-client.ts` (live transport)
- MCP capabilities surface (local rule/fix catalog + builder): `mcp-server/src/capabilities/`
- MCP tool definitions source: `mcp-server/src/tools/` (`ALL_TOOLS` in `index.ts`)
- Route policy per tool: `mcp-server/src/capabilities/route-policy.ts`
- Visibility group per tool: `mcp-server/src/capabilities/tool-groups.ts`
- MCP resources source: `mcp-server/src/resources/` (when shipped)
- Phase 1 parity smoke: `mcp-server/src/integration.test.ts` (in-process, runs on every `npm test`) and `mcp-server/scripts/p1-parity-smoke.mjs` (`npm run smoke:p1`, real stdio child). Pinned end-to-end route: MCP client → `godot_open_mcp_ping` → bridge `GET /ping`.

## Contract documentation guidance

- Prefer documenting behavior and payload shapes over implementation details.
- Call out breaking changes explicitly.
- Keep examples minimal and representative.
- Godot paths use `res://`; no batch route — document live / offline / local / live-first only. The shipped route policies and per-tool overrides live in `mcp-server/src/capabilities/route-policy.ts`; see `api/mcp-tools.md` [§Route policy](api/mcp-tools.md#route-policy) for the catalog. The canonical inventory table in `api/mcp-tools.md` is parity-checked against `ALL_TOOLS` by `scripts/check-tool-docs.mjs`.

## Update triggers

Update this index when:

- a new API/protocol doc is added,
- contract ownership moves to new modules,
- endpoint or resource domains are reorganized.
