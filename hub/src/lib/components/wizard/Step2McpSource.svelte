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

  // Inline validation message for an out-of-range port. The `min`/`max` attributes on
  // <input type="number"> are only enforced at form-validation time, never while typing.
  let portError = $state<string | null>(null);

  async function onPortInput(e: Event): Promise<void> {
    const v = (e.target as HTMLInputElement).value.trim();
    if (v === "") {
      portError = null;
      w.overridePort = null;
    } else {
      // Validate BEFORE assigning. Rust types this as Option<u16>, so a value like 70000, -1 or 1.5
      // fails serde deserialization on every subsequent invoke that carries it — resolve_bridge_port,
      // plan_mcp_config, then write_mcp_config — surfacing a raw serde string ("invalid value:
      // integer `70000`, expected u16") and hard-blocking Step 4 (mcpResult stays null so canAdvance
      // is false). The bad value was also persisted to the localStorage draft, so it survived a
      // restart.
      const n = Number.parseInt(v, 10);
      if (!Number.isInteger(n) || n < 1 || n > 65535) {
        portError = "Port must be a whole number between 1 and 65535.";
        return;
      }
      portError = null;
      w.overridePort = n;
    }
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
    aria-invalid={portError !== null}
  />
  {#if portError}
    <small class="err">{portError}</small>
  {/if}
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
