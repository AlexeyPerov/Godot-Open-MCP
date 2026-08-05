// `godot_open_mcp_settings_set_project` tool definition (P16.1).
//
// Writes key/value pairs within one `project.godot` section via Godot's
// `ProjectSettings.SetSetting` + `Save` API (never raw text edits — raw edits
// risk corrupting the file's formatting/escaping). The handler lives in the
// bridge (POST /tools/godot_open_mcp_settings_set_project); this file is the
// catalog metadata only — name / description / input schema — advertised to AI
// clients over stdio ListTools.
//
// Adapted from Unity Open MCP's `settings_set_player`
// (TypedTools/BuildSettingsTools.cs — adapt fidelity): same `fields[]` array of
// {key, value} patches contract and the per-key warnings accumulation shape, but
// writes route through Godot's `ProjectSettings.SetSetting` + `Save` rather than
// Unity's typed PlayerSettings setters + AssetDatabase.SaveAssets. A section
// allowlist rejects unknown sections (and "all") to prevent typos from inventing
// a bogus project.godot block. Unity-specific sections (quality tiers, scripting
// backend) are intentionally NOT ported.
//
// This is a `settings` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "settings" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const settingsSetProject: Tool = {
  name: "godot_open_mcp_settings_set_project",
  description:
    "Write key/value pairs within one project.godot section via Godot's ProjectSettings.SetSetting " +
    "+ Save API (no raw text edits — the API validates the file's formatting/escaping). Pass a " +
    "section plus a fields[] array of {key, value} patches; the handler applies each patch, " +
    "persists once with ProjectSettings.Save, and returns the applied keys plus any per-key " +
    "warnings (a bad key does NOT abort the batch — good entries still land).\n\n" +
    "Sections (writable): rendering, physics, input, layer_names, autoload, application, display. " +
    "'all' is rejected (it is a read-only summary). A relative key (no '/') is prefixed with the " +
    "section's leading segment (e.g. key 'run/main_scene' under section 'application' → " +
    "'application/run/main_scene'); an absolute key must stay inside the section (a cross-section " +
    "key is skipped with a warning). A null value clears the setting.\n\n" +
    "Mutating: runs the gate cycle by default; paths_hint is res://project.godot (the single " +
    "mutated file). This is a `settings` group tool — activate the group with manage_tools first.",
  inputSchema: {
    type: "object",
    required: ["section", "fields", "paths_hint"],
    properties: {
      section: {
        type: "string",
        enum: [
          "rendering",
          "physics",
          "input",
          "layer_names",
          "autoload",
          "application",
          "display",
        ],
        description:
          "Which writable project.godot section to write. 'all' is intentionally absent — it is a " +
          "read-only summary switch, not a writable domain. The section scopes the write and " +
          "prefixes any relative key.",
      },
      fields: {
        type: "array",
        minItems: 1,
        items: {
          type: "object",
          required: ["key"],
          properties: {
            key: {
              type: "string",
              description:
                "The ProjectSettings property path. A relative key (no '/') is prefixed with the " +
                "section's leading segment; an absolute key must stay inside the section. Examples: " +
                "'run/main_scene' under section 'application' → application/run/main_scene; " +
                "'common/physics_ticks_per_second' under section 'physics'; " +
                "'3d/physics/default_gravity' under section 'physics'.",
            },
            value: {
              description:
                "The value to write — any JSON type (string / number / boolean / null / object / " +
                "array). Godot re-parses the token into a Variant and stores it via SetSetting, so " +
                "a Color is an {r,g,b[,a]} object, a Vector3 is an {x,y,z} object, etc. A null value " +
                "clears the setting. Omit the key entirely to clear.",
            },
          },
          additionalProperties: false,
        },
        description:
          "Non-empty array of {key, value} patches. Each patch is applied independently; per-key " +
          "failures (unparseable value, write failure) accumulate as warnings and do NOT abort the " +
          "batch. An entirely empty applied set surfaces no_applicable_keys.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the project file. Pass [\"res://project.godot\"]. Mandatory even when " +
          "gate is 'off' (handler-level guard).",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → mutate → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
    },
    additionalProperties: false,
  },
};
