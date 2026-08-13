// P18.4 — Godot editor modal dialog policy taxonomy + per-dialog button
// selection. Pure module: no I/O, no platform calls, no globals. The tables are
// the contract; dialog-dismiss.ts consumes them and the per-OS scripts embed
// them.
//
// Adapted from Unity Open MCP's mcp-server/src/dialog-policy.ts (adapt for the
// policy enum shape, the per-kind-per-policy button-preference table pattern,
// the normalize/classify helpers, and parseDialogPolicy). Intentional deltas:
//   - 5-variant policy taxonomy instead of Unity's 6: `ignore` (default),
//     `auto`, `recover`, `cancel`, `manual`. Godot has no Safe Mode, so Unity's
//     `safe-mode` policy is dropped. `recover` covers the "accept the
//     forward-progress / disk-version choice" intent instead.
//   - Godot modal kinds (not Unity's launch_errors / non_matching_editor /
//     project_upgrade / auto_graphics_api / scene_modified_externally /
//     unsaved_scene_changes): `unsaved_changes` (Godot save/reload scene
//     prompt), `reimport` (Godot reimport prompt), `script_reload` (Godot
//     script-reload prompt). Unity-specific signatures are NOT ported.
//   - The destructive guard keys off a single opt-in
//     (GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS=1) for `unsaved_changes` — Godot
//     folds the "modified externally" + "save changes" variants under one kind
//     because they share the same surface and are both judgment-laden.

// ---------------------------------------------------------------------------
// Policy taxonomy
// ---------------------------------------------------------------------------

/**
 * The 5-variant dialog-dismiss policy taxonomy. Each workflow picks a different
 * button on the same Godot modal:
 *
 *   - `ignore` (default) — never click anything. The probe still DETECTS a
 *     blocking modal and reports it (so an agent/operator learns WHY a run is
 *     stuck), but performs no desktop action. Safer than Unity's `ignore`
 *     (which does click) because Godot modals are less uniform and the feature
 *     is opt-in by nature.
 *   - `auto` — click the safest forward-progress button per dialog (Reimport
 *     on a reimport prompt, Reload on a script-reload prompt; Save on an
 *     unsaved-changes prompt only when the destructive opt-in is set).
 *   - `recover` — same as `auto` for Godot's three kinds (Godot has no crash-
 *     recovery dialog like Unity's "Load Recovery"); kept as a distinct value
 *     so an operator can express intent and a future recovery modal slots in
 *     without a rename.
 *   - `cancel` — fail-fast: click Cancel / Don't Save / Close / No everywhere.
 *   - `manual` — never click, and do not even DETECT-then-report as an action
 *     (the feature is fully opted out). Distinct from `ignore` so telemetry /
 *     tool output can tell "detect-only" from "fully off".
 *
 * `unsaved_changes` is destructive under every policy ("Don't Save" loses work,
 * "Save" persists potentially-unwanted state), so no policy value auto-dismisses
 * it. A separate opt-in switch (`GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS=1`) is
 * required before ANY policy may click on that kind. See {@link isUnsavedBlocked}.
 */
export type DialogPolicy = "ignore" | "auto" | "recover" | "cancel" | "manual";

/** All valid policy values, lowercased. */
export const DIALOG_POLICY_VALUES: readonly DialogPolicy[] = [
  "ignore",
  "auto",
  "recover",
  "cancel",
  "manual",
];

/**
 * The policy that performs no desktop action by default. Used as the default
 * when the env var is unset or invalid, and when the tool is called without a
 * `policy` argument.
 */
export const DEFAULT_DIALOG_POLICY: DialogPolicy = "ignore";

/** Env var name that selects the policy. */
export const DIALOG_POLICY_ENV = "GODOT_OPEN_MCP_DIALOG_POLICY";

/**
 * Parse the policy from the environment. Unset / empty → default. Invalid →
 * default + a one-line warning on the supplied sink so a typo does not silently
 * downgrade the user's stated intent. Pure aside from the optional warning.
 */
export function parseDialogPolicy(
  env: NodeJS.ProcessEnv = process.env,
  warn: (msg: string) => void = defaultWarn,
): DialogPolicy {
  const raw = env[DIALOG_POLICY_ENV];
  if (raw === undefined || raw === "") return DEFAULT_DIALOG_POLICY;
  const norm = raw.trim().toLowerCase();
  if ((DIALOG_POLICY_VALUES as readonly string[]).includes(norm)) {
    return norm as DialogPolicy;
  }
  warn(
    `[godot-open-mcp] invalid ${DIALOG_POLICY_ENV}=${JSON.stringify(raw)}; ` +
      `expected one of ${DIALOG_POLICY_VALUES.join(", ")}. ` +
      `Falling back to '${DEFAULT_DIALOG_POLICY}'.`,
  );
  return DEFAULT_DIALOG_POLICY;
}

function defaultWarn(msg: string): void {
  // console.warn so it lands on stderr alongside the probe audit lines.
  console.warn(msg);
}

// ---------------------------------------------------------------------------
// Dialog kinds (Godot modal families)
// ---------------------------------------------------------------------------

/**
 * The Godot editor modal families this helper knows how to classify and (under
 * a non-`ignore`/`manual` policy + the destructive opt-in where required)
 * dismiss. Each maps to a window-title keyword set (see
 * {@link DIALOG_TITLE_FRAGMENTS}) and a per-policy button preference table (see
 * {@link preferenceTokensForPolicy}).
 *
 * The set is intentionally narrow — the three modal families the plan calls out
 * as the ones that most often jam a long autonomous run:
 *   - `unsaved_changes` — Godot's "Save Changes?" / scene-modified prompt.
 *     DESTRUCTIVE (data loss either way); blocked unless the dedicated opt-in
 *     is set.
 *   - `reimport` — Godot's reimport prompt (asset settings changed, or an
 *     external process rewrote a `.import`). Safe to accept under auto/recover.
 *   - `script_reload` — Godot's script-reload prompt (a `.gd`/`.cs` changed on
 *     disk while the editor held it open). Safe to accept under auto/recover.
 */
export type DialogKind = "unsaved_changes" | "reimport" | "script_reload";

/**
 * Window-title fragments per dialog kind. Case-insensitive substring match
 * against the normalized title (alphanumeric-only, lowercased — see
 * {@link normalizeDialogLabel}). Godot's actual dialog titles vary by version
 * and localization; the fragments are heuristic and deliberately broad on the
 * rare side (a fragment that is too generic risks matching an unrelated window,
 * so `reimport`/`reload` are kept as the anchor tokens and combined with the
 * Godot-process ownership check the per-OS probe performs).
 */
export const DIALOG_TITLE_FRAGMENTS: Readonly<Record<DialogKind, readonly string[]>> = {
  // "Save Changes?" / "There are unsaved changes" / "Scene has been modified
  // externally" — Godot surfaces these when a mutating tool leaves a scene
  // dirty and a reload/close fires the native save prompt, or when an external
  // process (git checkout, codegen) rewrote a `.tscn` the editor has open.
  // Destructive under every policy → blocked unless the dedicated opt-in is set.
  unsaved_changes: [
    "savechanges",
    "unsavedchanges",
    "savethescene",
    "savemodifiedscenes",
    "savemodified",
    "doyouwanttosave",
    "modifiedexternally",
    "fileondiskwasmodified",
  ],
  // "Reimport" / "Reimport affected resources" — Godot's importer-settings
  // changed dialog, or a `.import` rewrite. Safe to accept under auto/recover.
  reimport: ["reimport"],
  // "Reload" / "Script has been reloaded" — Godot's reload-tool prompt when a
  // `.gd`/`.cs` changed on disk while the editor held it open. Safe to accept
  // under auto/recover. "reload" alone is broad, but the per-OS probe scopes
  // the match to Godot-owned windows, which keeps false positives low.
  script_reload: ["reloadtool", "scriptreloaded", "hasbeenreloaded", "reload"],
};

/**
 * Classify a Godot window title into a known dialog kind, or `null` when it
 * does not match any. Pure / case-insensitive. Kinds are checked in declaration
 * order; the first fragment match wins. `script_reload` is checked LAST so its
 * broad "reload" token cannot shadow a more specific match.
 */
export function classifyDialogTitle(title: string): DialogKind | null {
  const norm = normalizeDialogLabel(title);
  if (norm === "") return null;
  // Check the specific kinds first; script_reload last (broadest token).
  const order: DialogKind[] = ["unsaved_changes", "reimport", "script_reload"];
  for (const kind of order) {
    for (const frag of DIALOG_TITLE_FRAGMENTS[kind]) {
      if (norm.includes(frag)) return kind;
    }
  }
  return null;
}

/**
 * Normalize a label for matching: strip every non-alphanumeric char and
 * lowercase. Mirrors Unity's `normalize_dialog_label` (and the per-OS scripts'
 * own `Norm`/normalize helpers). "Save Changes?" → "savechanges"; "Re-import"
 * → "reimport".
 */
export function normalizeDialogLabel(value: string): string {
  let out = "";
  for (let i = 0; i < value.length; i++) {
    const ch = value.charCodeAt(i);
    // ASCII alphanumeric only — keeps the matcher locale-independent and
    // punctuation-insensitive (Godot decorates titles with ?, !, ...).
    if (
      (ch >= 48 && ch <= 57) || // 0-9
      (ch >= 65 && ch <= 90) || // A-Z
      (ch >= 97 && ch <= 122) // a-z
    ) {
      out += value[i].toLowerCase();
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// Per-kind-per-policy button preference tables
// ---------------------------------------------------------------------------

/**
 * The normalized button-label tokens a policy prefers for a given dialog kind,
 * in priority order. The first token that matches a button actually present on
 * the dialog wins. `null` means "this policy does not click on this kind" (the
 * probe reports `detected`/`blocked` instead of clicking).
 *
 * `unsaved_changes` is gated: even when a policy WOULD click (auto/recover), this
 * function returns `null` unless `allowUnsavedDismiss` is true. See
 * {@link isUnsavedBlocked}.
 *
 * `ignore` and `manual` return `null` for every kind — they never click. They
 * differ only in that `ignore` still DETECTS and reports the modal (detect-only),
 * while `manual` opts out entirely.
 */
export function preferenceTokensForPolicy(
  kind: DialogKind,
  policy: DialogPolicy,
  allowUnsavedDismiss = false,
): readonly string[] | null {
  // unsaved_changes: destructive under every policy; requires the opt-in.
  if (kind === "unsaved_changes" && !allowUnsavedDismiss) return null;
  switch (kind) {
    case "unsaved_changes":
      return unsavedChangesTokens(policy);
    case "reimport":
      return reimportTokens(policy);
    case "script_reload":
      return scriptReloadTokens(policy);
  }
}

function unsavedChangesTokens(policy: DialogPolicy): readonly string[] | null {
  // Only reached when allowUnsavedDismiss === true. Prefer Save (preserve work)
  // over Don't Save (discard). "Save All" covers the multi-resource variant.
  // For the "modified externally" sub-case the on-disk version is what we want,
  // but Godot surfaces it through the same prompt; "Reload"/"Revert" are listed
  // lower priority so a present Save still wins for the plain save prompt.
  switch (policy) {
    case "auto":
    case "recover":
      return ["save", "saveall", "savechanges", "reload", "revert", "ok", "yes"];
    case "cancel":
      return ["dontsave", "dontsaveall", "cancel", "close", "no"];
    case "ignore":
    case "manual":
      return null;
  }
}

function reimportTokens(policy: DialogPolicy): readonly string[] | null {
  switch (policy) {
    case "auto":
    case "recover":
      return ["reimport", "ok", "yes", "continue"];
    case "cancel":
      return ["cancel", "close", "no"];
    case "ignore":
    case "manual":
      return null;
  }
}

function scriptReloadTokens(policy: DialogPolicy): readonly string[] | null {
  switch (policy) {
    case "auto":
    case "recover":
      return ["reload", "reloadnow", "ok", "continue", "yes"];
    case "cancel":
      return ["cancel", "close", "no"];
    case "ignore":
    case "manual":
      return null;
  }
}

/**
 * Generic per-policy fallback token list, applied when a dialog title does NOT
 * match any known kind (an unknown Godot modal). `ignore` and `manual` return
 * `[]` (no click) so the caller can treat "no preference" uniformly.
 */
export function genericFallbackTokens(policy: DialogPolicy): readonly string[] {
  switch (policy) {
    case "auto":
      return ["ignore", "continue", "confirm", "reimport", "reload", "ok", "yes", "save"];
    case "recover":
      return ["reload", "revert", "recover", "restore", "continue", "ok", "yes", "save"];
    case "cancel":
      return ["cancel", "quit", "close", "no"];
    case "ignore":
    case "manual":
      return [];
  }
}

/**
 * Whether the policy clicks AT ALL. `ignore` and `manual` never click — the
 * probe runs detect-only (ignore) or is skipped entirely (manual).
 */
export function policyClicks(policy: DialogPolicy): boolean {
  return policy === "auto" || policy === "recover" || policy === "cancel";
}

/**
 * The kinds a policy explicitly declines to click. Used by the probe to report a
 * `blocked` outcome (audit line, no click) instead of silently ignoring the
 * dialog. Currently `unsaved_changes` under the default (no opt-in) — the
 * destructive-mutation guard.
 */
export function blockedKindsForPolicy(
  _policy: DialogPolicy,
  allowUnsavedDismiss = false,
): readonly DialogKind[] {
  if (!allowUnsavedDismiss) return ["unsaved_changes"];
  return [];
}

/**
 * Whether an unsaved-changes dismissal would be blocked under the given flags.
 * True unless the dedicated opt-in is set — independent of policy because
 * "Don't Save" loses work and "Save" persists unwanted state under every policy.
 */
export function isUnsavedBlocked(
  _policy: DialogPolicy,
  allowUnsavedDismiss = false,
): boolean {
  return !allowUnsavedDismiss;
}

/**
 * Select the button to click on a dialog, given its kind, the visible button
 * labels, the active policy, and the unsaved-dismiss opt-in.
 *
 * Returns `{ button, token }` — `button` is the original (un-normalized) label
 * from `buttonLabels` that matched the highest-priority token; `token` is the
 * normalized token that won. Returns `null` when the policy declines this kind
 * (ignore/manual, or unsaved_changes without the opt-in), or when no preferred
 * token matches any visible button.
 *
 * Pure.
 */
export function preferredDialogButtonLabel(
  kind: DialogKind,
  buttonLabels: readonly string[],
  policy: DialogPolicy,
  opts: { allowUnsavedDismiss?: boolean } = {},
): { button: string; token: string } | null {
  const allowUnsavedDismiss = opts.allowUnsavedDismiss ?? false;
  const tokens = preferenceTokensForPolicy(kind, policy, allowUnsavedDismiss);
  if (tokens === null) return null;
  return matchTokens(tokens, buttonLabels);
}

/**
 * Select a button on an UNKNOWN dialog using the generic per-policy fallback.
 * Returns `null` when the policy is `ignore`/`manual` (empty token list) or no
 * token matches. Pure.
 */
export function preferredGenericButtonLabel(
  buttonLabels: readonly string[],
  policy: DialogPolicy,
): { button: string; token: string } | null {
  const tokens = genericFallbackTokens(policy);
  if (tokens.length === 0) return null;
  return matchTokens(tokens, buttonLabels);
}

/**
 * Find the visible button whose normalized label best matches a policy token.
 * Returns the original label + winning token.
 *
 * Two passes, token-priority order in each:
 *   1. EXACT match — a button whose normalized label equals the token. This is
 *      preferred so token `save` matches button "Save" (norm `save`) and NOT
 *      "Don't Save" (norm `dontsave`, which substring-contains `save`). Without
 *      this exact-first pass, destructive "Don't Save" / "Don't Save All"
 *      buttons would shadow their safe counterparts.
 *   2. SUBSTRING fallback — a button whose normalized label contains the token.
 *      Catches labels with extra decoration (e.g. "Save All" → norm `saveall`
 *      contains `save`) when no exact match exists.
 */
function matchTokens(
  tokens: readonly string[],
  buttonLabels: readonly string[],
): { button: string; token: string } | null {
  const normalized = buttonLabels.map((label) => ({
    raw: label,
    norm: normalizeDialogLabel(label),
  }));
  // Pass 1: exact normalized match.
  for (const token of tokens) {
    const hit = normalized.find((n) => n.norm === token);
    if (hit) return { button: hit.raw, token };
  }
  // Pass 2: substring fallback.
  for (const token of tokens) {
    const hit = normalized.find((n) => n.norm.includes(token));
    if (hit) return { button: hit.raw, token };
  }
  return null;
}

/**
 * Serialize the per-kind token table + blocked-kinds set into a compact JSON
 * blob embedded in the per-OS scripts. The script classifies each candidate
 * window title (after normalizing it the same way {@link normalizeDialogLabel}
 * does) and either clicks the first matching token or reports `blocked` /
 * `detected`.
 *
 * Shape (kept narrow so the PowerShell / AppleScript parsers stay simple):
 *   {
 *     kinds: { "<kind>": { fragments: string[], tokens: string[] | null } },
 *     blocked: ["<kind>", ...],
 *     genericTokens: string[],   // for unknown titles
 *     click: boolean             // whether the policy clicks at all
 *   }
 *
 * `tokens: null` means "this policy declines this kind" (ignore/manual, or the
 * unsaved_changes opt-in gate). The script reports `detected` for a matched
 * kind whose tokens are null but the policy still detects (ignore), `blocked`
 * for a kind in `blocked`, and `dismissed` when it clicks.
 */
export function buildTokenTable(
  policy: DialogPolicy,
  allowUnsavedDismiss = false,
): {
  kinds: Record<string, { fragments: string[]; tokens: string[] | null }>;
  blocked: string[];
  genericTokens: string[];
  click: boolean;
} {
  const kinds: Record<string, { fragments: string[]; tokens: string[] | null }> = {};
  for (const kind of Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[]) {
    const tokens = preferenceTokensForPolicy(kind, policy, allowUnsavedDismiss);
    kinds[kind] = {
      fragments: [...DIALOG_TITLE_FRAGMENTS[kind]],
      tokens: tokens === null ? null : [...tokens],
    };
  }
  return {
    kinds,
    blocked: [...blockedKindsForPolicy(policy, allowUnsavedDismiss)],
    genericTokens: [...genericFallbackTokens(policy)],
    click: policyClicks(policy),
  };
}
