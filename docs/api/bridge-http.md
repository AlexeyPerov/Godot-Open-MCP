# Bridge HTTP API

The Godot Open MCP bridge is a loopback HTTP server (`packages/bridge/Editor/Bridge/BridgeHttpServer.cs`) bound to a per-project deterministic port (`20000 + sha256(projectPath) % 10000`, overridable via `GODOT_OPEN_MCP_BRIDGE_PORT`). The MCP server routes tool calls to it over `POST /tools/{name}`; readiness probes hit `GET /ping`.

## Endpoints

### `GET /ping`

Live bridge status snapshot. Reads only cached session state (no main-thread hop), so it is safe to call from any thread.

- **200** — bridge ready. Body: the `PingResponse` (see below).
- **503** — bridge listener up, session not initialized yet. Body: the fallback `PingResponse` with `connected:false`, `compiling:true`. Treat as reachable-but-loading, not offline.
- **405** — any method other than `GET`. Body: `{ "error": { "code": "method_not_allowed", "message": "..." } }`.

`PingResponse` fields: `connected`, `projectPath`, `godotVersion`, `bridgeVersion`, `mode` (always `"live"` — Godot has no batch mode), `compiling`, `isPlaying`.

### `POST /tools/{name}`

Tool dispatch (P2.1). The `{name}` segment is the MCP tool name (`godot_open_mcp_*`). The request body is the tool's arguments as a JSON object; the bridge parses fields by key name (no schema validation on the bridge side — the MCP server validates against the tool's `inputSchema`).

The handler runs on the Godot editor main thread via `MainThreadDispatcher`. Every dispatch outcome — success or failure — is wrapped into the canonical envelope below.

- **200** — the request reached the dispatcher and was processed. Body: the canonical envelope (success or failure — see below).
- **404** — `{name}` is not a registered tool. Body: `{ "error": { "code": "tool_not_found", "message": "Unknown tool: <name>" } }`.
- **405** — any method other than `POST`. Body: `{ "error": { "code": "method_not_allowed", "message": "POST required for tool endpoints" } }`.
- **400** — the request body could not be read. Body: `{ "error": { "code": "invalid_request", "message": "..." } }`.
- **500** — unhandled bridge exception. Body: `{ "error": { "code": "bridge_internal_error", "message": "Unhandled bridge exception" } }`.

## Canonical envelope (P2.1)

Every tool dispatch outcome (HTTP 200) is wrapped into one of two envelope shapes so the MCP-side client parses a single contract:

**Success:**
```json
{ "ok": true, "result": <handler output> }
```
`result` is the handler's JSON output verbatim (an object, array, scalar, or `null`). A handler that returns no payload produces `"result":null`.

**Failure:**
```json
{ "ok": false, "error": { "code": "...", "message": "..." } }
```
`code` is a stable machine-readable string; `message` is human-readable.

### Failure codes (dispatched, HTTP 200 with `ok:false`)

These mean the request reached the dispatcher and was processed, but the tool failed. The agent branches on `code`:

| Code | Meaning | Agent action |
|---|---|---|
| `invalid_request` | The body parsed but a required field was missing or wrong. | Fix the arguments and retry. |
| `main_thread_blocked` | The Godot main thread did not pick up the work within the timeout — almost certainly a modal dialog (unsaved changes, export, a third-party editor window). | Do **not** raise `timeout_ms`. Dismiss any open dialog, `scene_save`, or restart the editor. |
| `timeout` | The handler started but ran past the timeout. | Raise `timeout_ms` or simplify the request. |
| `execution_error` | The handler threw an unhandled exception (a handler bug). | Report the error; do not retry blindly. |

### Failure codes (routing/transport, HTTP 4xx/5xx)

These mean the request did not reach the dispatcher (routing/transport fault):

| Code | HTTP | Meaning |
|---|---|---|
| `tool_not_found` | 404 | The tool name is not registered. |
| `method_not_allowed` | 405 | Wrong HTTP method for the route. |
| `invalid_request` | 400 | The request body could not be read. |
| `bridge_internal_error` | 500 | Unhandled bridge exception. |

## Request body fields

The dispatcher reads these scalar fields straight off the raw JSON body:

- `timeout_ms` (optional, integer) — per-call dispatch timeout in milliseconds. Clamped to `[1000, 600000]`; defaults to `30000` when absent. Distinguishes `main_thread_blocked` (never drained) from `timeout` (ran long).
- `gate` (optional, string) — gate mode override. **No-op in P2.1**; honored when the gate flow lands (P3.5).
- `paths_hint` (optional, string array) — mutation scope for the gate. **No-op in P2.1**; mandatory for mutators when the gate lands (P3.5).

All other fields are the tool's own arguments, parsed by the handler.

## Gate (deferred to P3.5)

P2.1 mutators dispatch **directly** — there is no gate wrapping. The `gate` and `paths_hint` fields may appear in tool schemas for forward-compatibility but are no-ops until P3.5. When the gate lands, the envelope may widen to carry gate metadata, but `ok` + `error.code` will stay stable so existing clients keep parsing.

## Auth (deferred to P5.2)

A per-session bearer token is minted into the instance lock on bridge start. The MCP server attaches `Authorization: Bearer <token>` to every request when present. Enforcement is opt-in via `authMode` in project settings (`"none"` default | `"required"`). In P2.1 the bridge does not enforce auth.

## Source of truth

- HTTP routing + dispatch: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- Envelope builders: `packages/bridge/Editor/Bridge/BridgeEnvelope.cs`
- Request-body parsing: `packages/bridge/Editor/Bridge/BridgeRequestBody.cs`
- Tool registry: `packages/bridge/Editor/Tools/BridgeToolRegistry.cs`
- Dispatch result model: `packages/bridge/Editor/Tools/ToolDispatchResult.cs`
- MCP-side client (envelope unwrap): `mcp-server/src/live-client.ts`
