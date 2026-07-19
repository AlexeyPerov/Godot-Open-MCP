// App identity constants. The version is kept in lockstep with
// `src-tauri/tauri.conf.json` and `package.json` — the repo version-sync
// script (`scripts/sync-version.mjs`) is the source of truth once the Hub
// joins the shared trio; until then, bump all three together.

export const APP_NAME = "Godot Open MCP Hub";
export const APP_VERSION = "0.0.1";

/** MCP server key written into every client config (never renamed). */
export const MCP_SERVER_NAME = "godot-open-mcp";
