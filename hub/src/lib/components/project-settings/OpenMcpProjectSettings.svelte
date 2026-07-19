<script lang="ts">
  import { onMount } from "svelte";
  import type { ProjectEntry } from "$lib/services/config.ts";
  import { detectProjectKind, type ProjectKind } from "$lib/services/config.ts";
  import * as maint from "$lib/services/maintainer.ts";
  import { commandLogs } from "$lib/state/command_logs.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";
  import Console from "./Console.svelte";
  import ConfirmationModal from "$lib/components/shell/ConfirmationModal.svelte";

  interface Props {
    project: ProjectEntry;
    onClose: () => void;
  }
  let { project, onClose }: Props = $props();

  const PANEL = "maintainer";

  let kind = $state<ProjectKind | null>(null);
  let info = $state<maint.McpPackageInfo | null>(null);
  let loadError = $state<string | null>(null);
  let dryRunDone = $state(false);
  let showPublishModal = $state(false);
  let busy = $state(false);

  const panel = $derived(commandLogs.get(PANEL));

  onMount(() => {
    void (async () => {
      kind = await detectProjectKind(project.path);
      if (kind === "openMcp") {
        try {
          info = await maint.readMcpPackageInfo(project.path);
        } catch (e) {
          loadError = String(e);
        }
      }
    })();
  });

  async function run(fn: () => Promise<maint.CommandResult>): Promise<maint.CommandResult> {
    busy = true;
    commandLogs.start(PANEL);
    try {
      const result = await fn();
      commandLogs.finish(PANEL, result);
      return result;
    } finally {
      busy = false;
    }
  }

  async function refresh(): Promise<void> {
    try {
      info = await maint.readMcpPackageInfo(project.path);
    } catch (e) {
      loadError = String(e);
    }
  }

  const doBuild = () => run(() => maint.npmBuild(project.path));
  const doTest = () => run(() => maint.npmTest(project.path));
  const doVersionCheck = () => run(() => maint.versionSyncCheck(project.path));
  const doVersionSync = () => run(() => maint.versionSyncWrite(project.path));

  async function doDryRun(): Promise<void> {
    await run(() => maint.npmPublishDryRun(project.path));
    dryRunDone = true;
  }

  async function confirmPublish(): Promise<void> {
    showPublishModal = false;
    await run(() => maint.npmPublish(project.path));
    await refresh();
  }
</script>

<section class="panel">
  <div class="head">
    <div>
      <h2>Maintainer</h2>
      <p class="proj">{project.path}</p>
    </div>
    <button class="close" onclick={onClose} title="Back to projects">✕</button>
  </div>

  {#if kind === null}
    <p class="muted">Detecting project kind…</p>
  {:else if kind !== "openMcp"}
    <div class="notice">
      <p>This isn't the Godot Open MCP monorepo.</p>
      <p class="muted">
        The maintainer npm panel is only available for the tooling repo (a folder
        whose <code>mcp-server/package.json</code> is named
        <code>godot-open-mcp</code>). Game projects don't publish an npm package.
      </p>
    </div>
  {:else}
    <div class="identity">
      {#if info}
        <div class="pkg">
          <span class="name">{info.name}</span>
          <span class="ver">v{info.version}</span>
        </div>
        <div class="manifest">{info.manifestPath}</div>
        <div class="cwd">npm cwd: <code>{project.path}/mcp-server</code></div>
      {:else if loadError}
        <p class="err">{loadError}</p>
      {/if}
    </div>

    <div class="actions">
      <Button disabled={busy} onclick={refresh}>Refresh</Button>
      <Button disabled={busy} onclick={doBuild}>Build</Button>
      <Button disabled={busy} onclick={doTest}>Test</Button>
      <Button disabled={busy} onclick={doVersionCheck}>Version check</Button>
      <Button disabled={busy} onclick={doVersionSync}>Version sync</Button>
      <Button disabled={busy} onclick={doDryRun}>Dry-run publish</Button>
      <Button variant="danger" disabled={busy} onclick={() => (showPublishModal = true)}>
        Publish…
      </Button>
    </div>

    {#if !dryRunDone}
      <p class="hint">Tip: run a dry-run publish before the real publish.</p>
    {/if}

    <Console lines={panel.lines} running={panel.running} lastExitCode={panel.lastExitCode} />
  {/if}
</section>

{#if showPublishModal && info}
  <ConfirmationModal
    title="Publish to npm?"
    confirmLabel="Publish now"
    danger
    onConfirm={confirmPublish}
    onCancel={() => (showPublishModal = false)}
  >
    <p>
      This runs <code>npm publish --access public</code> and pushes
      <b>{info.name}@{info.version}</b> to the public npm registry.
    </p>
    <p>cwd: <code>{project.path}/mcp-server</code></p>
    {#if !dryRunDone}
      <p style="color: var(--gom-warn)">You haven't run a dry-run this session.</p>
    {/if}
    <p>This cannot be undone. Make sure the version is correct.</p>
  </ConfirmationModal>
{/if}

<style>
  .panel {
    padding: 18px 24px;
    max-width: 900px;
    margin: 0 auto;
  }
  .head {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    margin-bottom: 14px;
  }
  .proj {
    margin: 2px 0 0;
    color: var(--gom-text-faint);
    font-family: var(--gom-mono);
    font-size: 12px;
  }
  .close {
    background: none;
    border: 1px solid var(--gom-border);
    color: var(--gom-text-dim);
    border-radius: var(--gom-radius-sm);
    width: 30px;
    height: 30px;
    cursor: pointer;
  }
  .muted {
    color: var(--gom-text-dim);
  }
  .notice {
    border: 1px dashed var(--gom-border-strong);
    border-radius: var(--gom-radius);
    padding: 20px;
  }
  .identity {
    background: var(--gom-bg-elevated);
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius);
    padding: 12px 14px;
    margin-bottom: 12px;
  }
  .pkg {
    display: flex;
    align-items: baseline;
    gap: 8px;
  }
  .name {
    font-weight: 600;
    font-size: 15px;
  }
  .ver {
    color: var(--gom-accent);
    font-family: var(--gom-mono);
  }
  .manifest,
  .cwd {
    font-family: var(--gom-mono);
    font-size: 12px;
    color: var(--gom-text-faint);
    margin-top: 4px;
    word-break: break-all;
  }
  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 8px;
  }
  .hint {
    color: var(--gom-text-faint);
    font-size: 12px;
    margin: 4px 0 10px;
  }
  .err {
    color: var(--gom-err);
  }
</style>
