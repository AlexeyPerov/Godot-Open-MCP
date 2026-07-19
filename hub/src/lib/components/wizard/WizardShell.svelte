<script lang="ts">
  import type { Snippet } from "svelte";
  import { STEP_IDS, STEP_TITLES, type StepId } from "$lib/state/wizard.svelte.ts";

  interface Props {
    projectName: string;
    index: number;
    onClose: () => void;
    body: Snippet;
    footer: Snippet;
  }
  let { projectName, index, onClose, body, footer }: Props = $props();

  const steps = STEP_IDS.map((id) => ({ id, title: STEP_TITLES[id as StepId] }));
</script>

<div class="wizard">
  <div class="whead">
    <div>
      <h2>AI Setup</h2>
      <p class="proj">{projectName}</p>
    </div>
    <button class="close" onclick={onClose} title="Back to projects">✕</button>
  </div>

  <ol class="rail" aria-label="Wizard steps">
    {#each steps as s, i (s.id)}
      <li class:done={i < index} class:active={i === index}>
        <span class="dot">{i < index ? "✓" : i + 1}</span>
        <span class="lbl">{s.title}</span>
      </li>
    {/each}
  </ol>

  <div class="wbody">
    {@render body()}
  </div>

  <div class="wfooter">
    {@render footer()}
  </div>
</div>

<style>
  .wizard {
    display: flex;
    flex-direction: column;
    height: 100%;
    max-width: 900px;
    margin: 0 auto;
    padding: 18px 24px 0;
  }
  .whead {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
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
  .close:hover {
    border-color: var(--gom-accent);
    color: var(--gom-text);
  }
  .rail {
    display: flex;
    gap: 6px;
    list-style: none;
    padding: 0;
    margin: 14px 0;
    flex-wrap: wrap;
  }
  .rail li {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: var(--gom-text-faint);
  }
  .rail li:not(:last-child)::after {
    content: "→";
    margin-left: 6px;
    color: var(--gom-border-strong);
  }
  .rail li.active {
    color: var(--gom-text);
  }
  .rail li.done {
    color: var(--gom-ok);
  }
  .dot {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 20px;
    height: 20px;
    border-radius: 50%;
    border: 1px solid currentColor;
    font-size: 11px;
  }
  .active .dot {
    background: var(--gom-accent);
    border-color: var(--gom-accent);
    color: #fff;
  }
  .wbody {
    flex: 1 1 auto;
    overflow: auto;
    padding: 6px 2px 18px;
  }
  .wfooter {
    flex: 0 0 auto;
    display: flex;
    justify-content: space-between;
    gap: 10px;
    padding: 14px 0;
    border-top: 1px solid var(--gom-border);
  }
</style>
