/**
 * Central Hub app state (Svelte 5 runes).
 *
 * Holds the project inventory, app settings, and the currently selected
 * project. UI components read `appState` and drive mutations through its
 * methods so the on-disk `projects.json` / `settings.json` stay in sync.
 */

import * as config from "../services/config.ts";
import type {
  AddProjectResult,
  ProjectEntry,
  Settings,
} from "../services/config.ts";

const DEFAULT_SETTINGS: Settings = {
  godotEditorPath: null,
  mcpSource: "npx-published",
  defaultClientId: "cursor",
};

class AppState {
  projects = $state<ProjectEntry[]>([]);
  settings = $state<Settings>({ ...DEFAULT_SETTINGS });
  selectedId = $state<string | null>(null);
  loading = $state(true);
  error = $state<string | null>(null);

  selected = $derived(
    this.projects.find((p) => p.id === this.selectedId) ?? null,
  );

  async init(): Promise<void> {
    this.loading = true;
    try {
      const [settings, projectsFile] = await Promise.all([
        config.loadSettings(),
        config.loadProjects(),
      ]);
      this.settings = settings;
      this.projects = projectsFile.projects;
      if (this.projects.length > 0) {
        this.selectedId = this.projects[0].id;
      }
      this.error = null;
    } catch (e) {
      this.error = String(e);
    } finally {
      this.loading = false;
    }
  }

  async addProject(path: string): Promise<AddProjectResult> {
    const result = await config.addProject(path);
    if (result.ok && result.project) {
      // The backend appended + persisted; reload to reflect canonical order.
      const file = await config.loadProjects();
      this.projects = file.projects;
      this.selectedId = result.project.id;
    }
    return result;
  }

  async removeProject(id: string): Promise<void> {
    const file = await config.removeProject(id);
    this.projects = file.projects;
    if (this.selectedId === id) {
      this.selectedId = this.projects[0]?.id ?? null;
    }
  }

  select(id: string): void {
    this.selectedId = id;
  }

  async saveSettings(patch: Partial<Settings>): Promise<void> {
    this.settings = { ...this.settings, ...patch };
    await config.saveSettings(this.settings);
  }

  async markOpened(id: string): Promise<void> {
    const file = await config.touchProjectOpened(id);
    this.projects = file.projects;
  }
}

export const appState = new AppState();
