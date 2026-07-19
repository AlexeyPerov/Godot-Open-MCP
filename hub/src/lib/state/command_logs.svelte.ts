/**
 * Per-panel command log buffers (Svelte 5 runes) for the maintainer console.
 *
 * Mirrors the Unity Hub pattern: each named panel keeps a capped line buffer
 * plus `running` + `lastExitCode` badges. Capped at 1000 lines (the Rust runner
 * also caps its output; this cap guards against many successive runs).
 */

import type { CommandLine, CommandResult } from "../services/maintainer.ts";

const MAX_LINES = 1000;

interface PanelLog {
  lines: CommandLine[];
  running: boolean;
  lastExitCode: number | null;
  lastArgv: string | null;
}

function emptyPanel(): PanelLog {
  return { lines: [], running: false, lastExitCode: null, lastArgv: null };
}

class CommandLogs {
  panels = $state<Record<string, PanelLog>>({});

  private ensure(id: string): PanelLog {
    if (!this.panels[id]) {
      this.panels = { ...this.panels, [id]: emptyPanel() };
    }
    return this.panels[id];
  }

  get(id: string): PanelLog {
    return this.panels[id] ?? emptyPanel();
  }

  start(id: string): void {
    const panel = { ...this.ensure(id), running: true };
    this.panels = { ...this.panels, [id]: panel };
  }

  clear(id: string): void {
    this.panels = { ...this.panels, [id]: emptyPanel() };
  }

  /** Fold a completed command result into the panel buffer. */
  finish(id: string, result: CommandResult): void {
    const prev = this.ensure(id);
    const merged = [...prev.lines, ...result.lines].slice(-MAX_LINES);
    this.panels = {
      ...this.panels,
      [id]: {
        lines: merged,
        running: false,
        lastExitCode: result.exitCode,
        lastArgv: result.argv,
      },
    };
  }
}

export const commandLogs = new CommandLogs();
