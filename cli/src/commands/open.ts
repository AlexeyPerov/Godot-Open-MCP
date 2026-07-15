// CLI command wrapper for `open`.
//
// Thin adapter over the library-safe `openProject()` (src/lib/open.ts): resolves
// argv into options, runs the opener, and maps the result union into a
// `CliCommandResult` the dispatcher prints + exits on. When `--wait` is set,
// chains into the wait-for-ready command after a successful launch so the
// one-command loop (`open --wait`) lands on a ready bridge.
//
// Exit codes: 0 on success (editor launched), 1 on failure, 3 on timeout (only
// reachable via the `--wait` chain when the bridge never becomes ready).

import * as path from "node:path";

import { EXIT } from "../exit-codes.js";
import { openProject } from "../lib/open.js";
import type {
  OpenProjectFailure,
  OpenProjectResult,
  OpenProjectSuccess,
} from "../lib/types.js";
import type { CliCommandResult } from "../commands.js";
import {
  resolveProbeTarget,
  waitForReadyInternal,
} from "./wait-for-ready.js";

export interface OpenCliInput {
  /** Resolved project root (positional `path` / `--project` / cwd). */
  projectPath: string;
  /** `--editor-path <bin>` override (skips discovery). */
  editorPath?: string;
  /** `--no-build` → false; otherwise the build runs when a .csproj exists. */
  build?: boolean;
  /** `--build-configuration <cfg>` (default Debug). */
  buildConfiguration?: string;
  /** `--wait` → chain into wait-for-ready after a successful launch. */
  wait?: boolean;
  /** Resolved bridge port override (for the `--wait` chain). */
  port?: number;
  /** wait-for-ready timeout (for the `--wait` chain). */
  timeoutMs?: number;
  /** wait-for-ready interval (for the `--wait` chain). */
  intervalMs?: number;
}

/**
 * Run the `open` command. Returns a `CliCommandResult`; the dispatcher prints
 * the JSON or human summary and exits with `result.exitCode`.
 */
export async function openCommand(
  input: OpenCliInput,
): Promise<CliCommandResult> {
  const projectPath = path.resolve(input.projectPath);

  const result = await openProject({
    projectPath,
    editorPath: input.editorPath,
    build: input.build,
    buildConfiguration: input.buildConfiguration,
  });

  if (result.kind === "failure") {
    return failureResult(result);
  }

  const success = successResult(result);

  // `--wait` chains into wait-for-ready after a successful launch. The chain
  // shares the same probe target the standalone wait-for-ready command builds.
  if (input.wait) {
    const target = resolveProbeTarget(projectPath, input.port);
    const wait = await waitForReadyInternal({
      projectPath,
      port: input.port,
      timeoutMs: input.timeoutMs,
      intervalMs: input.intervalMs,
    });
    // Merge the wait outcome into the open result: keep the open metadata, but
    // surface the wait's readiness + exit code. On timeout the open result
    // escalates to EXIT.TIMEOUT so a `open --wait` script can branch.
    return mergeWaitIntoOpen(success, target, wait);
  }

  return success;
}

/** Build the success `CliCommandResult` (human + JSON). */
function successResult(result: OpenProjectSuccess): CliCommandResult {
  const json = {
    command: "open",
    launched: result.launched,
    editorPath: result.editorPath,
    editorPid: result.editorPid ?? null,
    projectPath: result.projectPath,
    built: result.built,
    warnings: result.warnings,
  };

  const lines: string[] = [
    "Godot editor launched.",
    `Project:    ${result.projectPath}`,
    `Editor:     ${result.editorPath}`,
    `Editor PID: ${result.editorPid ?? "unknown"}`,
  ];
  if (result.built) {
    lines.push("C# build:   ran (dotnet build succeeded before launch)");
  } else {
    lines.push("C# build:   skipped (GDScript-only or --no-build)");
  }
  for (const warning of result.warnings) {
    lines.push("", `Warning: ${warning}`);
  }

  return {
    exitCode: EXIT.SUCCESS,
    json,
    human: lines.join("\n"),
  };
}

/** Build the failure `CliCommandResult` (human + JSON + errorLabel). */
function failureResult(result: OpenProjectFailure): CliCommandResult {
  const json = {
    command: "open",
    launched: false,
    projectPath: result.projectPath ?? null,
    editorPath: result.editorPath ?? null,
    error: {
      code: result.errorLabel,
      message: result.error.message,
    },
    warnings: result.warnings,
  };

  const lines: string[] = [`Failed to open project: ${result.error.message}`];
  if (result.projectPath) {
    lines.push(`Project: ${result.projectPath}`);
  }
  for (const warning of result.warnings) {
    lines.push("", `Warning: ${warning}`);
  }

  return {
    exitCode: EXIT.ERRORS,
    json,
    human: lines.join("\n"),
    errorLabel: result.errorLabel,
  };
}

/**
 * Merge the wait-for-ready outcome into the open success result. The merged JSON
 * nests the wait outcome under `wait` so a `open --wait` caller sees both the
 * launch metadata and the readiness probe in one payload.
 */
function mergeWaitIntoOpen(
  openSuccess: CliCommandResult,
  target: { port: number; baseUrl: string },
  wait: CliCommandResult,
): CliCommandResult {
  const openJson = openSuccess.json as Record<string, unknown>;
  const waitJson = wait.json as Record<string, unknown>;
  const merged: CliCommandResult = {
    exitCode: wait.exitCode,
    json: {
      ...openJson,
      wait: { ...waitJson, port: target.port, baseUrl: target.baseUrl },
    },
    human:
      openSuccess.human +
      "\n" +
      (wait.exitCode === EXIT.SUCCESS
        ? `wait-for-ready: ${waitJson.reason ?? "bridge ready"}`
        : `wait-for-ready: ${waitJson.reason ?? "bridge not ready"}`),
    errorLabel: wait.errorLabel,
  };
  return merged;
}

// Re-exported for the dispatcher; OpenProjectResult is the library union.
export type { OpenProjectResult };
