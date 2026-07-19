<script lang="ts">
  import { onMount } from "svelte";
  import { appState } from "$lib/state/app.svelte.ts";
  import type { ProjectEntry } from "$lib/services/config.ts";
  import TopBar from "$lib/components/shell/TopBar.svelte";
  import ProjectsTab from "$lib/tabs/ProjectsTab.svelte";
  import AiSetupWizard from "$lib/components/AiSetupWizard.svelte";
  import OpenMcpProjectSettings from "$lib/components/project-settings/OpenMcpProjectSettings.svelte";

  type View =
    | { kind: "projects" }
    | { kind: "wizard"; project: ProjectEntry }
    | { kind: "maintainer"; project: ProjectEntry };

  let view = $state<View>({ kind: "projects" });

  onMount(() => {
    void appState.init();
  });

  function openWizard(project: ProjectEntry): void {
    view = { kind: "wizard", project };
  }
  function openMaintainer(project: ProjectEntry): void {
    view = { kind: "maintainer", project };
  }
  function backToProjects(): void {
    view = { kind: "projects" };
  }
</script>

<div class="app">
  <TopBar />
  <main>
    {#if view.kind === "projects"}
      <ProjectsTab onAiSetup={openWizard} onMaintainer={openMaintainer} />
    {:else if view.kind === "wizard"}
      <AiSetupWizard project={view.project} onClose={backToProjects} />
    {:else if view.kind === "maintainer"}
      <OpenMcpProjectSettings project={view.project} onClose={backToProjects} />
    {/if}
  </main>
</div>

<style>
  .app {
    display: flex;
    flex-direction: column;
    height: 100vh;
    overflow: hidden;
  }
  main {
    flex: 1 1 auto;
    overflow: auto;
  }
</style>
