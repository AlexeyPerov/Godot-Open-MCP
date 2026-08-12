// MCP resource router (P18.2).
//
// Routes `resources/read` URIs to read-only handlers that wrap existing tool /
// scanner outputs — no new business logic. The catalog (`resources/index.ts`)
// advertises four URIs; this module owns the per-URI payload composition.
//
//   `godot-open-mcp://health/summary`   — fresh offline whole-project scan.
//   `godot-open-mcp://health/baseline`  — last baseline file on disk.
//   `godot-open-mcp://bridge/status`    — mirrors godot_open_mcp_bridge_status.
//   `godot-open-mcp://tool-groups`      — static tool-group catalog.
//
// Route: **local/live hybrid**. health/summary + health/baseline + tool-groups
// are local (disk scan, disk read, static catalog); bridge/status performs one
// /ping probe against the resolved bridge endpoint. Read-only — none mutate,
// none spawn Godot.
//
// Adapted from Unity Open MCP's `mcp-server/src/resource-router.ts` (copy
// fidelity for the router shape + the URI→handler switch + the
// `toTextResource` / `noDataSummary` helpers). Intentional deltas:
//   - health/summary runs the Godot offline scanner (`scanProjectOffline`)
//     instead of reading a bridge-cached verify summary — Godot has no such
//     bridge endpoint and no cached summary, so the resource re-runs the same
//     scan the baseline/regression tools use.
//   - health/baseline reuses `loadBaseline` (schema-validated, never throws)
//     and surfaces `generatedAt` as `asOf` rather than the file mtime.
//   - bridge/status delegates to `LiveClient.routeBridgeStatus()` and passes
//     its body through verbatim — Godot has no ping cache, so each read does a
//     fresh probe and the resource mirrors the tool output exactly (zero
//     payload drift; share logic, not reimplementation).
//   - tool-groups omits Unity's `domainDefine` / `unityPackage` fields (Godot
//     has no bridge compile inventory for packs).

import { resolve } from "node:path";
import type { ReadResourceResult } from "@modelcontextprotocol/sdk/types.js";
import type { LiveClient } from "../live-client.js";
import { TOOL_GROUPS, DEFAULT_ENABLED_GROUPS } from "../capabilities/tool-groups.js";
import { scanProjectOffline } from "../baseline/scan.js";
import {
  BASELINE_SCHEMA_VERSION,
  buildBaseline,
  loadBaseline,
  normalizeProfile,
} from "../baseline/baseline-schema.js";

/** Default baseline path (mirrors godot_open_mcp_baseline_create's default). */
const DEFAULT_BASELINE_PATH = "CI/godot-open-mcp-baseline.json";

export interface ResourceHandlerDeps {
  live: LiveClient;
  projectPath: string;
  /** Resolved bridge port — informational, surfaced in bridge/status payloads. */
  port: number;
}

/** Wrap any JSON-serializable payload as a single text resource content block. */
function toTextResource(uri: string, json: unknown): ReadResourceResult {
  return {
    contents: [
      {
        uri,
        mimeType: "application/json",
        text: JSON.stringify(json),
      },
    ],
  };
}

// ---------------------------------------------------------------------------
// health/summary — fresh offline whole-project scan
// ---------------------------------------------------------------------------

/**
 * Run the offline whole-project scanner (`scanProjectOffline`) and return the
 * severity summary. The scanner never throws — unreadable files are skipped
 * silently (a partial scan is better than failing the resource read). There is
 * no cached health summary in Godot today, so each read re-runs the scan; the
 * `asOf` timestamp is the moment the scan ran and `durationMs` is the scan's
 * wall-clock cost so a client can decide whether to poll.
 */
export async function handleHealthSummary(
  deps: ResourceHandlerDeps,
): Promise<ReadResourceResult> {
  const scan = await scanProjectOffline({ projectRoot: deps.projectPath });
  // buildBaseline aggregates the severity summary from the flat issue list —
  // the same path godot_open_mcp_baseline_create uses. The profile is
  // informational only; `desktop` matches the baseline default.
  const baseline = buildBaseline(
    scan.issues,
    scan.categoriesRun,
    scan.ciExcludedRules,
    normalizeProfile("desktop"),
  );

  return toTextResource("godot-open-mcp://health/summary", {
    status: "ok",
    asOf: new Date().toISOString(),
    source: "offline_scan",
    scanner: "offline",
    summary: baseline.summary,
    categoriesRun: scan.categoriesRun,
    ciExcludedRules: scan.ciExcludedRules,
    scannedFileCount: scan.scannedFiles.length,
    durationMs: scan.durationMs,
    nextStep:
      "Compare against a baseline with godot_open_mcp_regression_check, or " +
      "snapshot one with godot_open_mcp_baseline_create. The live editor " +
      "(validate_edit) can surface rules the offline scan CI-excludes.",
  });
}

// ---------------------------------------------------------------------------
// health/baseline — last baseline file on disk
// ---------------------------------------------------------------------------

/**
 * Load + validate the baseline file (`loadBaseline`, never throws) and surface
 * its summary. `no_baseline` when the file is absent; `invalid_baseline` when
 * it fails schema validation. `asOf` is the baseline's in-file `generatedAt`
 * (when the scan ran), not the file mtime.
 */
export async function handleHealthBaseline(
  deps: ResourceHandlerDeps,
): Promise<ReadResourceResult> {
  const baselinePath = resolve(deps.projectPath, DEFAULT_BASELINE_PATH);
  const loaded = await loadBaseline(baselinePath);

  if (!loaded.ok) {
    if (loaded.reason === "missing") {
      return toTextResource("godot-open-mcp://health/baseline", {
        status: "no_baseline",
        asOf: null,
        baselinePath: DEFAULT_BASELINE_PATH,
        nextStep:
          "Run godot_open_mcp_baseline_create to snapshot a baseline, then " +
          "compare future scans with godot_open_mcp_regression_check.",
      });
    }
    return toTextResource("godot-open-mcp://health/baseline", {
      status: "invalid_baseline",
      asOf: null,
      baselinePath: DEFAULT_BASELINE_PATH,
      message: loaded.message,
      nextStep:
        "Regenerate the baseline with godot_open_mcp_baseline_create " +
        "(schema version or shape drifted).",
    });
  }

  const baseline = loaded.baseline;
  return toTextResource("godot-open-mcp://health/baseline", {
    status: "ok",
    asOf: baseline.generatedAt || null,
    baselinePath: DEFAULT_BASELINE_PATH,
    schemaVersion: baseline.schemaVersion,
    platformProfile: baseline.platformProfile,
    scanner: baseline.scanner,
    summary: baseline.summary,
    ciExcludedRules: baseline.ciExcludedRules,
    ruleCount: baseline.rules.length,
  });
}

// ---------------------------------------------------------------------------
// bridge/status — mirrors godot_open_mcp_bridge_status
// ---------------------------------------------------------------------------

/**
 * Delegate to `LiveClient.routeBridgeStatus()` and pass its body through
 * verbatim. Godot has no ping cache, so each read performs one fresh /ping
 * probe (+ the instance-lock classification the tool already does) and the
 * resource payload is byte-identical to the tool output — zero drift. An
 * offline bridge reports `stopped` / `unreachable` / `dead_bridge` (those ARE
 * the answer), never an exception. The tool never sets `isError` for offline
 * states; the defensive branch below only guards a hypothetical programmer
 * mistake so a resource read can never surface a raw MCP error shape.
 */
export async function handleBridgeStatus(
  deps: ResourceHandlerDeps,
): Promise<ReadResourceResult> {
  const result = await deps.live.routeBridgeStatus();

  const first = result.content[0];
  if (
    !result.isError &&
    first &&
    first.type === "text" &&
    typeof first.text === "string"
  ) {
    // Pass the tool body through verbatim — single canonical source of truth.
    let parsed: unknown = first.text;
    try {
      parsed = JSON.parse(first.text);
    } catch {
      parsed = first.text;
    }
    return toTextResource("godot-open-mcp://bridge/status", parsed);
  }

  // Defensive: a non-JSON / error shape should never happen (routeBridgeStatus
  // never throws or sets isError for offline states), but keep the resource
  // contract read-only and exception-free regardless.
  return toTextResource("godot-open-mcp://bridge/status", {
    status: "no_data",
    asOf: new Date().toISOString(),
    bridgePort: deps.port,
    connected: false,
    nextStep:
      "Call godot_open_mcp_bridge_status directly for the full recovery hint.",
  });
}

// ---------------------------------------------------------------------------
// tool-groups — static catalog
// ---------------------------------------------------------------------------

/**
 * Static tool-group catalog (group ids, descriptions, default-enabled flags).
 * The same catalog `godot_open_mcp_capabilities` embeds; surfaced as a
 * resource so a client can discover the group vocabulary before the first
 * domain tool call. For the per-tool roster, call
 * `godot_open_mcp_capabilities`; for session activation state, call
 * `godot_open_mcp_manage_tools(action="list_groups")`.
 */
export function handleToolGroups(): ReadResourceResult {
  return toTextResource("godot-open-mcp://tool-groups", {
    status: "ok",
    groups: TOOL_GROUPS.map((g) => ({
      id: g.id,
      description: g.description,
      defaultEnabled: g.defaultEnabled,
    })),
    defaultEnabledGroups: Array.from(DEFAULT_ENABLED_GROUPS).sort(),
    usageHint:
      "Call godot_open_mcp_manage_tools(action=\"list_groups\") for session " +
      "activation state; call godot_open_mcp_capabilities for the full " +
      "per-tool roster per group.",
  });
}

// ---------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------

/**
 * Routes a `resources/read` URI to its handler. Unknown URIs return a
 * `no_data` payload (never throw) so a malformed client cannot kill the server
 * process — the same never-throw contract `handleCallTool` upholds.
 */
export class ResourceRouter {
  private deps: ResourceHandlerDeps;

  constructor(deps: ResourceHandlerDeps) {
    this.deps = deps;
  }

  async read(uri: string): Promise<ReadResourceResult> {
    switch (uri) {
      case "godot-open-mcp://health/summary":
        return handleHealthSummary(this.deps);
      case "godot-open-mcp://health/baseline":
        return handleHealthBaseline(this.deps);
      case "godot-open-mcp://bridge/status":
        return handleBridgeStatus(this.deps);
      case "godot-open-mcp://tool-groups":
        return handleToolGroups();
      default:
        return toTextResource(uri, {
          status: "no_data",
          error: `Unknown resource URI: ${uri}`,
        });
    }
  }
}

export { DEFAULT_BASELINE_PATH };
