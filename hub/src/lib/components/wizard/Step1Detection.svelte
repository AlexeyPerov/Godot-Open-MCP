<script lang="ts">
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";

  let { w }: { w: WizardState } = $props();

  function row(ok: boolean): string {
    return ok ? "ok" : "bad";
  }
</script>

<h3>Project detection</h3>
{#if !w.detection}
  <p class="hint">Running detection…</p>
{:else}
  <ul class="checks">
    <li class={row(w.detection.isGodotProject)}>
      <span>Valid Godot project (project.godot)</span>
      <b>{w.detection.isGodotProject ? "Yes" : "No"}</b>
    </li>
    <li class={row(w.detection.node.ok)}>
      <span>Node 18+ available</span>
      <b>{w.detection.node.ok ? (w.detection.node.version ?? "OK") : (w.detection.node.error ?? "Missing")}</b>
    </li>
    <li class={row(w.detection.addon.installed)}>
      <span>Addon installed</span>
      <b>{w.detection.addon.installed ? "Yes" : "Not yet"}</b>
    </li>
    <li class={row(w.detection.addon.enabled)}>
      <span>Plugin enabled</span>
      <b>{w.detection.addon.enabled ? "Yes" : "Not yet"}</b>
    </li>
    {#each w.detection.mcp as m (m.clientId)}
      <li class={row(m.configured)}>
        <span>MCP configured — {m.clientId}</span>
        <b>{m.configured ? "Yes" : "Not yet"}</b>
      </li>
    {/each}
    <li class="ok">
      <span>Bridge port (deterministic)</span>
      <b>{w.detection.bridge.port}</b>
    </li>
  </ul>

  {#if !w.detection.isGodotProject}
    <p class="err">This folder has no project.godot. Go back and pick a Godot project.</p>
  {/if}
{/if}

<div class="act">
  <Button disabled={w.busy} onclick={() => w.runDetection()}>Re-run detection</Button>
</div>

<style>
  .hint {
    color: var(--gom-text-dim);
  }
  .checks {
    list-style: none;
    padding: 0;
    margin: 12px 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .checks li {
    display: flex;
    justify-content: space-between;
    padding: 8px 12px;
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius-sm);
    background: var(--gom-bg-elevated);
  }
  .checks li.ok b {
    color: var(--gom-ok);
  }
  .checks li.bad b {
    color: var(--gom-warn);
  }
  .err {
    color: var(--gom-err);
  }
  .act {
    margin-top: 10px;
  }
</style>
