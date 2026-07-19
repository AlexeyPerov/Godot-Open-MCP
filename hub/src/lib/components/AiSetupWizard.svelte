<script lang="ts">
  import { onMount } from "svelte";
  import type { ProjectEntry } from "$lib/services/config.ts";
  import { WizardState } from "$lib/state/wizard.svelte.ts";
  import { appState } from "$lib/state/app.svelte.ts";
  import Button from "$lib/components/shell/Button.svelte";
  import WizardShell from "$lib/components/wizard/WizardShell.svelte";
  import Step0Preset from "$lib/components/wizard/Step0Preset.svelte";
  import Step1Detection from "$lib/components/wizard/Step1Detection.svelte";
  import Step2McpSource from "$lib/components/wizard/Step2McpSource.svelte";
  import Step3Install from "$lib/components/wizard/Step3Install.svelte";
  import Step4McpClient from "$lib/components/wizard/Step4McpClient.svelte";
  import Step4bSkill from "$lib/components/wizard/Step4bSkill.svelte";
  import Step5Launch from "$lib/components/wizard/Step5Launch.svelte";
  import StepDone from "$lib/components/wizard/StepDone.svelte";

  interface Props {
    project: ProjectEntry;
    onClose: () => void;
  }
  let { project, onClose }: Props = $props();

  const w = new WizardState();

  onMount(() => {
    void (async () => {
      // Seed source from saved settings as a sensible default.
      w.source = appState.settings.mcpSource;
      w.clientId = appState.settings.defaultClientId ?? "cursor";
      w.editorPath = appState.settings.godotEditorPath;
      await w.init(project);
      await w.onEnterStep();
    })();
  });

  const isLast = $derived(w.stepId === "done");

  async function finish(): Promise<void> {
    w.finish();
    await appState.markOpened(project.id);
    onClose();
  }
</script>

<WizardShell projectName={w.projectName} index={w.index} {onClose}>
  {#snippet body()}
    {#if w.error}
      <div class="banner">{w.error}</div>
    {/if}
    {#if w.stepId === "step0"}
      <Step0Preset {w} />
    {:else if w.stepId === "step1"}
      <Step1Detection {w} />
    {:else if w.stepId === "step2"}
      <Step2McpSource {w} />
    {:else if w.stepId === "step3"}
      <Step3Install {w} />
    {:else if w.stepId === "step4"}
      <Step4McpClient {w} />
    {:else if w.stepId === "step4b"}
      <Step4bSkill {w} />
    {:else if w.stepId === "step5"}
      <Step5Launch {w} />
    {:else}
      <StepDone {w} />
    {/if}
  {/snippet}

  {#snippet footer()}
    <Button variant="ghost" disabled={w.index === 0 || w.busy} onclick={() => w.back()}>
      ← Back
    </Button>
    {#if isLast}
      <Button variant="primary" onclick={finish}>Finish</Button>
    {:else}
      <Button
        variant="primary"
        disabled={w.busy || !w.canAdvance()}
        onclick={() => w.next()}
      >
        Next →
      </Button>
    {/if}
  {/snippet}
</WizardShell>

<style>
  .banner {
    background: color-mix(in srgb, var(--gom-err) 15%, transparent);
    border: 1px solid var(--gom-err);
    border-radius: var(--gom-radius-sm);
    padding: 8px 12px;
    margin-bottom: 12px;
    font-size: 13px;
  }
</style>
