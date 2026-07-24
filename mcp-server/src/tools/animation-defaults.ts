// `godot_open_mcp_animation_defaults` tool definition (P12.4).
//
// First tool of the animation domain pack. A pure helper — returns the recommended
// starter Animation length + loop mode as a JSON object an agent can spread into
// animation_create. No scene required; no gate surface. The handler lives in the
// bridge (POST /tools/godot_open_mcp_animation_defaults); this file is the catalog
// metadata only — name / description / input schema — advertised to AI clients
// over stdio ListTools.
//
// Adapted from Unity Open MCP's animation envelope (adapt fidelity): Unity ships no
// standalone defaults tool — its AnimationClip create carries inline defaults an
// agent has to infer. The Godot catalog surfaces defaults as its own tool so an
// agent gets a single-call probe of the animation authoring surface it can then
// spread into animation_create.
//
// This is an `animation` group tool — it is hidden from ListTools until an agent
// activates the group via `godot_open_mcp_manage_tools({ action: "activate",
// group: "animation" })`.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const animationDefaults: Tool = {
  name: "godot_open_mcp_animation_defaults",
  description:
    "Return the recommended starter length (1.0s) and loop mode (\"none\") for an Animation clip " +
    "as a JSON object an agent can spread into animation_create. Pure helper — no scene required, " +
    "no mutation. Animation is dimension-agnostic (one class), so there is no dimension arg. The " +
    "loop mode names mirror Godot's Animation.LoopMode enum minus the redundant \"Loop\" prefix " +
    "(none / linear / pingpong). Spread the result into animation_create, or use the values as " +
    "guidance. This is an `animation` group tool — activate the group with manage_tools first. " +
    "Read-only.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
