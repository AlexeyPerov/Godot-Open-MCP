# MCP tool catalog

This document covers the MCP tool catalog, the tool families, and the route policy each tool follows. For the bridge HTTP contract (`/ping`, `/tools/*`), see `bridge-http.md`.

## Tool inventory

Every tool is defined in `mcp-server/src/tools/{name}.ts` and registered in `ALL_TOOLS` (`mcp-server/src/tools/index.ts`). The bridge-side handlers live in `packages/bridge/Editor/Tools/` and `packages/bridge/Editor/MetaTools/`; the MCP-side `LiveClient` routes each CallTool to `POST /tools/{name}` on the resolved bridge port.

Tool names follow the `godot_open_mcp_*` convention (ADR-003).

### Tool families

| Family | Tools | Mutating | Notes |
|---|---|---|---|
| core | `ping`, `validate_edit`, `checkpoint_create`, `delta`, `apply_fix`, `capabilities` | `apply_fix` only | Always visible in `ListTools`. |
| node | `node_find`, `node_create`, `node_modify`, `node_set_parent`, `node_duplicate`, `node_delete` | create/modify/set-parent/duplicate/delete | Scene-tree operations. |
| scene | `scene_open`, `scene_save`, `scene_list_opened`, `scene_get_data`, `scene_create` | open/save/create | Scene lifecycle + read. |

## Route policy

| Route | Meaning | Tools |
|---|---|---|
| **live** | The CallTool handler POSTs to the bridge; the bridge handler runs on the editor main thread. | Most tools (node, scene, gate meta-tools, `apply_fix`). |
| **local** | The CallTool handler resolves the response in-process — no bridge hop. | `godot_open_mcp_capabilities`. |

`capabilities` is the one local-only tool: its response is built in-process from `ALL_TOOLS` + the rule/fix catalog via `buildCapabilities`. It never POSTs to the bridge (mirroring Unity Open MCP's tool-router `routeCapabilities`).

## `godot_open_mcp_capabilities`

Discover the full capability surface in one call.

**Input:**
- `kind` (optional) — `tools` | `rules` | `fixes`. Filter to a single surface; omit for all three.
- `include_planned` (optional, default `true`) — include planned-but-unbuilt capabilities (`status: "planned"`). The v1 catalog has no planned entries yet.

**Result:**

```json
{
  "tools": [{ "name": "godot_open_mcp_ping", "implemented": true, "status": "implemented", "description": "Bridge health check." }],
  "rules": [{
    "id": "missing_scripts",
    "title": "Missing scripts",
    "description": "...",
    "applicableAssetKinds": ["scene", "resource"],
    "applicableExtensions": [".tscn", ".tres"],
    "implemented": true,
    "status": "implemented",
    "issues": [{ "code": "missing_script", "severity": "Error", "fixIds": ["remove_missing_script"] }]
  }],
  "fixes": [{
    "id": "remove_missing_script",
    "implemented": true,
    "status": "implemented",
    "rules": ["missing_scripts"],
    "issueCodes": ["missing_script"],
    "safe": true
  }],
  "counts": { "toolsImplemented": 17, "toolsPlanned": 0, "rulesImplemented": 3, "rulesPlanned": 0, "fixesImplemented": 1, "fixesPlanned": 0 }
}
```

Each capability carries an `implemented` boolean; planned-but-unbuilt items return with `status: "planned"` and actionable `guidance` instead of failing.

### Rule + fix catalog

The `rules[]` and `fixes[]` arrays mirror the C# verify package and MUST stay in sync on every rule/fix change (per `packages/verify/AGENTS.md` §Capability catalog sync). The drift-detection tests in `mcp-server/src/capabilities/rule-catalog.test.ts` pin the issue codes, severities, and fix mappings against the C# constants:

| Rule id | Issue code | Severity | Fix |
|---|---|---|---|
| `broken_references` | `broken_scene_reference` | Error | — |
| `missing_scripts` | `missing_script` | Error | `remove_missing_script` |
| `import_health` | `orphan_import` | Warning | — |
| `import_health` | `duplicate_uid` | Error | — |

| Fix id | Resolves | Safe |
|---|---|---|
| `remove_missing_script` | `missing_scripts` / `missing_script` | true |

The catalog source of truth is `mcp-server/src/capabilities/rule-catalog.ts`; the builder is `mcp-server/src/capabilities/build-capabilities.ts`. There is no planned-rule surface yet — when a rule is stubbed but not built, add it with `implemented:false` + `guidance` so agents get a structured "not yet available" signal.

## Source of truth

- Tool definitions: `mcp-server/src/tools/`
- Capabilities builder + catalog: `mcp-server/src/capabilities/`
- CallTool routing (local vs live): `mcp-server/src/index.ts`
- MCP-side client (live POST + envelope unwrap): `mcp-server/src/live-client.ts`
