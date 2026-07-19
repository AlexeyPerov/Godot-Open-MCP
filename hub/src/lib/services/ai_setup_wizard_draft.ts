/**
 * In-progress wizard draft persistence, keyed by project path, so a crash /
 * accidental close doesn't lose the operator's form choices.
 *
 * No migrations: an incompatible draft version is dropped (returns null), never
 * upgraded (root AGENTS.md). No secrets are stored — only form choices. Backed
 * by the webview `localStorage`, which persists across app restarts.
 */

import type { McpSource } from "./ai_toolkit.ts";

/** Bump when the draft shape changes; older drafts are silently dropped. */
const DRAFT_VERSION = 1;
const KEY_PREFIX = "gom-hub-wizard-draft:";

export interface WizardDraft {
  version: number;
  source: McpSource;
  clientId: string;
  overridePort: number | null;
  monorepoPath: string | null;
  editorPath: string | null;
  copySkill: boolean;
  /** Highest step index reached (for resume). */
  stepIndex: number;
}

function keyFor(projectPath: string): string {
  return KEY_PREFIX + projectPath;
}

export function loadDraft(projectPath: string): WizardDraft | null {
  try {
    const raw = localStorage.getItem(keyFor(projectPath));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<WizardDraft>;
    if (parsed.version !== DRAFT_VERSION) {
      // Incompatible draft — drop it (no migrations).
      localStorage.removeItem(keyFor(projectPath));
      return null;
    }
    return parsed as WizardDraft;
  } catch {
    return null;
  }
}

export function saveDraft(projectPath: string, draft: Omit<WizardDraft, "version">): void {
  try {
    const full: WizardDraft = { version: DRAFT_VERSION, ...draft };
    localStorage.setItem(keyFor(projectPath), JSON.stringify(full));
  } catch {
    /* storage may be unavailable — draft persistence is best-effort */
  }
}

export function clearDraft(projectPath: string): void {
  try {
    localStorage.removeItem(keyFor(projectPath));
  } catch {
    /* best effort */
  }
}
