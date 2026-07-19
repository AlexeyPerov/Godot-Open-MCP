<script lang="ts">
  import { open } from "@tauri-apps/plugin-dialog";
  import type { WizardState } from "$lib/state/wizard.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";

  let { w }: { w: WizardState } = $props();

  async function browseMonorepo(): Promise<void> {
    const sel = await open({ directory: true, multiple: false, title: "Select the Godot Open MCP monorepo root" });
    if (typeof sel === "string") {
      w.monorepoPath = sel;
      await w.resolvePort();
      await w.previewMcp();
    }
  }

  async function onPortInput(e: Event): Promise<void> {
    const v = (e.target as HTMLInputElement).value.trim();
    w.overridePort = v === "" ? null : Number(v);
    await w.resolvePort();
    await w.previewMcp();
  }
</script>

<h3>MCP server source</h3>

{#if w.source === "local-checkout"}
  <label class="field">
    <span>Monorepo path (contains <code>mcp-server/</code>)</span>
    <div class="row">
      <input
        type="text"
        placeholder="/path/to/Godot-Open-MCP"
        value={w.monorepoPath ?? ""}
        oninput={(e) => (w.monorepoPath = (e.target as HTMLInputElement).value)}
      />
      <Button onclick={browseMonorepo}>Browse…</Button>
    </div>
    <small>Writes <code>node &lt;repo&gt;/mcp-server/dist/index.js</code>.</small>
  </label>
{:else}
  <p class="hint">
    Uses <code>npx -y godot-open-mcp</code> (pinned to the Hub version). No
    checkout required.
  </p>
{/if}

<label class="field">
  <span>Bridge port override (optional)</span>
  <input
    type="number"
    min="1"
    max="65535"
    placeholder="deterministic"
    value={w.overridePort ?? ""}
    oninput={onPortInput}
  />
  <small>
    Resolved port: <b>{w.resolvedPort ?? "…"}</b>. Leave blank to use the
    deterministic per-project port; a value writes
    <code>GODOT_OPEN_MCP_BRIDGE_PORT</code>.
  </small>
</label>

{#if w.mcpPreview}
  <h4>Preview — {w.mcpPreview.serverName} (stdio)</h4>
  <pre>{JSON.stringify(w.mcpPreview.entry, null, 2)}</pre>
{/if}

{#if w.detection && !w.detection.node.ok}
  <p class="err">{w.detection.node.error ?? "Node 18+ is required to run the MCP server."}</p>
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
  pre {
    background: var(--gom-bg-inset);
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius-sm);
    padding: 10px;
    overflow: auto;
  }
  .err {
    color: var(--gom-err);
  }
</style>
