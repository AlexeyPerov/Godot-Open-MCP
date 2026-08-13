// `godot_open_mcp_dialog_policy_set` tool definition (P18.4).
//
// Platform-specific modal/dialog automation. Godot editor modals (unsaved-
// changes save prompt, reimport prompt, script-reload prompt) can block a long
// autonomous run — a mutating tool leaves a scene dirty and Godot surfaces its
// native save prompt, or an external process rewrites a `.tscn`/`.import` and
// Godot asks how to reconcile. This tool is the agent-callable surface to
// DETECT a blocking modal and (under an opted-in policy) DISMISS it by clicking
// the policy-selected button on the OS desktop.
//
// Adapted from Unity Open MCP's dialog-dismiss subsystem (pattern only —
// `mcp-server/src/dialog-dismiss.ts`, `dialog-policy.ts`). Intentional deltas:
//   - Godot modal kinds (unsaved_changes / reimport / script_reload), NOT
//     Unity's launch_errors / non_matching_editor / project_upgrade /
//     auto_graphics_api / scene_modified_externally / unsaved_scene_changes.
//   - 5-variant policy (ignore / auto / recover / cancel / manual); Godot has
//     no Safe Mode, so Unity's `safe-mode` policy is dropped.
//   - Default policy `ignore` is DETECT-ONLY (reports the modal without
//     clicking); Unity's `ignore` clicks. Godot's feature is opt-in by nature.
//   - No launch-wait polling loop. The tool is a one-shot probe the agent calls
//     when a run appears stuck; the decision to re-probe is the agent's.
//
// Route: `local`. The probe shells out to osascript (macOS) / xdotool (Linux/
// X11) / PowerShell BM_CLICK (Windows) directly from the MCP process — no
// bridge round-trip (a blocking modal stalls the bridge's main thread too, so
// the tool may not depend on it). Always-visible meta-tool (no group): an agent
// must reach it to clear a modal that is jamming the very tools it needs.
//
// Safety contract:
//   1. Default policy `ignore` performs NO desktop action — it only DETECTS a
//      blocking modal and reports it. The operator opts in to clicking by
//      setting `policy` to `auto` / `recover` / `cancel` (or the
//      GODOT_OPEN_MCP_DIALOG_POLICY env var).
//   2. `unsaved_changes` is destructive under every policy ("Don't Save" loses
//      work, "Save" persists potentially-unwanted state) — it is blocked unless
//      the GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS=1 env opt-in is set, regardless
//      of policy.
//   3. `detect_only: true` forces detect-only regardless of policy (read-only
//      "what is blocking?" mode — never clicks).
//   4. Graceful no-op on an unsupported platform / missing tooling: returns
//      outcome `unsupported` with the platform note (macOS Accessibility grant;
//      Linux xdotool; Wayland unsupported).

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const dialogPolicySet: Tool = {
  name: "godot_open_mcp_dialog_policy_set",
  description:
    "Detect (and under an opted-in policy, dismiss) a Godot editor modal that " +
      "is blocking a run — the unsaved-changes save prompt, a reimport prompt, " +
      "or a script-reload prompt. Platform-specific desktop automation: macOS " +
      "(AppleScript, needs an Accessibility grant), Linux/X11 (xdotool; " +
      "Wayland unsupported), Windows (Win32 BM_CLICK). Local-routed — no bridge " +
      "round-trip (a blocking modal stalls the bridge too). Use when a tool " +
      "call appears stuck and you suspect a native Godot dialog is up. " +
      "Default policy is `ignore` (DETECT-ONLY — reports the modal without " +
      "clicking); set `policy` to `auto` / `recover` / `cancel` to opt in to " +
      "clicking the policy-selected button, or `manual` to fully opt out. The " +
      "destructive unsaved-changes prompt is blocked unless the " +
      "GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS=1 env opt-in is set. Pass " +
      "`probe: true` to run the detection/dismissal now; pass `policy` (without " +
      "probe) to just set the per-session policy. `detect_only: true` forces " +
      "read-only detection regardless of policy. Returns the effective policy, " +
      "resolved platform, the outcome (dismissed / detected / not_found / " +
      "blocked / unsupported / error), the matched dialog kind, and the button " +
      "clicked or the detected button labels. outcome `unsupported` is a " +
      "graceful no-op (missing tooling / Wayland / unknown platform).",
  inputSchema: {
    type: "object",
    properties: {
      policy: {
        type: "string",
        enum: ["ignore", "auto", "recover", "cancel", "manual"],
        description:
          "Set the per-session dialog-dismiss policy. `ignore` (default) = " +
            "detect-only, never click. `auto` / `recover` = click the safest " +
            "forward-progress button per dialog (Reimport / Reload; Save on the " +
            "unsaved prompt only with the destructive opt-in). `cancel` = click " +
            "Cancel / Don't Save / Close / No. `manual` = fully opted out (no " +
            "detect, no click). When omitted, the active session policy is " +
            "unchanged (resolved from env / default).",
      },
      probe: {
        type: "boolean",
        default: false,
        description:
          "When true, run a one-shot desktop probe NOW: detect any blocking " +
            "Godot modal and, under a clicking policy, dismiss it. When false " +
            "(default), the call only sets the per-session policy (if `policy` " +
            "is given) and returns the resolved policy — no desktop action.",
      },
      detect_only: {
        type: "boolean",
        default: false,
        description:
          "When true (with `probe: true`), force read-only detection — report " +
            "any blocking modal and its buttons WITHOUT clicking, regardless of " +
            "the policy. Use this to see what is blocking before opting in to " +
            "dismissing.",
      },
    },
    additionalProperties: false,
  },
};
