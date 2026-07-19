<script lang="ts">
  import type { Snippet } from "svelte";
  import Button from "./Button.svelte";

  interface Props {
    title: string;
    confirmLabel?: string;
    cancelLabel?: string;
    danger?: boolean;
    onConfirm: () => void;
    onCancel: () => void;
    children: Snippet;
  }
  let {
    title,
    confirmLabel = "Confirm",
    cancelLabel = "Cancel",
    danger = false,
    onConfirm,
    onCancel,
    children,
  }: Props = $props();
</script>

<div
  class="backdrop"
  role="button"
  tabindex="0"
  onclick={onCancel}
  onkeydown={(e) => e.key === "Escape" && onCancel()}
>
  <div
    class="modal"
    role="dialog"
    aria-modal="true"
    aria-label={title}
    tabindex="-1"
    onclick={(e) => e.stopPropagation()}
    onkeydown={() => {}}
  >
    <h3>{title}</h3>
    <div class="content">{@render children()}</div>
    <div class="actions">
      <Button variant="ghost" onclick={onCancel}>{cancelLabel}</Button>
      <Button variant={danger ? "danger" : "primary"} onclick={onConfirm}>
        {confirmLabel}
      </Button>
    </div>
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 50;
  }
  .modal {
    background: var(--gom-bg-elevated);
    border: 1px solid var(--gom-border-strong);
    border-radius: var(--gom-radius);
    padding: 20px;
    width: min(520px, 92vw);
    box-shadow: 0 12px 48px rgba(0, 0, 0, 0.5);
  }
  .content {
    color: var(--gom-text-dim);
    font-size: 14px;
    margin: 8px 0 18px;
  }
  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 10px;
  }
</style>
