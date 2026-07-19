<script lang="ts">
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";

  let { w }: { w: WizardState } = $props();

  async function onClientChange(e: Event): Promise<void> {
    w.clientId = (e.target as HTMLSelectElement).value;
    w.mcpResult = null;
    await w.previewMcp();
  }
</script>

<h3>MCP client config</h3>
<p class="hint">
  Writes a stdio <code>godot-open-mcp</code> entry (no <code>url</code>) into the
  selected client's config, preserving any other servers.
</p>

<label class="field">
  <span>Client</span>
  <select value={w.clientId} onchange={onClientChange}>
    {#each w.agents as a (a.id)}
      <option value={a.id}>{a.name} — {a.configPathDisplay}</option>
    {/each}
  </select>
</label>

{#if w.mcpPreview}
  <p class="path">Target: {w.mcpPreview.configPath}</p>
  <h4>Entry preview</h4>
  <pre>{JSON.stringify(w.mcpPreview.entry, null, 2)}</pre>
  {#each w.mcpPreview.warnings ?? [] as msg}
    <p class="warn">{msg}</p>
  {/each}
{/if}

<div class="act">
  <Button variant="primary" disabled={w.busy} onclick={() => w.writeMcp()}>
    {w.busy ? "Writing…" : "Write config"}
  </Button>
</div>

{#if w.mcpResult}
  <p class="ok">
    ✓ {w.mcpResult.changed ? "Wrote" : "Already up to date"} — {w.mcpResult.configPath}
  </p>
{/if}

<style>
  .hint {
    color: var(--gom-text-dim);
  }
  .field {
    display: block;
    margin: 12px 0;
  }
  .field > span {
    display: block;
    margin-bottom: 4px;
    font-size: 13px;
  }
  select {
    width: 100%;
    background: var(--gom-bg-inset);
    border: 1px solid var(--gom-border-strong);
    color: var(--gom-text);
    border-radius: var(--gom-radius-sm);
    padding: 7px 10px;
    font-size: 13px;
  }
  pre {
    background: var(--gom-bg-inset);
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius-sm);
    padding: 10px;
    overflow: auto;
  }
  .path {
    font-family: var(--gom-mono);
    font-size: 12px;
    color: var(--gom-text-dim);
    word-break: break-all;
  }
  .act {
    margin: 10px 0;
  }
  .ok {
    color: var(--gom-ok);
  }
  .warn {
    color: var(--gom-warn);
    font-size: 13px;
  }
</style>
