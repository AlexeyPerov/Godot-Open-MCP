// P18.4 — Godot editor modal dialog detection + dismissal.
//
// The desktop-automation half of the dialog-policy subsystem. Pure policy
// tables live in dialog-policy.ts; this module performs the bounded OS probes
// (one osascript / xdotool / PowerShell invocation) that classify a visible
// Godot modal and, under a clicking policy (auto/recover/cancel), click the
// policy-selected button.
//
// Adapted from Unity Open MCP's mcp-server/src/dialog-dismiss.ts (adapt for the
// platform-dispatch shape, the single-token stdout contract, parseDismissOutput,
// readDismissConfig, and the AppleScript/PowerShell/xdotool scaffolds).
// Intentional deltas:
//   - Godot process name "Godot" (Unity: "Unity"). Godot-owned-window check via
//     pgrep/Get-Process accordingly.
//   - Godot modal kinds + token tables from dialog-policy.ts (NOT Unity's six).
//   - The macOS path clicks a NAMED button by token (AppleScript `buttons of w`
//     enumeration) rather than always pressing Return — Godot's default button
//     is not as uniformly the safe choice as Unity's launch-errors "Ignore".
//   - No polling loop is wired into a launch-wait flow (Godot's bridge does not
//     run a Unity-style waitForCompile-with-dismiss). The tool exposes a one-shot
//     `probeGodotDialog` that an agent calls when a run appears stuck; the
//     decision to poll is the agent's, not the server's.
//   - `ignore` (the default) is detect-only: it reports a blocking modal WITHOUT
//     clicking, so the feature never performs a desktop action unless the
//     operator opts in. Unity's `ignore` clicks; Godot's does not.
//
// No runtime deps beyond node builtins (mcp-server/AGENTS.md "no runtime deps
// beyond MCP SDK"): only child_process and os.

import { execFile, execFileSync } from "node:child_process";
import { platform as nodePlatform } from "node:os";
import {
  DIALOG_TITLE_FRAGMENTS,
  parseDialogPolicy,
  preferenceTokensForPolicy,
  genericFallbackTokens,
  blockedKindsForPolicy,
  policyClicks,
  buildTokenTable,
  type DialogPolicy,
  type DialogKind,
} from "./dialog-policy.js";

/**
 * Supported `process.platform` values for the dialog dismiss helper. Narrowed
 * alias of NodeJS.Platform — keeps the platform-dispatch table exhaustive in
 * tests without forcing callers to import a Node-internal type.
 */
export type DismissPlatform = "win32" | "darwin" | "linux";

/**
 * Outcome of a single dismiss probe against the running OS desktop.
 *
 * `dismissed`: a Godot modal was found AND a click was dispatched successfully.
 *
 * `detected`: a Godot modal was found but the policy declines to click
 * (`ignore`/`manual`, or a kind whose tokens are null under this policy). No
 * desktop action was taken; `detectedButtons` carries the visible button labels
 * when the platform path could enumerate them (macOS), else an empty array.
 *
 * `not-found`: no Godot modal was visible on this probe (either Godot is not
 * running, or none of its windows matched a known/unknown dismissable title).
 *
 * `blocked`: a Godot modal was found that the policy explicitly declines to
 * click because it is destructive (currently `unsaved_changes` without the
 * opt-in). No click; the operator must dismiss it (or set the opt-in).
 *
 * `unsupported`: the platform or required tooling is unavailable (e.g. Linux on
 * Wayland without xdotool, or a platform Node does not report as win32/darwin/
 * linux). The probe is a graceful no-op.
 *
 * `error`: an unexpected platform error happened (osascript/PowerShell/xdotool
 * failure). Never thrown — surfaced in the union so callers never crash.
 */
export type DismissOutcome =
  | { kind: "dismissed"; button: string; dialog: DialogKind }
  | { kind: "detected"; dialog: DialogKind; detectedButtons: string[] }
  | { kind: "not-found" }
  | { kind: "blocked"; dialog: DialogKind; message: string }
  | { kind: "unsupported"; message: string }
  | { kind: "error"; message: string };

/**
 * Producer-side prefixes for error messages that are permanent (re-probing
 * would just respawn the same doomed tool). Exported so a future caller can
 * match the same literals.
 */
export const LINUX_XDOTOOL_MISSING_PREFIX = "xdotool not found on PATH";
export const UNSUPPORTED_PLATFORM_PREFIX =
  "Unsupported platform for Godot dialog auto-dismiss";

/** Options threaded through every platform probe. */
export interface DismissProbeOptions {
  platform: DismissPlatform;
  policy: DialogPolicy;
  /** Opt-in for the destructive `unsaved_changes` modal. Off by default. */
  allowUnsavedDismiss: boolean;
  /**
   * When true, the probe detects but never clicks regardless of policy (read-
   * only "what is blocking?" mode). Exposed for the tool's detect-only path.
   */
  detectOnly?: boolean;
}

// ---------------------------------------------------------------------------
// Per-session policy state
// ---------------------------------------------------------------------------

/**
 * Per-session policy override. Set by the `godot_open_mcp_dialog_policy_set`
 * tool; takes precedence over the env-derived policy. Held at module scope
 * because the MCP server is single-session per process. `undefined` means "no
 * override — consult env / default".
 */
let activePolicyOverride: DialogPolicy | undefined;

/**
 * Read the active per-session policy override, if any. Test-visible.
 */
export function getActiveDialogPolicyOverride(): DialogPolicy | undefined {
  return activePolicyOverride;
}

/**
 * Set the per-session policy override. `undefined` clears it (fall back to env /
 * default). Called by the `dialog_policy_set` tool.
 */
export function setActiveDialogPolicyOverride(policy: DialogPolicy | undefined): void {
  activePolicyOverride = policy;
}

/**
 * Resolve the effective policy: per-session override > env > default. Pure
 * aside from the env read.
 */
export function resolveEffectivePolicy(env: NodeJS.ProcessEnv = process.env): DialogPolicy {
  if (activePolicyOverride !== undefined) return activePolicyOverride;
  return parseDialogPolicy(env);
}

/**
 * Reset all per-session state. Test-only — production code never needs this.
 */
export function _resetDialogDismissStateForTests(): void {
  activePolicyOverride = undefined;
  xdotoolPresence = undefined;
}

// ---------------------------------------------------------------------------
// Top-level dispatch
// ---------------------------------------------------------------------------

/**
 * Try once to find (and, under a clicking policy, dismiss) a Godot editor modal
 * on the current OS desktop. Library-safe: never throws (errors are returned in
 * the {@link DismissOutcome} union), never writes to stdout/stderr, never
 * mutates global state beyond the cached xdotool-presence flag.
 *
 * Platform-dispatched:
 * - **macOS**: AppleScript via `osascript`. Enumerates the Godot process's
 *   windows, classifies the title, and clicks a named button per the policy
 *   token table (requires an Accessibility grant once).
 * - **Linux/X11**: `xdotool`. Searches for Godot-owned windows by title
 *   fragment, activates the window, and sends Return (clicks the focused
 *   button). Wayland is unsupported — `xdotool` is X11-only.
 * - **Windows**: Win32 (`EnumWindows` / `EnumChildWindows` / `GetWindowTextW` /
 *   `SendMessageW(BM_CLICK)`) via PowerShell — no native node-gyp dep.
 */
export async function tryDismissDialog(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  switch (opts.platform) {
    case "win32":
      return tryDismissWindows(opts);
    case "darwin":
      return tryDismissMacOS(opts);
    case "linux":
      return tryDismissLinuxX11(opts);
    default:
      return {
        kind: "unsupported",
        message: `${UNSUPPORTED_PLATFORM_PREFIX}: ${opts.platform as string}`,
      };
  }
}

// ---------------------------------------------------------------------------
// macOS — AppleScript via osascript
// ---------------------------------------------------------------------------

/**
 * Human-readable title fragments for the macOS AppleScript path. AppleScript's
 * `contains` is case-insensitive but does NOT strip punctuation, so the
 * normalized fragments in {@link DIALOG_TITLE_FRAGMENTS} (e.g. "savechanges")
 * would NOT match "Save Changes?". This parallel table carries the human
 * spellings Godot actually puts in a window title. Order matches the
 * {@link DialogKind} declaration; the per-fragment `contains` check is the
 * classifier for the macOS path.
 */
const GODOT_TITLE_FRAGMENTS_HUMAN: Readonly<Record<DialogKind, readonly string[]>> = {
  unsaved_changes: [
    "Save Changes",
    "Unsaved changes",
    "modified externally",
    "modified scenes",
    "Do you want to save",
  ],
  reimport: ["Reimport"],
  script_reload: ["Reload"],
};

/**
 * Map a normalized button token to a human-readable spelling for the macOS
 * AppleScript `contains` check. AppleScript cannot normalize a label, so the
 * named-button click matches the human spelling case-insensitively. Tokens not
 * in this map are passed through verbatim (and will still match a button whose
 * label literally contains them).
 */
function tokenToHumanLabel(token: string): string {
  switch (token) {
    case "save":
    case "saveall":
    case "savechanges":
      return "Save";
    case "reload":
    case "reloadnow":
      return "Reload";
    case "revert":
      return "Revert";
    case "reimport":
      return "Reimport";
    case "dontsave":
    case "dontsaveall":
      return "Don't Save";
    case "ok":
      return "OK";
    case "yes":
      return "Yes";
    case "no":
      return "No";
    case "cancel":
      return "Cancel";
    case "close":
      return "Close";
    case "continue":
      return "Continue";
    case "confirm":
      return "Confirm";
    case "quit":
      return "Quit";
    default:
      return token;
  }
}

/**
 * Build the macOS AppleScript that probes the Godot process's windows and
 * clicks the policy-selected button. Exposed as a function of the token table
 * so tests can assert the script shape without launching osascript.
 *
 * Strategy:
 *   1. If the Godot process is not running → `not-found`.
 *   2. For each window, check the title against the per-kind human fragments.
 *   3. If a kind matches AND it is in the blocked list → `blocked:<kind>`
 *      (no click).
 *   4. If a kind matches AND the policy clicks AND tokens are non-null →
 *      enumerate the window's buttons, click the first whose label contains a
 *      policy token (human spelling), return `dismissed:<button>:<kind>`.
 *   5. If a kind matches but the policy does not click (ignore/manual, or
 *      detect-only) → enumerate buttons and return
 *      `detected:<kind>:<pipe-joined-buttons>`.
 *   6. Unknown title + clicking policy + non-empty generic tokens → click the
 *      first generic-token button, return `dismissed:<button>:unknown`.
 *   7. Otherwise → `not-found`.
 *
 * Requires the Terminal / `node` binary to have been granted Accessibility
 * permission in System Settings → Privacy & Security → Accessibility.
 */
export function macosDismissAppleScript(opts: DismissProbeOptions): string {
  const table = buildTokenTable(opts.policy, opts.allowUnsavedDismiss);
  const click = table.click && !opts.detectOnly;
  const blockedSet = new Set(table.blocked);
  // Per-kind AppleScript blocks. For each kind, emit a title-fragment ladder
  // that, on the first match, branches on blocked / click / detect.
  const kindBlocks = (Object.keys(GODOT_TITLE_FRAGMENTS_HUMAN) as DialogKind[])
    .map((kind) => {
      const frags = GODOT_TITLE_FRAGMENTS_HUMAN[kind];
      const entry = table.kinds[kind];
      const cond = frags.map((f) => `wt contains "${escapeAppleScriptString(f)}"`).join(" or ");
      // Build the branch body.
      let body: string;
      if (blockedSet.has(kind)) {
        body = `return "blocked:${kind}"`;
      } else if (click && entry.tokens !== null && entry.tokens.length > 0) {
        // Click the first button whose label contains a token.
        const clickExprs = entry.tokens
          .map((tok) => {
            const label = escapeAppleScriptString(tokenToHumanLabel(tok));
            return `
            repeat with btn in buttons of w
              try
                set bt to (title of btn) as text
              on error
                set bt to ""
              end try
              if bt contains "${label}" then
                set frontmost to true
                click btn
                return "dismissed:" & bt & ":${kind}"
              end if
            end repeat`;
          })
          .join("");
        body = `${clickExprs}
          return "detected:${kind}:" & collectedButtons`;
      } else {
        // Detect-only: enumerate buttons, report detected.
        body = `return "detected:${kind}:" & collectedButtons`;
      }
      return `          if ${cond} then
            ${body}
          end if`;
    })
    .join("\n");

  // Generic unknown-title click block (clicking policy only).
  const genericClickBlock =
    click && table.genericTokens.length > 0
      ? table.genericTokens
          .map((tok) => {
            const label = escapeAppleScriptString(tokenToHumanLabel(tok));
            return `
          repeat with btn in buttons of w
            try
              set bt to (title of btn) as text
            on error
              set bt to ""
            end try
            if bt contains "${label}" then
              set frontmost to true
              click btn
              return "dismissed:" & bt & ":unknown"
            end if
          end repeat`;
          })
          .join("")
      : "";

  return `
on run
  -- Collect this window's button titles into a pipe-joined string for the
  -- detect-only report path.
  set collectedButtons to ""
  try
    tell application "System Events"
      if not (exists process "Godot") then
        return "not-found"
      end if
      tell process "Godot"
        repeat with w in windows
          try
            set wt to (title of w) as text
          on error
            set wt to ""
          end try
          -- Gather button labels for the detect report.
          set btnLabels to {}
          try
            repeat with btn in buttons of w
              try
                set end of btnLabels to ((title of btn) as text)
              end try
            end repeat
          end try
          set AppleScript's text item delimiters to "|"
          set collectedButtons to btnLabels as text
          set AppleScript's text item delimiters to ""
${kindBlocks}
${genericClickBlock ? `          -- generic unknown-title path\n${genericClickBlock}` : ""}
        end repeat
      end tell
    end tell
    return "not-found"
  on error errMsg
    return "error:" & errMsg
  end try
end run
`;
}

/** Escape a literal for safe interpolation into a double-quoted AppleScript string. */
function escapeAppleScriptString(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

async function tryDismissMacOS(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  return new Promise<DismissOutcome>((resolve) => {
    const child = execFile(
      "osascript",
      ["-e", macosDismissAppleScript(opts)],
      { timeout: DISMISS_SHELL_TIMEOUT_MS },
      (err, stdout) => {
        if (err) {
          // osascript returns non-zero when Accessibility is not granted (errAE*
          // / errAEEventNotPermitted). Surface as an error outcome so the tool
          // can point the operator at the permission grant.
          resolve({ kind: "error", message: err.message });
          return;
        }
        resolve(parseDismissOutput(stdout));
      },
    );
    void child;
  });
}

// ---------------------------------------------------------------------------
// Windows — Win32 via PowerShell
// ---------------------------------------------------------------------------

/**
 * The PowerShell payload that probes for any Godot modal and clicks the policy-
 * selected button. Exported as a string so tests can assert the script shape
 * without launching PowerShell.
 *
 * Strategy mirrors the macOS path: EnumWindows over Godot-owned windows,
 * normalize + classify the title, and either BM_CLICK the first token-matching
 * Button child or report detected/blocked. The token table is passed via stdin
 * (one JSON blob) so the script body stays constant.
 *
 * Single-token stdout contract — see {@link parseDismissOutput}.
 */
export const WINDOWS_DISMISS_PS_SCRIPT = `
$ErrorActionPreference = 'Stop'
try {
  if (-not ([System.Management.Automation.PSTypeName]'GodotOpenMcp.Dialogs.Dismisser').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace GodotOpenMcp.Dialogs {
  public static class Dismisser {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    const uint BM_CLICK = 0x00F5;
    static string Norm(string s) {
      var sb = new StringBuilder(s.Length);
      foreach (var ch in s) {
        if ((ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) {
          sb.Append(char.ToLowerInvariant(ch));
        }
      }
      return sb.ToString();
    }
    public static string TryDismiss(int[] godotPids, string tableJson) {
      var pidSet = new HashSet<uint>();
      for (int i = 0; i < godotPids.Length; i++) pidSet.Add((uint)godotPids[i]);
      var table = System.Text.Json.JsonSerializer.Deserialize<Types.Table>(tableJson);
      var sb = new StringBuilder(512);
      var candidates = new List<IntPtr>();
      EnumWindows((hWnd, lParam) => {
        if (!IsWindowVisible(hWnd)) return true;
        uint procId; GetWindowThreadProcessId(hWnd, out procId);
        if (!pidSet.Contains(procId)) return true;
        candidates.Add(hWnd);
        return true;
      }, IntPtr.Zero);
      foreach (var hWnd in candidates) {
        sb.Length = 0; GetWindowTextW(hWnd, sb, sb.Capacity);
        var title = sb.ToString(); var norm = Norm(title);
        string kind = null;
        foreach (var kv in table.kinds) {
          foreach (var frag in kv.Value.fragments) {
            if (norm.Contains(frag)) { kind = kv.Key; break; }
          }
          if (kind != null) break;
        }
        string[] tokens = null;
        if (kind != null) {
          if (table.blocked.Contains(kind)) return "blocked:" + kind;
          if (!kvTokensSpecified(table, kind)) {
            return "detected:" + kind + ":" + collectButtons(hWnd, sb);
          }
          tokens = table.kinds[kind].tokens;
        } else {
          if (!table.click || table.genericTokens.Length == 0) continue;
          tokens = table.genericTokens;
        }
        var match = findButton(hWnd, tokens, sb);
        if (match == null) continue;
        SendMessageW(match, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return "dismissed:" + buttonText(match, sb) + ":" + (kind ?? "unknown");
      }
      return "not-found";
    }
    static bool kvTokensSpecified(Types.Table t, string kind) {
      return t.kinds[kind].tokensSpecified;
    }
    static IntPtr findButton(IntPtr hWnd, string[] tokens, StringBuilder sb) {
      IntPtr matched = IntPtr.Zero;
      EnumChildWindows(hWnd, (hChild, lParam) => {
        sb.Length = 0; GetClassNameW(hChild, sb, sb.Capacity);
        if (sb.ToString() != "Button") return true;
        sb.Length = 0; GetWindowTextW(hChild, sb, sb.Capacity);
        var textNorm = Norm(sb.ToString());
        foreach (var tok in tokens) {
          if (textNorm.Contains(tok)) { matched = hChild; return false; }
        }
        return true;
      }, IntPtr.Zero);
      return matched;
    }
    static string buttonText(IntPtr hBtn, StringBuilder sb) {
      sb.Length = 0; GetWindowTextW(hBtn, sb, sb.Capacity);
      return sb.ToString();
    }
    static string collectButtons(IntPtr hWnd, StringBuilder sb) {
      var labels = new List<string>();
      EnumChildWindows(hWnd, (hChild, lParam) => {
        sb.Length = 0; GetClassNameW(hChild, sb, sb.Capacity);
        if (sb.ToString() != "Button") return true;
        sb.Length = 0; GetWindowTextW(hChild, sb, sb.Capacity);
        labels.Add(sb.ToString());
        return true;
      }, IntPtr.Zero);
      return String.Join("|", labels);
    }
  }
}
namespace GodotOpenMcp.Dialogs.Types {
  public class Table {
    public Dictionary<string, Kind> kinds { get; set; }
    public string[] blocked { get; set; }
    public string[] genericTokens { get; set; }
    public bool click { get; set; }
  }
  public class Kind {
    public string[] fragments { get; set; }
    public string[] tokens { get; set; }
    public bool tokensSpecified { get; set; }
  }
}
"@
  }
  $godotPids = @(Get-Process -Name 'Godot' -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id })
  if ($godotPids.Count -eq 0) { Write-Output 'not-found'; return }
  $tableJson = [Console]::In.ReadToEnd();
  Write-Output ([GodotOpenMcp.Dialogs.Dismisser]::TryDismiss([int[]]$godotPids, $tableJson))
} catch {
  Write-Output ('error:' + $_.Exception.Message)
}
`;

async function tryDismissWindows(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  const table = buildTokenTable(opts.policy, opts.allowUnsavedDismiss);
  // The C# distinguishes "null tokens" (decline → detect) from "empty tokens".
  // Emit tokensSpecified so the script routes correctly; mirror tokens: [] when
  // null so System.Text.Json deserializes cleanly.
  const payload = JSON.stringify({
    kinds: Object.fromEntries(
      Object.entries(table.kinds).map(([k, v]) => [
        k,
        {
          fragments: v.fragments,
          tokens: v.tokens ?? [],
          tokensSpecified: v.tokens !== null,
        },
      ]),
    ),
    blocked: table.blocked,
    genericTokens: table.genericTokens,
    click: table.click && !opts.detectOnly,
  });
  return new Promise<DismissOutcome>((resolve) => {
    const child = execFile(
      "powershell",
      [
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        WINDOWS_DISMISS_PS_SCRIPT,
      ],
      { timeout: DISMISS_SHELL_TIMEOUT_MS, windowsHide: true },
      (err, stdout) => {
        if (err) {
          resolve({ kind: "error", message: err.message });
          return;
        }
        resolve(parseDismissOutput(stdout));
      },
    );
    if (child.stdin) {
      child.stdin.end(payload);
    }
  });
}

// ---------------------------------------------------------------------------
// Linux/X11 — xdotool
// ---------------------------------------------------------------------------

/**
 * Whether `xdotool` is on PATH. Cached per process so a probe pays the lookup
 * cost at most once.
 */
let xdotoolPresence: boolean | undefined;

function isXdotoolAvailable(): boolean {
  if (xdotoolPresence !== undefined) return xdotoolPresence;
  try {
    execFileSync("xdotool", ["--version"], {
      stdio: "ignore",
      timeout: XDOTOOL_PROBE_TIMEOUT_MS,
    });
    xdotoolPresence = true;
  } catch {
    xdotoolPresence = false;
  }
  return xdotoolPresence;
}

/**
 * Escape a literal string for safe use in `xdotool search --name` (the arg is
 * interpreted as a regex).
 */
export function regexEscapeForXdotool(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Look up currently-running Godot Editor PIDs on the local box. Used by the
 * Linux/X11 path to scope the title-fragment match to Godot processes. Returns
 * an empty array on any failure. Pure / idempotent / never throws.
 */
function getGodotPidsLinux(): readonly number[] {
  try {
    const stdout = execFileSync("pgrep", ["-x", "Godot"], {
      stdio: ["ignore", "pipe", "ignore"],
      timeout: LINUX_PROBE_TIMEOUT_MS,
      encoding: "utf8",
    });
    return stdout
      .split(/\r?\n/)
      .map((s) => parseInt(s.trim(), 10))
      .filter((n) => Number.isFinite(n) && n > 0);
  } catch {
    return [];
  }
}

/**
 * Whether the session is running under Wayland (xdotool is X11-only). Best-
 * effort: checks XDG_SESSION_TYPE. Returns false on any uncertainty (the xdotool
 * probe will then fail gracefully and surface `unsupported`).
 */
function isLikelyWayland(env: NodeJS.ProcessEnv = process.env): boolean {
  const t = env.XDG_SESSION_TYPE ?? env.WAYLAND_DISPLAY;
  return typeof t === "string" && t.toLowerCase().includes("wayland");
}

async function tryDismissLinuxX11(
  opts: DismissProbeOptions,
): Promise<DismissOutcome> {
  if (isLikelyWayland()) {
    return {
      kind: "unsupported",
      message:
        "Linux dialog auto-dismiss requires X11 (xdotool). Wayland is not " +
        "supported; the probe is a no-op.",
    };
  }
  if (!isXdotoolAvailable()) {
    return {
      kind: "unsupported",
      message:
        `${LINUX_XDOTOOL_MISSING_PREFIX}. Install it (e.g. ` +
        "`sudo apt-get install xdotool`) to enable Godot dialog auto-dismiss " +
        "on Linux/X11. Wayland is not supported.",
    };
  }
  const godotPids = new Set(getGodotPidsLinux());
  if (godotPids.size === 0) {
    return { kind: "not-found" };
  }
  const blocked = new Set<string>(
    blockedKindsForPolicy(opts.policy, opts.allowUnsavedDismiss),
  );
  const click = policyClicks(opts.policy) && !opts.detectOnly;
  // Prefer the safe kinds first (reimport, script_reload), then the destructive
  // one last so a blocked unsaved_changes does not shadow a dismissable modal.
  const order: DialogKind[] = ["reimport", "script_reload", "unsaved_changes"];
  return new Promise<DismissOutcome>((resolve) => {
    let idx = 0;
    let settled = false;
    const finish = (outcome: DismissOutcome): void => {
      if (settled) return;
      settled = true;
      resolve(outcome);
    };
    const tryNextKind = (): void => {
      if (settled) return;
      if (idx >= order.length) {
        finish({ kind: "not-found" });
        return;
      }
      const kind = order[idx++];
      const tokens = preferenceTokensForPolicy(
        kind,
        opts.policy,
        opts.allowUnsavedDismiss,
      );
      const fragments = DIALOG_TITLE_FRAGMENTS[kind];
      let fIdx = 0;
      const tryNextFragment = (): void => {
        if (settled) return;
        if (fIdx >= fragments.length) {
          tryNextKind();
          return;
        }
        const fragment = fragments[fIdx++];
        execFile(
          "xdotool",
          ["search", "--name", regexEscapeForXdotool(fragment)],
          { timeout: XDOTOOL_PROBE_TIMEOUT_MS },
          (err, stdout) => {
            if (settled) return;
            if (err || !stdout.trim()) {
              tryNextFragment();
              return;
            }
            const candidateIds = stdout.trim().split(/\s+/).filter(Boolean);
            findGodotOwnedWindow(candidateIds, godotPids, (winId) => {
              if (!winId) {
                tryNextFragment();
                return;
              }
              if (blocked.has(kind)) {
                finish({
                  kind: "blocked",
                  dialog: kind,
                  message: `Policy '${opts.policy}' declines to dismiss the ${kind} dialog`,
                });
                return;
              }
              if (!click || tokens === null) {
                // Detect-only: xdotool cannot enumerate button labels, so the
                // report carries an empty button list.
                finish({ kind: "detected", dialog: kind, detectedButtons: [] });
                return;
              }
              // Activate the window + send Return (clicks the focused/default
              // button). Under auto/recover Godot's default button is the safe
              // forward-progress choice for reimport/script_reload.
              execFile(
                "xdotool",
                [
                  "windowactivate",
                  "--sync",
                  winId,
                  "key",
                  "--clearmodifiers",
                  "Return",
                ],
                { timeout: XDOTOOL_PROBE_TIMEOUT_MS },
                (activateErr) => {
                  if (settled) return;
                  if (activateErr) {
                    finish({
                      kind: "error",
                      message: `xdotool failed to dismiss window ${winId}: ${activateErr.message}`,
                    });
                    return;
                  }
                  finish({ kind: "dismissed", button: "Focus", dialog: kind });
                },
              );
            });
          },
        );
      };
      tryNextFragment();
    };
    tryNextKind();
  });
}

/**
 * Walk `candidateIds` and call `done(winId)` with the first window owned by a
 * Godot PID, or `done(undefined)` if none match.
 */
function findGodotOwnedWindow(
  candidateIds: string[],
  godotPids: ReadonlySet<number>,
  done: (winId: string | undefined) => void,
): void {
  let idx = 0;
  const next = (): void => {
    if (idx >= candidateIds.length) {
      done(undefined);
      return;
    }
    const winId = candidateIds[idx++];
    execFile(
      "xdotool",
      ["getwindowpid", winId],
      { timeout: LINUX_PROBE_TIMEOUT_MS },
      (err, stdout) => {
        if (err) {
          next();
          return;
        }
        const pid = parseInt(stdout.trim(), 10);
        if (Number.isFinite(pid) && godotPids.has(pid)) {
          done(winId);
          return;
        }
        next();
      },
    );
  };
  next();
}

// ---------------------------------------------------------------------------
// Shared parser
// ---------------------------------------------------------------------------

/**
 * Parse the single-token contract every platform dispatcher writes to stdout.
 * Inspects the LAST non-empty line of stdout (a stray osascript notice or
 * PowerShell warning must not misclassify the result).
 *
 * Contract:
 *   - `dismissed:<button>:<kind>` → dismissed
 *   - `detected:<kind>:<buttons>` → detected (buttons is a pipe-joined list,
 *     possibly empty)
 *   - `blocked:<kind>`            → blocked
 *   - `not-found`                 → not-found
 *   - `unsupported:<message>`     → unsupported
 *   - `error:<message>`           → error
 *   - any other / empty           → not-found (defensive)
 */
export function parseDismissOutput(stdout: string): DismissOutcome {
  const lines = stdout
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0);
  if (lines.length === 0) return { kind: "not-found" };
  const last = lines[lines.length - 1];
  if (last === "not-found") return { kind: "not-found" };
  if (last.startsWith("dismissed:")) {
    const rest = last.substring("dismissed:".length);
    const parts = rest.split(":");
    if (parts.length >= 2) {
      const dialog = parts[parts.length - 1];
      const button = parts.slice(0, -1).join(":") || "Focus";
      return {
        kind: "dismissed",
        button,
        dialog: isDialogKind(dialog) ? dialog : "script_reload",
      };
    }
    return {
      kind: "dismissed",
      button: rest || "Focus",
      dialog: "script_reload",
    };
  }
  if (last.startsWith("detected:")) {
    const rest = last.substring("detected:".length);
    const colonIdx = rest.indexOf(":");
    if (colonIdx === -1) {
      const dialog = rest;
      return {
        kind: "detected",
        dialog: isDialogKind(dialog) ? dialog : "script_reload",
        detectedButtons: [],
      };
    }
    const dialog = rest.substring(0, colonIdx);
    const buttonsRaw = rest.substring(colonIdx + 1);
    const detectedButtons = buttonsRaw
      .split("|")
      .map((b) => b.trim())
      .filter((b) => b.length > 0);
    return {
      kind: "detected",
      dialog: isDialogKind(dialog) ? dialog : "script_reload",
      detectedButtons,
    };
  }
  if (last.startsWith("blocked:")) {
    const dialog = last.substring("blocked:".length);
    return {
      kind: "blocked",
      dialog: isDialogKind(dialog) ? dialog : "unsaved_changes",
      message: `Policy declined to dismiss the ${dialog} dialog`,
    };
  }
  if (last.startsWith("unsupported:")) {
    return { kind: "unsupported", message: last.substring("unsupported:".length) };
  }
  if (last.startsWith("error:")) {
    return { kind: "error", message: last.substring("error:".length) };
  }
  return { kind: "not-found" };
}

function isDialogKind(s: string): s is DialogKind {
  return (
    s === "unsaved_changes" ||
    s === "reimport" ||
    s === "script_reload" ||
    s === "unknown"
  );
}

// ---------------------------------------------------------------------------
// Config + probe timeout constants
// ---------------------------------------------------------------------------

/**
 * Resolve the dismiss-feature config from the environment + per-session
 * override. Pure aside from the env read.
 *
 *   - `GODOT_OPEN_MCP_DIALOG_POLICY=ignore|auto|recover|cancel|manual` selects
 *     which button to click per dialog kind (default `ignore`). `manual` fully
 *     opts out (no detect, no click).
 *   - `GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS=1` opts in to auto-dismissing the
 *     unsaved-changes modal (destructive under every policy — off by default).
 *   - The per-session override ({@link setActiveDialogPolicyOverride}) wins
 *     over the env value when set.
 */
export interface DialogDismissConfig {
  policy: DialogPolicy;
  allowUnsavedDismiss: boolean;
}

export function readDialogDismissConfig(
  env: NodeJS.ProcessEnv = process.env,
): DialogDismissConfig {
  return {
    policy: resolveEffectivePolicy(env),
    allowUnsavedDismiss: env.GODOT_OPEN_MCP_ALLOW_UNSAVED_DISMISS === "1",
  };
}

/** osascript / PowerShell dismiss invocation cap. */
const DISMISS_SHELL_TIMEOUT_MS = 5_000;
/** xdotool presence / window-search / key-send probe cap. */
const XDOTOOL_PROBE_TIMEOUT_MS = 2_000;
/** Linux process-presence / window-id probe cap (pgrep / getwindowpid). */
const LINUX_PROBE_TIMEOUT_MS = 1_000;

// ---------------------------------------------------------------------------
// Tool-facing entry: one-shot probe (detect + optional dismiss)
// ---------------------------------------------------------------------------

/**
 * The structured result the `dialog_policy_set` tool serializes. Captures the
 * effective policy, the detected platform, what the probe found, and what
 * action it took.
 */
export interface DialogProbeResult {
  /** Policy in effect for this probe (override > env > default). */
  policy: DialogPolicy;
  /** Whether a per-session override was active. */
  policyOverridden: boolean;
  /** Destructive opt-in state. */
  allowUnsavedDismiss: boolean;
  /** Resolved platform. */
  platform: string;
  /** Whether the policy clicks at all (auto/recover/cancel). */
  clickPolicy: boolean;
  /** Whether this probe was detect-only (no click regardless of policy). */
  detectOnly: boolean;
  /** Outcome kind: dismissed | detected | not_found | blocked | unsupported | error. */
  outcome: string;
  /** Matched dialog kind, when one was found. */
  dialog?: DialogKind;
  /** Button label that was clicked (dismissed) or the detected button labels. */
  button?: string;
  detectedButtons?: string[];
  /** Free-text detail (blocked / unsupported / error messages). */
  message?: string;
  /**
   * Platform permission/tooling note the operator may need to act on (macOS
   * Accessibility grant; Linux xdotool; Windows none).
   */
  platformNote?: string;
}

/**
 * One-shot probe: resolve the effective policy, dispatch to the platform probe,
 * and return a structured result. Never throws. The agent-callable surface —
 * an agent that suspects a Godot modal is blocking a run calls this (via the
 * tool) to detect and (under a clicking policy) dismiss it.
 *
 * `detectOnly` forces detect-only regardless of policy (the tool's read-only
 * path). When false, the policy decides: `ignore`/`manual` detect-only; the
 * rest detect + click.
 */
export async function probeGodotDialog(opts: {
  env?: NodeJS.ProcessEnv;
  detectOnly?: boolean;
  /** Override the platform — exposed for tests. */
  platform?: DismissPlatform;
  /** Override the probe — exposed for tests. */
  probe?: typeof tryDismissDialog;
}): Promise<DialogProbeResult> {
  const env = opts.env ?? process.env;
  const config = readDialogDismissConfig(env);
  const platform = opts.platform ?? (nodePlatform() as DismissPlatform);
  const detectOnly = opts.detectOnly ?? false;
  const clickPolicy = policyClicks(config.policy);
  const probe = opts.probe ?? tryDismissDialog;
  // `manual` fully opts out (no detect, no click): short-circuit without
  // spawning a subprocess. Distinct from `ignore`, which detects-only.
  if (config.policy === "manual") {
    return {
      policy: config.policy,
      policyOverridden: getActiveDialogPolicyOverride() !== undefined,
      allowUnsavedDismiss: config.allowUnsavedDismiss,
      platform,
      clickPolicy: false,
      detectOnly: false,
      outcome: "not-found",
      message:
        "Policy is 'manual' — dialog detection and dismissal are fully opted out. " +
        "No desktop probe was run.",
      platformNote: platformNote(platform),
    };
  }
  const outcome = await probe({
    platform,
    policy: config.policy,
    allowUnsavedDismiss: config.allowUnsavedDismiss,
    detectOnly: clickPolicy ? detectOnly : true,
  });
  const result: DialogProbeResult = {
    policy: config.policy,
    policyOverridden: getActiveDialogPolicyOverride() !== undefined,
    allowUnsavedDismiss: config.allowUnsavedDismiss,
    platform,
    clickPolicy,
    detectOnly: clickPolicy ? detectOnly : true,
    outcome: outcome.kind,
    platformNote: platformNote(platform),
  };
  switch (outcome.kind) {
    case "dismissed":
      result.dialog = outcome.dialog;
      result.button = outcome.button;
      break;
    case "detected":
      result.dialog = outcome.dialog;
      result.detectedButtons = outcome.detectedButtons;
      break;
    case "blocked":
      result.dialog = outcome.dialog;
      result.message = outcome.message;
      break;
    case "not-found":
      break;
    case "unsupported":
    case "error":
      result.message = outcome.message;
      break;
  }
  return result;
}

/**
 * The operator-facing permission/tooling note per platform. Surfaced in every
 * probe result so a `unsupported`/`error` outcome points the operator at the
 * fix.
 */
function platformNote(platform: string): string {
  switch (platform) {
    case "darwin":
      return (
        "macOS: requires an Accessibility grant for the process running the " +
        "MCP server (System Settings → Privacy & Security → Accessibility). " +
        "Grant it once; without it osascript returns a not-permitted error."
      );
    case "linux":
      return (
        "Linux: requires xdotool and an X11 session (Wayland is not " +
        "supported). Install xdotool (e.g. `sudo apt-get install xdotool`)."
      );
    case "win32":
      return "Windows: no extra setup (uses Win32 SendMessage BM_CLICK).";
    default:
      return `Unsupported platform: ${platform}`;
  }
}

/**
 * Whether the effective policy performs any desktop action at all. Exposed so
 * the tool can short-circuit a probe when fully opted out (`manual`) without
 * spawning a subprocess.
 */
export function effectivePolicyActs(env: NodeJS.ProcessEnv = process.env): boolean {
  return policyClicks(resolveEffectivePolicy(env));
}

// Re-export the fallback token helper for tests that assert the generic table.
export { genericFallbackTokens };
