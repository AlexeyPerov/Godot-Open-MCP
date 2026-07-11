// Tool registry — single export point for the stdio MCP server.
//
// Every MCP tool is defined in `src/tools/{tool-name}.ts` and added to the
// `ALL_TOOLS` array below. The registry started empty in the P1.5 scaffold;
// the first tool (`godot_open_mcp_ping`) lands here in P1.7 alongside the
// live bridge client. Subsequent phases (P2.x editor tools, P3.x gate, ...)
// append their tools to this array.
//
// Per mcp-server/AGENTS.md:
//   - tool names follow the `godot_open_mcp_*` convention,
//   - every tool definition carries `name`, `description`, `inputSchema`,
//     and a handler (the handler lives in LiveClient / tool-router; the
//     definition here is catalog metadata only),
//   - the bridge-side C# handler must stay in sync when a schema changes.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { ping } from "./ping.js";
import { nodeFind } from "./node-find.js";
import { nodeCreate } from "./node-create.js";
import { nodeModify } from "./node-modify.js";
import { nodeSetParent } from "./node-set-parent.js";
import { nodeDuplicate } from "./node-duplicate.js";
import { nodeDelete } from "./node-delete.js";
import { sceneOpen } from "./scene-open.js";
import { sceneSave } from "./scene-save.js";
import { sceneListOpened } from "./scene-list-opened.js";
import { sceneGetData } from "./scene-get-data.js";
import { sceneCreate } from "./scene-create.js";
import { validateEdit } from "./validate-edit.js";
import { checkpointCreate } from "./checkpoint-create.js";
import { delta } from "./delta.js";
import { applyFix } from "./apply-fix.js";
import { capabilities } from "./capabilities.js";
import { resourceFind } from "./resource-find.js";
import { resourceGetData } from "./resource-get-data.js";
import { resourceCreate } from "./resource-create.js";
import { resourceModify } from "./resource-modify.js";

/** Ordered list of every tool exposed over stdio MCP. */
export const ALL_TOOLS: Tool[] = [
  ping,
  nodeFind,
  nodeCreate,
  nodeModify,
  nodeSetParent,
  nodeDuplicate,
  nodeDelete,
  sceneOpen,
  sceneSave,
  sceneListOpened,
  sceneGetData,
  sceneCreate,
  // P3.6 — gate meta-tools (read-only, group core). The explicit checkpoint →
  // mutate → delta workflow surface: validate_edit for a scoped health check,
  // checkpoint_create to capture a baseline, delta to compare post-mutation.
  validateEdit,
  checkpointCreate,
  delta,
  // P3.7 — apply_fix: apply (or preview) a structured fix for a verify issue. Mutating
  // (non-dry-run applies run through the gate with safe auto-fix rollback); a dry-run
  // apply bypasses the gate. The initial Safe:true provider is remove_missing_script.
  applyFix,
  // P3.8 — capabilities: discover the full capability surface (tools + verify rules + fixes).
  // Built locally in the MCP server (no bridge hop); the CallTool handler special-cases the name.
  capabilities,
  // P4.1 — resource read tools: resource_find (exact path/UID lookup + indexed type search) and
  // resource_get_data (bounded, cycle-safe property inspection). Read-only, group resource. Live
  // route (POST /tools/{name}); offline resource reads arrive in Phase 7.
  resourceFind,
  resourceGetData,
  // P4.2 — resource mutation tools: resource_create (instantiate a Resource subclass + save) and
  // resource_modify (apply validated property-path patches + save). Mutating, group resource,
  // default gate enforce. Both write .tres/.res files to disk through ResourceSaver.
  resourceCreate,
  resourceModify,
];
