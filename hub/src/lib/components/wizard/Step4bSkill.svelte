<script lang="ts">
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";

  let { w }: { w: WizardState } = $props();
</script>

<h3>Agent skill (optional)</h3>
<p class="hint">
  If you selected a monorepo checkout, the Hub can copy the
  <code>skills/godot-open-mcp</code> playbook into the project so agents pick up
  the safe-usage rules. This step is optional — skip it any time.
</p>

<label class="toggle">
  <input type="checkbox" checked={w.copySkill} onchange={(e) => (w.copySkill = (e.target as HTMLInputElement).checked)} />
  <span>Copy the skill into <code>.godot-open-mcp/skills/</code></span>
</label>

<div class="act">
  <Button disabled={w.busy || !w.copySkill} onclick={() => w.runSkill()}>
    {w.busy ? "Copying…" : "Copy skill"}
  </Button>
</div>

{#if w.skillResult}
  <p class:ok={w.skillResult.copied} class:muted={!w.skillResult.copied}>
    {w.skillResult.message ?? (w.skillResult.copied ? `Copied to ${w.skillResult.dest}` : "Skipped.")}
  </p>
{/if}

<style>
  .hint {
    color: var(--gom-text-dim);
  }
  .toggle {
    display: flex;
    gap: 8px;
    align-items: center;
    margin: 12px 0;
  }
  .act {
    margin: 8px 0;
  }
  .ok {
    color: var(--gom-ok);
  }
  .muted {
    color: var(--gom-text-dim);
  }
</style>
