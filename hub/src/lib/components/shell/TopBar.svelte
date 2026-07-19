<script lang="ts">
  import { APP_NAME, APP_VERSION } from "$lib/version.ts";
</script>

<header class="topbar" data-tauri-drag-region>
  <div class="brand" data-tauri-drag-region>
    <span class="logo" aria-hidden="true">◆</span>
    <span class="name">{APP_NAME}</span>
    <span class="ver">v{APP_VERSION}</span>
  </div>
</header>

<style>
  .topbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: 46px;
    padding: 0 16px 0 84px; /* leave room for macOS traffic lights */
    background: var(--gom-bg-elevated);
    border-bottom: 1px solid var(--gom-border);
    user-select: none;
    flex: 0 0 auto;
  }
  .brand {
    display: flex;
    align-items: baseline;
    gap: 8px;
  }
  .logo {
    color: var(--gom-accent);
    font-size: 15px;
  }
  .name {
    font-weight: 600;
    font-size: 14px;
  }
  .ver {
    color: var(--gom-text-faint);
    font-size: 11px;
  }
</style>
