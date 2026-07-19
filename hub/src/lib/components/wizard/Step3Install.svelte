<script lang="ts">
  import { open } from "@tauri-apps/plugin-dialog";
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";

  let { w }: { w: WizardState } = $props();

  async function browseMonorepo(): Promise<void> {
    const sel = await open({ directory: true, multiple: false, title: "Select the Godot Open MCP monorepo root" });
    if (typeof sel === "string") w.monorepoPath = sel;
  }
</script>

<h3>Install the addon</h3>
<p class="hint">
  Copies <code>addons/godot_open_mcp/</code> into the project and enables the
  plugin in <code>project.godot</code> — the same outcome as the CLI's
  <code>install-plugin</code>. Idempotent.
</p>

<label class="field">
  <span>Addon source — monorepo path (contains <code>packages/bridge/</code>)</span>
  <div class="row">
    <input
      type="text"
      placeholder="/path/to/Godot-Open-MCP"
      value={w.monorepoPath ?? ""}
      oninput={(e) => (w.monorepoPath = (e.target as HTMLInputElement).value)}
    />
    <Button onclick={browseMonorepo}>Browse…</Button>
  </div>
  <small>The addon files are copied from <code>&lt;repo&gt;/packages/bridge</code> (+ bundled verify source).</small>
</label>

<div class="act">
  <Button variant="primary" disabled={w.busy} onclick={() => w.runInstall()}>
    {w.busy ? "Installing…" : "Install addon"}
  </Button>
</div>

{#if w.installResult}
  {#if w.installResult.ok}
    <div class="ok">
      <p>✓ {w.installResult.changed ? "Installed and enabled." : "Already installed and enabled."}</p>
      <p class="path">{w.installResult.addonDir}</p>
      <p class="path">enabled: {w.installResult.enabledPlugins.join(", ")}</p>
    </div>
  {:else}
    <div class="err">
      <p>✕ Install failed ({w.installResult.errorLabel}).</p>
      {#each w.installResult.warnings ?? [] as msg}
        <p class="path">{msg}</p>
      {/each}
    </div>
  {/if}
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
  .row {
    display: flex;
    gap: 8px;
  }
  input {
    flex: 1 1 auto;
    background: var(--gom-bg-inset);
    border: 1px solid var(--gom-border-strong);
    color: var(--gom-text);
    border-radius: var(--gom-radius-sm);
    padding: 7px 10px;
    font-family: var(--gom-mono);
    font-size: 13px;
  }
  small {
    display: block;
    color: var(--gom-text-faint);
    margin-top: 4px;
  }
  .act {
    margin: 10px 0;
  }
  .ok {
    color: var(--gom-ok);
  }
  .err {
    color: var(--gom-err);
  }
  .path {
    font-family: var(--gom-mono);
    font-size: 12px;
    color: var(--gom-text-dim);
    margin: 2px 0;
    word-break: break-all;
  }
</style>
