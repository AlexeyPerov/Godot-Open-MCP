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
| `paths_hint_required` | A mutating tool was called with `gate` set to `enforce` or `warn` but no `paths_hint`. There is no whole-project fallback. | Pass a non-empty `paths_hint` (the `res://` paths the mutation touches) and retry. |
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
- `gate` (optional, string) — gate mode for this call. One of `enforce`, `warn`, `off`. Precedence: request `gate` → project default (`.godot-open-mcp/settings.json`, not yet wired) → the tool's registered default (currently `off` for every tool).
- `paths_hint` (optional, string array) — mutation scope (`res://` paths) the gate checkpoints and validates against. **Required** when `gate` is `enforce` or `warn`; optional when `gate` is `off`. There is no whole-project fallback.

All other fields are the tool's own arguments, parsed by the handler.

## Gate

Every mutating tool routes through the gate policy (`GatePolicy.Execute`) — a checkpoint → mutate → validate → delta cycle. Read-only tools bypass the gate entirely.

**Modes:**

- `off` (default for every tool) — the mutation runs directly; no checkpoint/validate overhead. The gate outcome is `skipped`.
- `enforce` — the cycle runs. New errors introduced by the mutation fail the gate (`outcome: failed`, `failed: true`). The mutation still ran — the gate outcome is informational unless a caller rolls back.
- `warn` — the cycle runs but never hard-fails; new errors surface as `outcome: warned`.

**`paths_hint` is required** when the gate is `enforce`/`warn`. An empty hint is rejected with `paths_hint_required` before the main-thread hop (no whole-project fallback).

**Outcome is surfaced inside `result`.** The gate block is prepended into the handler's JSON output object so the canonical `{ok, result, error}` envelope stays stable:

```json
{
  "ok": true,
  "result": {
    "gate": {
      "mode": "enforce",
      "outcome": "passed",
      "ran": true,
      "failed": false,
      "checkpointId": "cp_ab12cd",
      "categoriesRun": ["broken_references", "missing_scripts", "import_health"],
      "checkpointMs": 3, "validateMs": 5, "totalMs": 9,
      "delta": { "newErrors": 0, "newWarnings": 0, "resolvedErrors": 0, "resolvedWarnings": 0 },
      "agentNextSteps": ["Gate passed — no new issues detected."]
    },
    "...handler output fields..."
  }
}
```

When the gate did not run (`off` / read-only tool), only `mode`, `outcome` (`skipped`), `ran` (`false`), and `failed` are emitted. A failed mutation surfaces as `ok:false` + `error` with no gate block (the mutation itself faulted).

### Gate meta-tools (explicit workflow)

Three read-only tools expose the gate's checkpoint / validate / delta steps directly, so an agent can run the safety workflow across separate tool calls instead of relying on the per-mutation implicit cycle:

- `godot_open_mcp_validate_edit` — scoped verify pass over `res://` paths. Returns `passed` (strict on any `Error` severity), `issues[]` (ruleId/categoryId, severity, code/issueCode, assetPath, description, optional evidence, `fixCandidates[]` / `fixId`+`fixSafe`), `categoriesRun`, `rulesApplied`, `durationMs`. Optional `categories` narrows the rule set; an unknown rule id returns a structured `error.code:unknown_rule` body (the tool still succeeds).
- `godot_open_mcp_checkpoint_create` — captures a project-health baseline into a session-scoped in-memory store. Returns `checkpointId` (the resume key), `timestamp`, and a per-rule `fingerprint` map of `errors` / `warnings` / `issueKeys[]`. Recommended `paths` scope the baseline; an empty/absent array yields a baseline of nothing.
- `godot_open_mcp_delta` — compares current state against a stored checkpoint. Returns `passed` (strict on new errors), a `summary` of new/resolved counts, and `newIssues[]` / `resolvedIssues[]` (canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` keys). **Session-safe recovery:** a checkpoint that is no longer in the store (cleared on script recompile, assembly reload, or editor restart) returns `passed:true` + `unavailable:true` + `agentNextSteps[]` — NOT a hard error — so the agent can fall back to `validate_edit`.

These bypass the gate dispatch path (they are read-only) and surface their JSON output verbatim as the `result` field.

## Auth (deferred to P5.2)

A per-session bearer token is minted into the instance lock on bridge start. The MCP server attaches `Authorization: Bearer <token>` to every request when present. Enforcement is opt-in via `authMode` in project settings (`"none"` default | `"required"`). In P2.1 the bridge does not enforce auth.

## Source of truth

- HTTP routing + dispatch: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- Envelope builders: `packages/bridge/Editor/Bridge/BridgeEnvelope.cs`
- Request-body parsing: `packages/bridge/Editor/Bridge/BridgeRequestBody.cs`
- Gate policy (checkpoint → mutate → validate → delta): `packages/bridge/Editor/Gate/GatePolicy.cs`
- Verify adapter (checkpoint/validate/delta over the verify package): `packages/bridge/Editor/Gate/VerifyGateAdapter.cs`
- Gate default + precedence: `packages/bridge/Editor/Gate/GateDefaultPolicy.cs`
- Checkpoint store (session-scoped baseline for the explicit meta-tools): `packages/bridge/Editor/Gate/CheckpointStore.cs`
- Gate meta-tools (`validate_edit` / `checkpoint_create` / `delta` handlers + registration): `packages/bridge/Editor/MetaTools/`
- Tool registry: `packages/bridge/Editor/Tools/BridgeToolRegistry.cs`
- Dispatch result model: `packages/bridge/Editor/Tools/ToolDispatchResult.cs`
- MCP-side client (envelope unwrap): `mcp-server/src/live-client.ts`
