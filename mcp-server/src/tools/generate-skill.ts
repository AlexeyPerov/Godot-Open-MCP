// `godot_open_mcp_generate_skill` tool definition (P15.5).
//
// Generate a project-specific agent skill file (SKILL.md) that reflects the
// ACTUAL project state: Godot version, enabled editor plugins (including the
// bridge/verify addons), autoloads, available tools + verify rules + fixes,
// and the key Godot types discovered in the project (`class_name` declarations
// + C# Node/Resource subclasses). Set `write:true` to persist the file into
// one or more client skill directories derived from `skills/client-paths.json`;
// the default (`write:false`) returns the content as a string for preview.
//
// The generated content is a project-inventory section MERGED with the
// canonical playbook (`skills/godot-open-mcp/SKILL.md`) — the playbook stays
// hand-authored and is never overwritten. Regenerate after plugin or script
// changes to keep the skill current.
//
// Adapted from Unity Open MCP's `mcp-server/src/tools/generate-skill.ts`
// (adapt for the description + the `write` / `clients` / `include_workflow`
// schema shape). Intentional deltas:
//   - Godot surface: `project.godot` + `class_name` / `@tool` / Node-Resource
//     subclass scanning, not Unity's ProjectVersion.txt + manifest.json +
//     MonoBehaviour/ScriptableObject scan.
//   - The `clients[]` enum is derived at module-load from the live manifest
//     (`knownClientKeys()`) — no hand-maintained literal array. When the
//     manifest cannot be located, the bundled fallback supplies the roster.
//
// Operator surface: no group assignment in `capabilities/tool-groups.ts`, so
// it sits in the always-visible meta-tool bucket alongside `capabilities` /
// `manage_tools` / `restart_editor`. Local-routed; mutating only when
// `write:true` (writes files under the project root). Never depends on the
// live bridge.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { knownClientKeys } from "../skill/client-paths.js";

// The `clients` enum is derived from the single-source manifest at
// `skills/client-paths.json` (via the bundled fallback when the repo tree is
// absent). Do not hand-maintain a literal array here — edit the manifest
// instead.
const CLIENT_ENUM = knownClientKeys();
const clientListDoc = CLIENT_ENUM.map((c) => `\`${c}\``).join(", ");

export const generateSkill: Tool = {
  name: "godot_open_mcp_generate_skill",
  description:
    "Generate a project-specific agent skill file (SKILL.md) that reflects the " +
      "actual project state: Godot version, enabled editor plugins (including " +
      "the bridge/verify addons), autoloads, available tools and verify rules, " +
      "and the key Godot types discovered in the project (class_name " +
      "declarations + C# Node/Resource subclasses). Set write=true to persist " +
      "the file into the client skill directories derived from " +
      "skills/client-paths.json. The generated content is a project-inventory " +
      "section MERGED with the canonical playbook (the hand-authored " +
      "skills/godot-open-mcp/SKILL.md) — the playbook is never overwritten. " +
      "Regenerate after plugin or script changes to keep the skill current. " +
      "Local-routed: no bridge round-trip — reads project.godot + the " +
      "capability catalog + the project type scan entirely in the MCP process.",
  inputSchema: {
    type: "object",
    properties: {
      write: {
        type: "boolean",
        default: false,
        description:
          "When true, write the generated skill file to the client skill " +
            "directories. When false (default), return the skill content as a " +
            "string for preview (no files written).",
      },
      clients: {
        type: "array",
        items: {
          enum: CLIENT_ENUM,
        },
        description:
          "Which client skill directories to write to. Only used when " +
            `write=true. Defaults to ["claude"]. Allowed values: ${clientListDoc}. ` +
            "Each entry writes to the project-relative path declared for that " +
            "client in skills/client-paths.json. Unknown keys are skipped " +
            "(never abort the whole write).",
      },
      include_workflow: {
        type: "boolean",
        default: true,
        description:
          "When true (default), compose the canonical workflow playbook " +
            "(the checked-in skills/godot-open-mcp/SKILL.md, the source of " +
            "truth for agent guidance) with a project-specific inventory " +
            "section into one file. When false, or when the template cannot " +
            "be located, emit only the standalone project inventory.",
      },
    },
    additionalProperties: false,
  },
};
