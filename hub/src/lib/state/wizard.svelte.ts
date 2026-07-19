/**
 * AI Setup wizard state (Svelte 5 runes).
 *
 * Orchestrates the CLI-equivalent outcomes end to end: detection → install
 * addon → write MCP client config → launch editor + wait-for-ready. Holds the
 * form model, per-step results, and a live log; persists a draft so a crash
 * doesn't lose choices. All real work delegates to the Rust backend
 * (`ai_toolkit` service), which mirrors the `godot-open-mcp-cli` contracts.
 */

import * as ai from "../services/ai_toolkit.ts";
import * as config from "../services/config.ts";
import { resolveBridgePort } from "../services/config.ts";
import { APP_VERSION } from "../version.ts";
import {
  clearDraft,
  loadDraft,
  saveDraft,
} from "../services/ai_setup_wizard_draft.ts";

export const STEP_IDS = [
  "step0",
  "step1",
  "step2",
  "step3",
  "step4",
  "step4b",
  "step5",
  "done",
] as const;
export type StepId = (typeof STEP_IDS)[number];

export const STEP_TITLES: Record<StepId, string> = {
  step0: "Preset",
  step1: "Detect project",
  step2: "MCP server source",
  step3: "Install addon",
  step4: "MCP client",
  step4b: "Skill (optional)",
  step5: "Launch",
  done: "Summary",
};

export interface LogLine {
  level: "info" | "ok" | "warn" | "error";
  text: string;
}

export class WizardState {
  projectPath = $state("");
  projectName = $state("");
  index = $state(0);

  // Form model.
  source = $state<ai.McpSource>("npx-published");
  clientId = $state("cursor");
  overridePort = $state<number | null>(null);
  monorepoPath = $state<string | null>(null);
  editorPath = $state<string | null>(null);
  copySkill = $state(false);

  // Backend snapshots + results.
  agents = $state<ai.AgentInfo[]>([]);
  detection = $state<ai.ProjectState | null>(null);
  resolvedPort = $state<number | null>(null);
  mcpPreview = $state<ai.McpPlan | null>(null);
  installResult = $state<ai.InstallResult | null>(null);
  mcpResult = $state<ai.McpPlan | null>(null);
  launchResult = $state<ai.LaunchResult | null>(null);
  pingStatus = $state<ai.PingResult["status"] | "pending" | null>(null);
  skillResult = $state<ai.SkillCopyResult | null>(null);

  busy = $state(false);
  error = $state<string | null>(null);
  log = $state<LogLine[]>([]);
  launchAcknowledged = $state(false);

  get stepId(): StepId {
    return STEP_IDS[this.index];
  }

  private push(level: LogLine["level"], text: string): void {
    this.log = [...this.log, { level, text }];
  }

  async init(project: config.ProjectEntry): Promise<void> {
    this.projectPath = project.path;
    this.projectName = project.name;
    try {
      this.agents = await ai.listAgents();
    } catch (e) {
      this.error = String(e);
    }
    const draft = loadDraft(project.path);
    if (draft) {
      this.source = draft.source;
      this.clientId = draft.clientId;
      this.overridePort = draft.overridePort;
      this.monorepoPath = draft.monorepoPath;
      this.editorPath = draft.editorPath;
      this.copySkill = draft.copySkill;
      this.index = Math.min(draft.stepIndex, STEP_IDS.length - 1);
      this.push("info", "Restored a saved draft for this project.");
    }
  }

  private persist(): void {
    saveDraft(this.projectPath, {
      source: this.source,
      clientId: this.clientId,
      overridePort: this.overridePort,
      monorepoPath: this.monorepoPath,
      editorPath: this.editorPath,
      copySkill: this.copySkill,
      stepIndex: this.index,
    });
  }

  // ── navigation ─────────────────────────────────────────────────────────────

  canAdvance(): boolean {
    switch (this.stepId) {
      case "step1":
        return this.detection?.isGodotProject === true;
      case "step2":
        if (this.detection && !this.detection.node.ok) return false;
        if (this.source === "local-checkout")
          return !!this.monorepoPath && this.monorepoPath.length > 0;
        return true;
      case "step3":
        return this.installResult?.ok === true;
      case "step4":
        // Written this run, or already up to date (changed:false) — either is a pass.
        return !!this.mcpResult && (this.mcpResult.wrote || !this.mcpResult.changed);
      case "step5":
        return this.pingStatus === "ready" || this.launchAcknowledged;
      default:
        return true;
    }
  }

  async next(): Promise<void> {
    if (this.index >= STEP_IDS.length - 1) return;
    this.index += 1;
    this.persist();
    // Auto-run the entering step's detection/preview.
    await this.onEnterStep();
  }

  async back(): Promise<void> {
    if (this.index === 0) return;
    this.index -= 1;
    this.persist();
  }

  async onEnterStep(): Promise<void> {
    switch (this.stepId) {
      case "step1":
        await this.runDetection();
        break;
      case "step2":
        await this.resolvePort();
        await this.previewMcp();
        break;
      case "step4":
        await this.previewMcp();
        break;
      default:
        break;
    }
  }

  // ── actions ────────────────────────────────────────────────────────────────

  async runDetection(): Promise<void> {
    this.busy = true;
    this.error = null;
    try {
      this.detection = await ai.detectProjectState(this.projectPath, [this.clientId]);
      if (!this.detection.isGodotProject) {
        this.push("error", "No project.godot found — pick a valid Godot project.");
      } else {
        this.push("ok", `Detected Godot project (bridge port ${this.detection.bridge.port}).`);
        if (!this.detection.node.ok) {
          this.push("warn", this.detection.node.error ?? "Node 18+ is required.");
        }
      }
    } catch (e) {
      this.error = String(e);
    } finally {
      this.busy = false;
    }
  }

  async resolvePort(): Promise<void> {
    try {
      this.resolvedPort = await resolveBridgePort(
        this.projectPath,
        this.overridePort ?? undefined,
      );
    } catch (e) {
      this.error = String(e);
    }
  }

  private mcpInput(): ai.McpInput {
    return {
      projectPath: this.projectPath,
      agentId: this.clientId,
      source: this.source,
      packageVersion: this.source === "npx-published" ? APP_VERSION : null,
      monorepoPath: this.monorepoPath,
      overridePort: this.overridePort,
    };
  }

  async previewMcp(): Promise<void> {
    try {
      this.mcpPreview = await ai.planMcpConfig(this.mcpInput());
    } catch (e) {
      this.error = String(e);
      this.mcpPreview = null;
    }
  }

  async runInstall(): Promise<void> {
    this.busy = true;
    this.error = null;
    try {
      const source = null; // resolve via monorepo path (local) or fail clearly
      this.installResult = await ai.installAddon(
        this.projectPath,
        source,
        this.monorepoPath,
      );
      if (this.installResult.ok) {
        this.push(
          "ok",
          this.installResult.changed
            ? "Installed addon and enabled the plugin."
            : "Addon already installed and enabled (no change).",
        );
        for (const w of this.installResult.warnings ?? []) this.push("warn", w);
      } else {
        this.push("error", `Install failed (${this.installResult.errorLabel}).`);
        for (const w of this.installResult.warnings ?? []) this.push("error", w);
      }
    } catch (e) {
      this.error = String(e);
    } finally {
      this.busy = false;
    }
  }

  async writeMcp(): Promise<void> {
    this.busy = true;
    this.error = null;
    try {
      this.mcpResult = await ai.writeMcpConfig(this.mcpInput());
      this.push(
        "ok",
        this.mcpResult.changed
          ? `Wrote MCP config: ${this.mcpResult.configPath}`
          : `MCP config already up to date: ${this.mcpResult.configPath}`,
      );
    } catch (e) {
      this.error = String(e);
      this.push("error", String(e));
    } finally {
      this.busy = false;
    }
  }

  async runSkill(): Promise<void> {
    this.busy = true;
    try {
      this.skillResult = await ai.copySkillFiles(this.projectPath, this.monorepoPath);
      this.push(
        this.skillResult.copied ? "ok" : "info",
        this.skillResult.message ??
          (this.skillResult.copied ? `Copied skill to ${this.skillResult.dest}` : "Skill skipped."),
      );
    } catch (e) {
      this.push("warn", String(e));
    } finally {
      this.busy = false;
    }
  }

  /** Launch the editor and poll /ping until ready or timeout. */
  async launchAndWait(): Promise<void> {
    this.busy = true;
    this.error = null;
    this.pingStatus = "pending";
    try {
      this.launchResult = await ai.launchGodot(this.projectPath, this.editorPath);
      if (!this.launchResult.ok) {
        this.push("error", this.launchResult.message ?? "Could not launch Godot.");
        this.pingStatus = "offline";
        return;
      }
      this.push("ok", `Launched Godot (${this.launchResult.editorPath}). Waiting for the bridge…`);

      const deadline = Date.now() + 120_000; // matches CLI DEFAULT_WAIT_TIMEOUT_MS
      while (Date.now() < deadline) {
        const ping = await ai.pollBridgePing(this.projectPath, this.overridePort);
        this.pingStatus = ping.status;
        if (ping.status === "ready") {
          this.push("ok", `Bridge is ready on port ${ping.port}.`);
          return;
        }
        if (ping.status === "compiling") {
          this.push("info", "Bridge compiling — waiting…");
        }
        await new Promise((r) => setTimeout(r, 1500));
      }
      this.pingStatus = "timeout";
      this.push("warn", "Bridge did not become ready within 120s. You can skip and check later.");
    } catch (e) {
      this.error = String(e);
      this.pingStatus = "error";
    } finally {
      this.busy = false;
    }
  }

  finish(): void {
    clearDraft(this.projectPath);
  }
}
