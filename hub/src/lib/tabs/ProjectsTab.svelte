<script lang="ts">
  import { open } from "@tauri-apps/plugin-dialog";
  import { appState } from "$lib/state/app.svelte.ts";
  import * as config from "$lib/services/config.ts";
  import Button from "$lib/components/shell/Button.svelte";

  interface Props {
    /** Open the AI Setup wizard for the given project. */
    onAiSetup: (project: config.ProjectEntry) => void;
    /** Open the maintainer panel for an OpenMcp repo. */
    onMaintainer: (project: config.ProjectEntry) => void;
  }
  let { onAiSetup, onMaintainer }: Props = $props();

  let addError = $state<string | null>(null);
  let busy = $state(false);
  // Per-project detected kind (openMcp repos also get a Maintainer button).
  let kinds = $state<Record<string, config.ProjectKind>>({});

  async function refreshKinds(): Promise<void> {
    const next: Record<string, config.ProjectKind> = {};
    for (const p of appState.projects) {
      try {
        next[p.id] = await config.detectProjectKind(p.path);
      } catch {
        next[p.id] = "custom";
      }
    }
    kinds = next;
  }

  $effect(() => {
    // Recompute kinds whenever the project list changes.
    void appState.projects.length;
    void refreshKinds();
  });

  async function pickAndAdd(): Promise<void> {
    addError = null;
    busy = true;
    try {
      const selected = await open({
        directory: true,
        multiple: false,
        title: "Select a Godot project folder (contains project.godot)",
      });
      if (typeof selected !== "string") return;
      const result = await appState.addProject(selected);
      if (!result.ok) {
        addError = result.message ?? `Could not add project (${result.errorLabel}).`;
      }
    } catch (e) {
      addError = String(e);
    } finally {
      busy = false;
    }
  }

  async function remove(id: string): Promise<void> {
    // remove_project returns Result<ProjectsFile, String> and rejects when projects.json cannot be
    // written. Unhandled, that rejection left the row in the list, the file unchanged on disk, and no
    // feedback at all — clicking "Remove" just appeared to do nothing. Surface it in the existing
    // error banner.
    try {
      addError = null;
      await appState.removeProject(id);
    } catch (e) {
      addError = `Could not remove the project: ${String(e)}`;
    }
  }

  async function reveal(path: string): Promise<void> {
    try {
      await config.revealPath(path);
    } catch {
      /* best effort */
    }
  }
</script>

<section class="projects">
  <div class="head">
    <h2>Projects</h2>
    <Button variant="primary" disabled={busy} onclick={pickAndAdd}>
      + Add project
    </Button>
  </div>

  {#if addError}
    <div class="err" role="alert">{addError}</div>
  {/if}

  {#if appState.loading}
    <p class="muted">Loading…</p>
  {:else if appState.projects.length === 0}
    <div class="empty">
      <p>No projects yet.</p>
      <p class="muted">
        Add a folder that contains a <code>project.godot</code> file to get
        started.
      </p>
    </div>
  {:else}
    <ul class="list">
      {#each appState.projects as p (p.id)}
        <li class="item" class:selected={p.id === appState.selectedId}>
          <button
            class="pick"
            onclick={() => appState.select(p.id)}
            title="Select project"
          >
            <span class="pname">{p.name}</span>
            <span class="ppath">{p.path}</span>
          </button>
          <div class="actions">
            <Button variant="primary" onclick={() => onAiSetup(p)}>
              AI Setup
            </Button>
            {#if kinds[p.id] === "openMcp"}
              <Button onclick={() => onMaintainer(p)}>Maintainer</Button>
            {/if}
            <Button variant="ghost" onclick={() => reveal(p.path)} title="Reveal in file manager">
              Reveal
            </Button>
            <Button variant="danger" onclick={() => remove(p.id)} title="Remove from Hub">
              Remove
            </Button>
          </div>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .projects {
    padding: 20px 24px;
    max-width: 980px;
    margin: 0 auto;
  }
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 14px;
  }
  .muted {
    color: var(--gom-text-dim);
  }
  .err {
    background: color-mix(in srgb, var(--gom-err) 15%, transparent);
    border: 1px solid var(--gom-err);
    color: var(--gom-text);
    border-radius: var(--gom-radius-sm);
    padding: 10px 12px;
    margin-bottom: 14px;
    font-size: 13px;
  }
  .empty {
    border: 1px dashed var(--gom-border-strong);
    border-radius: var(--gom-radius);
    padding: 32px;
    text-align: center;
  }
  .list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .item {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 12px;
    background: var(--gom-bg-elevated);
    border: 1px solid var(--gom-border);
    border-radius: var(--gom-radius);
  }
  .item.selected {
    border-color: var(--gom-accent);
  }
  .pick {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 2px;
    background: none;
    border: none;
    color: inherit;
    text-align: left;
    cursor: pointer;
    min-width: 0;
  }
  .pname {
    font-weight: 600;
  }
  .ppath {
    color: var(--gom-text-faint);
    font-size: 12px;
    font-family: var(--gom-mono);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 100%;
  }
  .actions {
    display: flex;
    gap: 6px;
    flex: 0 0 auto;
  }
</style>
