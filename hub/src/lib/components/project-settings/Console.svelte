<script lang="ts">
  import type { CommandLine } from "$lib/services/maintainer.ts";

  interface Props {
    lines: CommandLine[];
    running: boolean;
    lastExitCode: number | null;
  }
  let { lines, running, lastExitCode }: Props = $props();
</script>

<div class="console">
  <div class="chead">
    <span class="title">Console</span>
    {#if running}
      <span class="badge run">running…</span>
    {:else if lastExitCode !== null}
      <span class="badge" class:ok={lastExitCode === 0} class:bad={lastExitCode !== 0}>
        exit {lastExitCode}
      </span>
    {/if}
  </div>
  <div class="body">
    {#if lines.length === 0}
      <div class="empty">No output yet.</div>
    {:else}
      {#each lines as l, i (i)}
        <div class="line {l.stream}">{l.text}</div>
      {/each}
    {/if}
  </div>
</div>

<style>
  .console {
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius-sm);
    overflow: hidden;
    background: var(--gom-bg-inset);
  }
  .chead {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--gom-border);
    background: var(--gom-bg-elevated);
  }
  .title {
    font-size: 12px;
    color: var(--gom-text-dim);
  }
  .badge {
    font-size: 11px;
    padding: 1px 8px;
    border-radius: 10px;
    border: 1px solid var(--gom-border-strong);
  }
  .badge.run {
    color: var(--gom-warn);
    border-color: var(--gom-warn);
  }
  .badge.ok {
    color: var(--gom-ok);
    border-color: var(--gom-ok);
  }
  .badge.bad {
    color: var(--gom-err);
    border-color: var(--gom-err);
  }
  .body {
    max-height: 320px;
    overflow: auto;
    padding: 8px 10px;
    font-family: var(--gom-mono);
    font-size: 12px;
  }
  .empty {
    color: var(--gom-text-faint);
  }
  .line {
    white-space: pre-wrap;
    word-break: break-word;
  }
  .line.meta {
    color: var(--gom-accent);
  }
  .line.stderr {
    color: var(--gom-warn);
  }
  .line.stdout {
    color: var(--gom-text);
  }
</style>
