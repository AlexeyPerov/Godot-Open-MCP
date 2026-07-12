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
| `unauthorized` | 401 | Missing or invalid `Authorization` header under `authMode:"required"` (see §Auth). |
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

Three read-only tools expose the gate's checkpoint / validate / delta steps directly, so an agent can run the safety workflow across separate tool calls instead of relying on the per-mutation implicit cycle. A fourth mutating tool (`apply_fix`) applies structured remediations for the issues those checks surface:

- `godot_open_mcp_validate_edit` — scoped verify pass over `res://` paths. Returns `passed` (strict on any `Error` severity), `issues[]` (ruleId/categoryId, severity, code/issueCode, assetPath, description, optional evidence, `fixCandidates[]` / `fixId`+`fixSafe`), `categoriesRun`, `rulesApplied`, `durationMs`. Optional `categories` narrows the rule set; an unknown rule id returns a structured `error.code:unknown_rule` body (the tool still succeeds).
- `godot_open_mcp_checkpoint_create` — captures a project-health baseline into a session-scoped in-memory store. Returns `checkpointId` (the resume key), `timestamp`, and a per-rule `fingerprint` map of `errors` / `warnings` / `issueKeys[]`. Recommended `paths` scope the baseline; an empty/absent array yields a baseline of nothing.
- `godot_open_mcp_delta` — compares current state against a stored checkpoint. Returns `passed` (strict on new errors), a `summary` of new/resolved counts, and `newIssues[]` / `resolvedIssues[]` (canonical `{ruleId}|{severity}|{assetPath}|{issueCode}` keys). **Session-safe recovery:** a checkpoint that is no longer in the store (cleared on script recompile, assembly reload, or editor restart) returns `passed:true` + `unavailable:true` + `agentNextSteps[]` — NOT a hard error — so the agent can fall back to `validate_edit`.
- `godot_open_mcp_apply_fix` — apply (or preview) a structured fix for a canonical issue id. **`dry_run` defaults to true** (preview only); pass `dry_run:false` to apply. Omit `fix_id` to list every fix that can resolve the issue (safe vs unsafe); set it to preview or apply a specific fix.

The first three bypass the gate dispatch path (they are read-only) and surface their JSON output verbatim as the `result` field. `apply_fix` is mutating: a dry-run apply bypasses the gate (it mutates nothing), while a non-dry-run apply routes through `ApplyFixGateRunner` (gate + rollback).

#### `apply_fix` dispatch and rollback

`apply_fix` has two dispatch shapes:

- **Dry-run** (`dry_run:true`, the default) — a preview: returns the fix description + `safe` flag (`fix_id` set), or the list of fix ids that can resolve the issue (`fix_id` omitted). No project change, so it bypasses the gate like a read-only tool. `paths_hint` is not required for a dry-run.
- **Non-dry-run** (`dry_run:false`) — the real apply. Routes through `ApplyFixGateRunner`, which snapshots the issue's asset file before the fix, runs the checkpoint → apply → validate → delta cycle, then **rolls back** if the fix failed OR the gate detected new errors under `enforce`. `paths_hint` is required under enforce/warn (the gate checkpoints it for rollback).

A rolled-back apply surfaces a top-level `rollback` block in the result so an agent can tell "the fix ran and stuck" from "the fix was undone — no project change remains":

```json
{
  "ok": true,
  "result": {
    "gate": { "mode": "enforce", "outcome": "failed", "ran": true, "failed": true, "delta": { "newErrors": 1, "...": "..." }, "agentNextSteps": ["..."] },
    "rollback": { "rolledBack": true, "reason": "fix introduced 1 new error(s) under enforce — restored touched files to pre-fix state", "restoredPaths": ["res://Main.tscn"] },
    "dryRun": false,
    "success": true,
    "description": "Removed broken script attachment from 'res://Main.tscn'.",
    "touchedPaths": ["res://Main.tscn"]
  }
}
```

Structured errors (all `ok:false` with a stable `error.code`): `missing_parameter` (empty `issue_id`), `invalid_issue_id` (malformed key), `fix_not_applicable` (provider cannot fix this issue), `fix_failed` (`Apply` returned failure), `fix_error` (`Apply` threw). An unknown `fix_id` is an `ok:true` body with `error.code:unknown_fix` listing `availableFixIds` + `applicableFixIdsForIssue` so the agent can self-correct without a second round-trip.

The initial `Safe:true` provider is `remove_missing_script` — resolves `missing_scripts|missing_script` by removing the broken `script = ExtResource("id")` attachment from a `.tscn`/`.tres` (and dropping the orphaned `[ext_resource]` declaration when no node still uses it). More providers register through `FixProviderRegistry`.

## Auth

The bridge mints a 256-bit per-session bearer token on start and writes it into the instance lock (`authToken` field). The MCP server auto-discovers the token from the lock file (`~/.godot-open-mcp/instances/<sha256(projectPath)>.json`) and attaches `Authorization: Bearer <token>` to every request when present; when the token is absent (older bridge, or an explicit `GODOT_OPEN_MCP_BRIDGE_PORT` override with no lock) no header is sent. The token is **always** minted — even when auth is off — so flipping the mode needs no restart.

Enforcement is opt-in via `authMode` in project settings (`<project>/.godot-open-mcp/settings.json`):

```json
{ "authMode": "none", "bindAddress": "127.0.0.1" }
```

**Modes:**

| `authMode` | Request `Authorization` | Outcome |
|---|---|---|
| `none` (default) | any / missing | **Allow** — preserves the localhost-trust behavior. |
| `required` | `Bearer <correct token>` | **Allow**. |
| `required` | missing / wrong / non-Bearer | **401 `unauthorized`** (see the failure table above). |
| `<unknown>` (typo / corrupt file) | any | **401 — fail closed.** An unrecognized mode is never coerced to `none`. |

The auth check runs **before routing**, so every endpoint (`/ping`, `/tools/*`, future `/instance` / `/events`) is gated equally — no exemption. Token comparison is constant-time. Under `none` the gate is a single pure-function call returning `true`, so the common path is unaffected.

When a request is denied the bridge returns HTTP 401 with:

```json
{ "error": { "code": "unauthorized", "message": "Missing or invalid Authorization header. Set authMode to \"none\" in .godot-open-mcp/settings.json, or send Authorization: Bearer <token>." } }
```

The MCP client surfaces this verbatim as a structured `unauthorized` error on both `/ping` and `/tools/*`.

### Remote bind

By default the bridge binds `127.0.0.1` (loopback only). Remote bind (`0.0.0.0`) is opt-in and **requires `authMode:"required"`** — the bridge refuses to start on a non-loopback interface without token auth so an accidental remote bind on an open network never serves unauthenticated traffic:

```json
{ "authMode": "required", "bindAddress": "0.0.0.0" }
```

| `bindAddress` | `authMode` | Start |
|---|---|---|
| `127.0.0.1` (default) | any | Allow. |
| `0.0.0.0` | `required` | Allow. |
| `0.0.0.0` | `none` / missing / invalid | **Refuse** before listen (the bridge stays down with an actionable error). |

TLS termination, SIEM audit logging, and request deny-lists are not in scope for this phase.

## Source of truth

- HTTP routing + dispatch: `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- Auth gate (`CheckAuth`, runs before routing): `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`
- Auth primitives (token mint / Bearer parse / constant-time compare): `packages/bridge/Editor/Bridge/BridgeAuthToken.cs`
- Auth decision (policy matrix, fail-closed): `packages/bridge/Editor/Bridge/BridgeAuthCheck.cs`
- Auth mode constants: `packages/bridge/Editor/Bridge/BridgeAuthPolicy.cs`
- Project settings (`authMode` / `bindAddress` reader for `.godot-open-mcp/settings.json`): `packages/bridge/Editor/Bridge/BridgeProjectSettings.cs`
- Bind address decision (loopback always; remote requires `required`): `packages/bridge/Editor/Bridge/BridgeBindAddress.cs`
- Token mint + lock serialization (`authToken` field): `packages/bridge/Editor/Bridge/BridgeInstanceLock.cs`
- Response writers (incl. `SendUnauthorized`): `packages/bridge/Editor/Bridge/BridgeHttpResponse.cs`
- Envelope builders: `packages/bridge/Editor/Bridge/BridgeEnvelope.cs`
- Request-body parsing: `packages/bridge/Editor/Bridge/BridgeRequestBody.cs`
- Gate policy (checkpoint → mutate → validate → delta): `packages/bridge/Editor/Gate/GatePolicy.cs`
- Verify adapter (checkpoint/validate/delta over the verify package): `packages/bridge/Editor/Gate/VerifyGateAdapter.cs`
- Gate default + precedence: `packages/bridge/Editor/Gate/GateDefaultPolicy.cs`
- Checkpoint store (session-scoped baseline for the explicit meta-tools): `packages/bridge/Editor/Gate/CheckpointStore.cs`
- Gate meta-tools (`validate_edit` / `checkpoint_create` / `delta` / `apply_fix` handlers + registration): `packages/bridge/Editor/MetaTools/`
- `apply_fix` gate runner (gate + safe auto-fix rollback): `packages/bridge/Editor/MetaTools/ApplyFixGateRunner.cs`
- Fix rollback (byte-level snapshot/restore): `packages/verify/Editor/Fixes/FixRollback.cs`
- Fix providers (`remove_missing_script` + registry): `packages/verify/Editor/Fixes/`
- Tool registry: `packages/bridge/Editor/Tools/BridgeToolRegistry.cs`
- Dispatch result model: `packages/bridge/Editor/Tools/ToolDispatchResult.cs`
- MCP-side client (envelope unwrap): `mcp-server/src/live-client.ts`
