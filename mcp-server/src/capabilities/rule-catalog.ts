// Capability-discovery rule + fix catalog (P3.8).
//
// Static metadata for the verify rules and fix providers, mirroring the C# verify package
// (packages/verify/Editor/Rules/*) issue mappers and the fix registry
// (packages/verify/Editor/Fixes/FixProviderRegistry.cs). This is the documented capability surface
// agents discover via `godot_open_mcp_capabilities`. The catalog is versioned with the package:
// `implemented` flags reflect what ships in the matching bridge/verify package release.
//
// Adapted from Unity Open MCP's mcp-server/src/capabilities/rule-catalog.ts (copy for the catalog
// contract / types; the rule/fix ENTRIES are Godot-specific). Intentional deltas for v1:
//   - Godot ships four implemented rules (broken_references, missing_scripts, import_health,
//     project_health) and four implemented fixes (remove_missing_script, relink_broken_reference,
//     remove_orphan_import, fix_duplicate_uid). Unity's catalog carries many more rules
//     (missing_references, scene_prefab_health, materials, shader_analysis, ...) and more fixes; the
//     remaining Unity rules are omitted here because the Godot verify package does not implement them
//     yet (P14 adds them incrementally). There are no `planned` entries — when a rule is stubbed but
//     not built, add it with implemented:false + guidance so agents get a structured "not yet
//     available" signal.
//   - Unity's RuleIssueDescriptor carries rootCause + remediation fields (from an IssueExplainability
//     taxonomy). Godot has no such taxonomy yet, so those fields are omitted (they are additive and
//     safe to add later without breaking the contract).
//
// KEEP IN SYNC with the C# verify package on every rule/fix change — the drift-detection test in
// rule-catalog.test.ts pins the issue codes / severities / fix mappings against the C# constants
// (packages/verify/Editor/Rules/*/IssueCodes.cs + Fixes/RemoveMissingScriptFix.cs).

export type CapabilityStatus = "implemented" | "planned";

/**
 * One issue code a rule can emit. Mirrors the C# `IssueCodes` constants + the per-issue severity set
 * in the rule's `Scan`.
 */
export interface RuleIssueDescriptor {
  /** Issue code emitted by the rule (e.g. `missing_script`). Matches the C# `IssueCodes` constant. */
  code: string;
  /** Default severity (`Error` | `Warning`) — matches the severity the rule's `MakeIssue` sets. */
  severity: "Error" | "Warning";
  /** Fix IDs that can resolve this issue code, if any (empty when no fix provider handles it yet). */
  fixIds: string[];
}

/** A verify rule's capability surface: its id, applicable asset kinds, and the issue codes it emits. */
export interface RuleCapability {
  /** Stable rule id — matches the C# `IVerifyRule.Id` / `RuleId` const (e.g. `missing_scripts`). */
  id: string;
  /** Human-readable title. */
  title: string;
  /** One-line description of what the rule detects. */
  description: string;
  /** Asset kinds the rule analyzes (e.g. `scene`, `resource`). */
  applicableAssetKinds: string[];
  /** File extensions the rule applies to, when known. */
  applicableExtensions?: string[];
  /** True when the rule is built and registered in `VerifyRunner.RegisterDefaults`. */
  implemented: boolean;
  /** Implemented vs planned. */
  status: CapabilityStatus;
  /** Issue codes this rule can emit. Empty for planned rules. */
  issues: RuleIssueDescriptor[];
  /** Guidance shown to the agent when the rule is not implemented. */
  guidance?: string;
}

/** A fix provider's capability surface: which issue codes it resolves and whether it is safe to auto-apply. */
export interface FixCapability {
  /** Stable fix id — matches the C# `IFixProvider.FixId` (e.g. `remove_missing_script`). */
  id: string;
  /** True when the provider is built and registered in `FixProviderRegistry.RegisterDefaults`. */
  implemented: boolean;
  /** Implemented vs planned. */
  status: CapabilityStatus;
  /** Rule IDs this fix can resolve issues for. */
  rules: string[];
  /** Issue codes this fix addresses. */
  issueCodes: string[];
  /** True when the fix is safe to auto-apply (no destructive side effects). Mirrors `IFixProvider.Describe().Safe`. */
  safe: boolean;
  /** Guidance shown to the agent when the fix is not implemented. */
  guidance?: string;
}

// ---------------------------------------------------------------------------
// Implemented rules — mirror packages/verify/Editor/Rules/*
// ---------------------------------------------------------------------------

const BROKEN_REFERENCES_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "broken_scene_reference",
    // Error: a broken reference can fail scene load or silently drop a node/resource. Matches
    // BrokenReferencesRule.MakeIssue (severity Error).
    severity: "Error",
    // No fix provider yet — the eventual relink/remove fix would land here.
    fixIds: ["relink_broken_reference"],
  },
];

const MISSING_SCRIPTS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "missing_script",
    // Error: a node runs without its intended behavior (or fails to load in strict modes). Matches
    // MissingScriptsRule.MakeIssue (severity Error).
    severity: "Error",
    fixIds: ["remove_missing_script"],
  },
];

const IMPORT_HEALTH_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "orphan_import",
    // Warning: an orphan .import sidecar does not break scene load (the engine ignores it after a
    // rescan) — it is project-level cruft. Matches ImportHealthRule.MakeOrphanIssue (severity Warning).
    severity: "Warning",
    fixIds: ["remove_orphan_import"],
  },
  {
    code: "duplicate_uid",
    // Error: a uid collision is a real integrity break — Godot refuses to reimport or silently picks
    // one. Matches ImportHealthRule.MakeDuplicateUidIssue (severity Error).
    severity: "Error",
    fixIds: ["fix_duplicate_uid"],
  },
];

const PROJECT_HEALTH_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "project_empty_folder",
    // Warning: an empty folder is cruft, not a load break. Matches ProjectHealthRule's empty-folder
    // finding (severity Warning). No fix provider yet — folder lifecycle fixes land in a later phase.
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "project_uid_only_folder",
    // Warning: a folder of only .gd.uid sidecars is stale metadata (scripts moved away). Godot-specific
    // (Unity's twin is project_meta_only_folder). Matches ProjectHealthRule's uid-only-folder finding
    // (severity Warning).
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "project_deep_nesting",
    // Warning: folder depth > 8 is a maintainability signal, not an integrity break. Matches
    // ProjectHealthRule's deep-nesting finding (severity Warning).
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "project_large_folder",
    // Warning: > 200 direct children is a maintainability/perf signal. Matches ProjectHealthRule's
    // large-folder finding (severity Warning).
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "project_broken_asset",
    // Error: a .tres/.tscn that fails to parse can fail scene load or silently drop resources — a real
    // integrity break. Matches ProjectHealthRule's broken-asset finding (severity Error).
    severity: "Error",
    fixIds: [],
  },
  {
    code: "project_empty_scene",
    // Warning: a .tscn with only a root node is cruft (a created-but-never-populated scene), not a load
    // break. Matches ProjectHealthRule's empty-scene finding (severity Warning).
    severity: "Warning",
    fixIds: [],
  },
];

// ---------------------------------------------------------------------------
// Full catalog
// ---------------------------------------------------------------------------

export const RULE_CATALOG: RuleCapability[] = [
  {
    id: "broken_references",
    title: "Broken references",
    description:
      "Detects unresolved [ext_resource] declarations (missing path/uid) and dangling " +
      "ExtResource(\"id\")/SubResource(\"id\") usages inside .tscn/.tres files.",
    applicableAssetKinds: ["scene", "resource"],
    applicableExtensions: [".tscn", ".tres"],
    implemented: true,
    status: "implemented",
    issues: BROKEN_REFERENCES_ISSUES,
  },
  {
    id: "missing_scripts",
    title: "Missing scripts",
    description:
      "Detects nodes whose script = ExtResource(\"id\") attachment could not be resolved — the " +
      "script was deleted/moved, or the usage id was never declared (a partial edit).",
    applicableAssetKinds: ["scene", "resource"],
    applicableExtensions: [".tscn", ".tres"],
    implemented: true,
    status: "implemented",
    issues: MISSING_SCRIPTS_ISSUES,
  },
  {
    id: "import_health",
    title: "Import health",
    description:
      "Detects orphan .import sidecars (source file deleted) and duplicate uid declarations across " +
      ".import sidecars.",
    applicableAssetKinds: ["import"],
    applicableExtensions: [".import"],
    implemented: true,
    status: "implemented",
    issues: IMPORT_HEALTH_ISSUES,
  },
  {
    id: "project_health",
    title: "Project health",
    description:
      "Offline project-wide structural integrity: empty folders, uid-sidecar-only folders, deep " +
      "folder nesting, oversized flat folders, structurally broken .tres/.tscn assets, and " +
      "root-only (empty) scenes.",
    applicableAssetKinds: ["folder", "scene", "resource"],
    applicableExtensions: [".tscn", ".tres"],
    implemented: true,
    status: "implemented",
    issues: PROJECT_HEALTH_ISSUES,
  },
];

// ---------------------------------------------------------------------------
// Fix capability entries.
//
// Mirrors the C# FixProviderRegistry.RegisterDefaults. P3.7 ships remove_missing_script;
// P13.3 adds relink_broken_reference, remove_orphan_import, fix_duplicate_uid.
// ---------------------------------------------------------------------------

export const FIX_CATALOG: FixCapability[] = [
  {
    id: "remove_missing_script",
    implemented: true,
    status: "implemented",
    rules: ["missing_scripts"],
    issueCodes: ["missing_script"],
    safe: true,
  },
  {
    id: "relink_broken_reference",
    implemented: true,
    status: "implemented",
    rules: ["broken_references"],
    issueCodes: ["broken_scene_reference"],
    safe: false,
  },
  {
    id: "remove_orphan_import",
    implemented: true,
    status: "implemented",
    rules: ["import_health"],
    issueCodes: ["orphan_import"],
    safe: true,
  },
  {
    id: "fix_duplicate_uid",
    implemented: true,
    status: "implemented",
    rules: ["import_health"],
    issueCodes: ["duplicate_uid"],
    safe: false,
  },
];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

export function implementedRules(): RuleCapability[] {
  return RULE_CATALOG.filter((r) => r.implemented);
}

export function plannedRules(): RuleCapability[] {
  return RULE_CATALOG.filter((r) => !r.implemented);
}

export function implementedFixes(): FixCapability[] {
  return FIX_CATALOG.filter((f) => f.implemented);
}

export function plannedFixes(): FixCapability[] {
  return FIX_CATALOG.filter((f) => !f.implemented);
}
