/**
 * AI Setup wizard service — pure env/entry builders (for instant preview +
 * parity tests) and typed `invoke` wrappers over the wizard backend commands.
 *
 * The pure builders mirror the CLI's `setup-mcp` stdio descriptor byte-for-byte
 * (server key `godot-open-mcp`, `GODOT_PROJECT_PATH` env, npx vs local node,
 * never a `url`/`type:"http"` entry). The backend writer produces the same
 * shape; a golden fixture test (`ai_toolkit.test.ts`) pins the two together.
 */

import { invoke } from "@tauri-apps/api/core";
import type { McpSource } from "./mcp_entry.ts";

// Re-export the dep-free pure builders (defined in mcp_entry.ts so the parity
// test can import them under `node --test` without Tauri packages installed).
export {
  buildMcpEnv,
  buildStdioProps,
  buildBareEntry,
  type StdioProps,
  type McpSource,
} from "./mcp_entry.ts";

export interface McpInput {
  projectPath: string;
  agentId: string;
  source: McpSource;
  packageVersion?: string | null;
  monorepoPath?: string | null;
  overridePort?: number | null;
  configPath?: string | null;
}

// ── backend result types (mirror src-tauri/src/config/*) ─────────────────────

export interface AgentInfo {
  id: string;
  name: string;
  scope: "project" | "global";
  configPathDisplay: string;
}

export interface AddonState {
  installed: boolean;
  enabled: boolean;
  path?: string;
}
export interface ClientMcpState {
  clientId: string;
  configured: boolean;
  path?: string;
}
export interface NodeState {
  ok: boolean;
  version?: string;
  error?: string;
}
export interface ProjectState {
  projectPath: string;
  isGodotProject: boolean;
  addon: AddonState;
  mcp: ClientMcpState[];
  node: NodeState;
  bridge: { port: number };
}

export interface McpPlan {
  agentId: string;
  configPath: string;
  serverName: string;
  transport: "stdio";
  entry: Record<string, unknown>;
  changed: boolean;
  wrote: boolean;
  warnings?: string[];
}

export interface InstallResult {
  ok: boolean;
  changed: boolean;
  addonDir: string;
  projectGodotPath: string;
  pluginPath: string;
  enabledPlugins: string[];
  errorLabel?: string;
  warnings?: string[];
}

export interface LaunchResult {
  ok: boolean;
  editorPath?: string;
  pid?: number;
  errorLabel?: string;
  message?: string;
}

export interface PingResult {
  status: "ready" | "compiling" | "offline" | "error" | "timeout" | "dead_bridge";
  port: number;
  baseUrl: string;
  body?: unknown;
  ready: boolean;
}

export interface ClearResult {
  ok: boolean;
  clients: { clientId: string; removed: boolean; configPath: string }[];
  pluginDisabled: boolean;
  warnings?: string[];
}

export interface SkillCopyResult {
  ok: boolean;
  copied: boolean;
  dest?: string;
  message?: string;
}

// ── invoke wrappers ───────────────────────────────────────────────────────────

export function listAgents(): Promise<AgentInfo[]> {
  return invoke<AgentInfo[]>("list_agents");
}

export function detectProjectState(
  projectPath: string,
  clientIds: string[],
): Promise<ProjectState> {
  return invoke<ProjectState>("detect_project_state", { projectPath, clientIds });
}

export function planMcpConfig(input: McpInput): Promise<McpPlan> {
  return invoke<McpPlan>("plan_mcp_config", { input });
}

export function writeMcpConfig(input: McpInput): Promise<McpPlan> {
  return invoke<McpPlan>("write_mcp_config", { input });
}

export function installAddon(
  projectPath: string,
  source: string | null,
  monorepoPath: string | null,
): Promise<InstallResult> {
  return invoke<InstallResult>("install_addon", { projectPath, source, monorepoPath });
}

export function launchGodot(
  projectPath: string,
  editorPath: string | null,
): Promise<LaunchResult> {
  return invoke<LaunchResult>("launch_godot", { projectPath, editorPath });
}

export function pollBridgePing(
  projectPath: string,
  overridePort?: number | null,
): Promise<PingResult> {
  return invoke<PingResult>("poll_bridge_ping", {
    projectPath,
    overridePort: overridePort ?? null,
  });
}

export function clearAiSetup(
  projectPath: string,
  clientIds: string[],
  disablePlugin: boolean,
): Promise<ClearResult> {
  return invoke<ClearResult>("clear_ai_setup", { projectPath, clientIds, disablePlugin });
}

export function copySkillFiles(
  projectPath: string,
  monorepoPath: string | null,
): Promise<SkillCopyResult> {
  return invoke<SkillCopyResult>("copy_skill_files", { projectPath, monorepoPath });
}
