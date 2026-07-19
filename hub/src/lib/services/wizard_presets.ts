/**
 * Slim wizard presets. Unity ships a large preset catalog (domain deps, UPM
 * variants); Godot v1 needs only the MCP-source choice: the published npm
 * package via `npx`, or the local monorepo checkout via `node`.
 */

import type { McpSource } from "./ai_toolkit.ts";

export interface WizardPreset {
  id: string;
  label: string;
  description: string;
  source: McpSource;
}

export const WIZARD_PRESETS: WizardPreset[] = [
  {
    id: "npx-published",
    label: "Published package (recommended)",
    description:
      "AI clients run the MCP server with `npx godot-open-mcp`. Best for most users — no checkout required.",
    source: "npx-published",
  },
  {
    id: "local-checkout",
    label: "Local monorepo checkout",
    description:
      "AI clients run `node <repo>/mcp-server/dist/index.js`. For contributors working in the Godot Open MCP repo.",
    source: "local-checkout",
  },
];

export function presetForSource(source: McpSource): WizardPreset {
  return WIZARD_PRESETS.find((p) => p.source === source) ?? WIZARD_PRESETS[0];
}
