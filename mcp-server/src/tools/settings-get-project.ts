// `godot_open_mcp_settings_get_project` tool definition (P16.1).
//
// Reads one `project.godot` section via Godot's `ProjectSettings` API and
// returns its keys + values (or a per-section key-count summary when
// `section:"all"`). The handler lives in the bridge
// (POST /tools/godot_open_mcp_settings_get_project); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's `settings_get_player`
// (TypedTools/BuildSettingsTools.cs — adapt fidelity): same section-based read
// shape, but the section vocabulary is Godot's (`rendering` / `physics` /
// `input` / `layer_names` / `autoload` / `application` / `display`) and values
// are read via `ProjectSettings.GetSetting` rather than Unity's PlayerSettings
// typed properties. Unity-specific sections (quality tiers, scripting backend)
// are intentionally NOT ported.
//
// This is a `settings` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "settings" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const settingsGetProject: Tool = {
  name: "godot_open_mcp_settings_get_project",
  description:
    "Read one project.godot section via Godot's ProjectSettings API. Returns the section's keys " +
    "and their current values (serialized to JSON by Godot's own Json.Stringify so every Variant " +
    "type — Color, Vector2/3, Dictionary, Array — round-trips). Pass section:\"all\" for a " +
    "per-section key-count summary (no per-key values) so you can pick which section to read in " +
    "full without dumping the whole file.\n\n" +
    "Sections (Godot project.godot first path segment): rendering, physics, input, layer_names, " +
    "autoload, application, display. Unknown sections return invalid_parameter. No scene required; " +
    "no mutation. This is a `settings` group tool — activate the group with manage_tools first. " +
    "Read-only.",
  inputSchema: {
    type: "object",
    required: ["section"],
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
          "all",
        ],
        description:
          "Which project.godot section to read. One of the seven writable domains, or 'all' for a " +
          "per-section key-count summary. The seven domains map to the first path segment of the " +
          "ProjectSettings property path (e.g. 'rendering' covers rendering/environment/*, " +
          "rendering/anti_aliasing/*, ...). 'all' is a read-only summary switch — it is not a " +
          "writable section (settings_set_project rejects it).",
      },
    },
    additionalProperties: false,
  },
};
