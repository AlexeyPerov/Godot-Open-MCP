<script lang="ts">
  import { open } from "@tauri-apps/plugin-dialog";
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";
  import LogPanel from "./LogPanel.svelte";

  let { w }: { w: WizardState } = $props();

  async function browseEditor(): Promise<void> {
    const sel = await open({ directory: false, multiple: false, title: "Select the Godot editor binary" });
    if (typeof sel === "string") w.editorPath = sel;
  }

  const statusLabel = $derived(
    w.pingStatus === "ready"
      ? "Bridge ready ✓"
      : w.pingStatus === "compiling"
        ? "Compiling…"
        : w.pingStatus === "pending"
          ? "Waiting for the bridge…"
          : w.pingStatus === "timeout"
            ? "Timed out"
            : w.pingStatus
              ? "Not reachable"
              : "",
  );
</script>

<h3>Launch &amp; verify</h3>
<p class="hint">
  Opens the Godot editor on this project and polls the bridge
  <code>/ping</code> until it reports ready — the same wait-for-ready the CLI
  runs.
</p>

<label class="field">
  <span>Godot editor binary (optional — auto-discovered if blank)</span>
  <div class="row">
    <input
      type="text"
      placeholder="auto (GODOT env / PATH / common install dirs)"
      value={w.editorPath ?? ""}
      oninput={(e) => (w.editorPath = (e.target as HTMLInputElement).value || null)}
    />
    <Button onclick={browseEditor}>Browse…</Button>
  </div>
</label>

<div class="act">
  <Button variant="primary" disabled={w.busy} onclick={() => w.launchAndWait()}>
    {w.busy ? "Launching…" : "Launch Godot & wait"}
  </Button>
  {#if statusLabel}
    <span class="status" class:ready={w.pingStatus === "ready"}>{statusLabel}</span>
  {/if}
</div>

<LogPanel lines={w.log} />

{#if w.pingStatus && w.pingStatus !== "ready"}
  <label class="toggle">
    <input
      type="checkbox"
      checked={w.launchAcknowledged}
      onchange={(e) => (w.launchAcknowledged = (e.target as HTMLInputElement).checked)}
    />
    <span>The editor didn't reach ready — continue anyway (I'll verify later).</span>
  </label>
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
  .act {
    display: flex;
    align-items: center;
    gap: 12px;
    margin: 10px 0;
  }
  .status {
    color: var(--gom-warn);
    font-size: 13px;
  }
  .status.ready {
    color: var(--gom-ok);
  }
  .toggle {
    display: flex;
    gap: 8px;
    align-items: center;
    margin-top: 10px;
    color: var(--gom-text-dim);
    font-size: 13px;
  }
</style>
