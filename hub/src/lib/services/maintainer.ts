/**
 * Maintainer panel service (P11.3) — typed `invoke` wrappers over the npm
 * command runner. All npm commands run in `mcp-server/` for OpenMcp repos
 * (resolved in Rust via `resolve_npm_cwd`); version sync runs from the repo
 * root. Publish is confirm-gated in the UI, never one-click.
 */

import { invoke } from "@tauri-apps/api/core";

export interface McpPackageInfo {
  name: string;
  version: string;
  manifestPath: string;
}

export interface CommandLine {
  stream: "meta" | "stdout" | "stderr";
  text: string;
}

export interface CommandResult {
  ok: boolean;
  exitCode: number | null;
  cwd: string;
  argv: string;
  lines: CommandLine[];
  truncated: boolean;
}

export function readMcpPackageInfo(projectPath: string): Promise<McpPackageInfo> {
  return invoke<McpPackageInfo>("read_mcp_package_info", { projectPath });
}

/** Run an npm subcommand (e.g. ["run","build"], ["test"]). */
export function runNpmScript(projectPath: string, args: string[]): Promise<CommandResult> {
  return invoke<CommandResult>("run_npm_script", { projectPath, args });
}

/** Run `scripts/sync-version.mjs <args>` from the repo root. */
export function runVersionSync(projectPath: string, args: string[]): Promise<CommandResult> {
  return invoke<CommandResult>("run_version_sync", { projectPath, args });
}

// ── Convenience wrappers for the standard maintainer actions ──────────────────

export const npmBuild = (p: string) => runNpmScript(p, ["run", "build"]);
export const npmTest = (p: string) => runNpmScript(p, ["test"]);
export const npmPublishDryRun = (p: string) =>
  runNpmScript(p, ["publish", "--dry-run", "--access", "public"]);
export const npmPublish = (p: string) => runNpmScript(p, ["publish", "--access", "public"]);
export const versionSyncCheck = (p: string) => runVersionSync(p, ["--check"]);
export const versionSyncWrite = (p: string) => runVersionSync(p, []);
