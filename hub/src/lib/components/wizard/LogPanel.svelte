<script lang="ts">
  import type { LogLine } from "$lib/state/wizard.svelte.ts";
  let { lines }: { lines: LogLine[] } = $props();
</script>

{#if lines.length > 0}
  <div class="log" role="log">
    {#each lines as l, i (i)}
      <div class="line {l.level}">{l.text}</div>
    {/each}
  </div>
{/if}

<style>
  .log {
    background: var(--gom-bg-inset);
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius-sm);
    padding: 8px 10px;
    font-family: var(--gom-mono);
    font-size: 12px;
    max-height: 220px;
    overflow: auto;
  }
  .line {
    white-space: pre-wrap;
    word-break: break-word;
  }
  .ok {
    color: var(--gom-ok);
  }
  .warn {
    color: var(--gom-warn);
  }
  .error {
    color: var(--gom-err);
  }
  .info {
    color: var(--gom-text-dim);
  }
</style>
