<script lang="ts">
  import type { WizardState } from "$lib/state/wizard.svelte.ts";

  let { w }: { w: WizardState } = $props();
</script>

<h3>Setup complete</h3>
<p class="hint">Here's what was configured for <b>{w.projectName}</b>.</p>

<ul class="summary">
  {#if w.installResult?.ok}
    <li>
      <span class="k">Addon</span>
      <span class="v">{w.installResult.addonDir}</span>
    </li>
  {/if}
  {#if w.mcpResult}
    <li>
      <span class="k">MCP config</span>
      <span class="v">{w.mcpResult.configPath}</span>
    </li>
  {/if}
  {#if w.resolvedPort}
    <li>
      <span class="k">Bridge port</span>
      <span class="v">{w.resolvedPort}</span>
    </li>
  {/if}
  <li>
    <span class="k">Bridge status</span>
    <span class="v" class:ok={w.pingStatus === "ready"}>
      {w.pingStatus === "ready" ? "ready" : (w.pingStatus ?? "not verified")}
    </span>
  </li>
  {#if w.skillResult?.copied}
    <li>
      <span class="k">Skill</span>
      <span class="v">{w.skillResult.dest}</span>
    </li>
  {/if}
</ul>

<h4>Next steps</h4>
<ol class="next">
  <li>In your AI client, confirm the <code>godot-open-mcp</code> server appears.</li>
  <li>Call <code>godot_open_mcp_ping</code> — you should get a pong.</li>
  {#if w.pingStatus !== "ready"}
    <li>If the bridge isn't ready, open the editor and check the Godot output for C# compile errors.</li>
  {/if}
</ol>

<style>
  .hint {
    color: var(--gom-text-dim);
  }
  .summary {
    list-style: none;
    padding: 0;
    margin: 12px 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .summary li {
    display: flex;
    gap: 12px;
    padding: 8px 12px;
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius-sm);
    background: var(--gom-bg-elevated);
  }
  .k {
    flex: 0 0 120px;
    color: var(--gom-text-dim);
  }
  .v {
    font-family: var(--gom-mono);
    font-size: 12px;
    word-break: break-all;
  }
  .v.ok {
    color: var(--gom-ok);
  }
  .next {
    color: var(--gom-text-dim);
    padding-left: 20px;
  }
</style>
