<script lang="ts">
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import { WIZARD_PRESETS } from "$lib/services/wizard_presets.ts";

  let { w }: { w: WizardState } = $props();
</script>

<h3>How should AI clients run the MCP server?</h3>
<p class="hint">
  This mirrors the CLI: the AI client spawns the <code>godot-open-mcp</code>
  server over stdio. Pick the source that matches your setup.
</p>

<div class="presets">
  {#each WIZARD_PRESETS as p (p.id)}
    <label class="preset" class:sel={w.source === p.source}>
      <input
        type="radio"
        name="preset"
        value={p.source}
        checked={w.source === p.source}
        onchange={() => (w.source = p.source)}
      />
      <div>
        <div class="plabel">{p.label}</div>
        <div class="pdesc">{p.description}</div>
      </div>
    </label>
  {/each}
</div>

<style>
  .hint {
    color: var(--gom-text-dim);
  }
  .presets {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin-top: 12px;
  }
  .preset {
    display: flex;
    gap: 10px;
    padding: 12px;
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius);
    cursor: pointer;
  }
  .preset.sel {
    border-color: var(--gom-accent);
    background: color-mix(in srgb, var(--gom-accent) 8%, transparent);
  }
  .plabel {
    font-weight: 600;
  }
  .pdesc {
    color: var(--gom-text-dim);
    font-size: 13px;
  }
</style>
