<script lang="ts">
  // Small, dependency-free button used across the shell (Svelte 5 runes).
  import type { Snippet } from "svelte";

  interface Props {
    variant?: "primary" | "secondary" | "ghost" | "danger";
    disabled?: boolean;
    title?: string;
    type?: "button" | "submit";
    onclick?: (e: MouseEvent) => void;
    children: Snippet;
  }

  let {
    variant = "secondary",
    disabled = false,
    title,
    type = "button",
    onclick,
    children,
  }: Props = $props();
</script>

<button class="btn {variant}" {type} {disabled} {title} onclick={onclick}>
  {@render children()}
</button>

<style>
  .btn {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 7px 14px;
    border-radius: var(--gom-radius-sm);
    border: 1px solid var(--gom-border-strong);
    background: var(--gom-bg-elevated);
    color: var(--gom-text);
    font-size: 13px;
    cursor: pointer;
    transition:
      background 0.12s ease,
      border-color 0.12s ease,
      opacity 0.12s ease;
  }
  .btn:hover:not(:disabled) {
    border-color: var(--gom-accent);
  }
  .btn:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }
  .primary {
    background: var(--gom-accent);
    border-color: var(--gom-accent);
    color: #fff;
    font-weight: 600;
  }
  .primary:hover:not(:disabled) {
    background: var(--gom-accent-hover);
  }
  .ghost {
    background: transparent;
    border-color: transparent;
    color: var(--gom-text-dim);
  }
  .ghost:hover:not(:disabled) {
    background: var(--gom-bg-elevated);
    border-color: var(--gom-border);
  }
  .danger {
    background: transparent;
    border-color: var(--gom-err);
    color: var(--gom-err);
  }
  .danger:hover:not(:disabled) {
    background: color-mix(in srgb, var(--gom-err) 15%, transparent);
  }
</style>
