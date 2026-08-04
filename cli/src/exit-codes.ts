// CLI exit-code contract.
//
// The CLI exposes a small deterministic exit-code scheme so CI pipelines and
// scripts can branch on outcome without parsing JSON. Adapted from Unity Open
// MCP's 4-level scheme (mcp-server/src/cli/exit-codes.ts).
//
//   0  success            — no issues (or command completed).
//   1  errors             — a generic failure (unknown command, bad args, a
//                           command that did not succeed, verify found errors,
//                           regression detected a regression).
//   2  baseline missing   — regression: the baseline file does not exist.
//   3  baseline invalid / timeout — regression: the baseline is unreadable /
//                           unparseable / schema-mismatched; OR the bridge
//                           never became reachable / a call timed out.
//
// The 2/3 codes are overloaded: `2` and `3` mean "baseline missing/invalid"
// ONLY for `regression check` (the P15.1 exit-code contract). For every other
// command `3` retains its timeout meaning. `verify` and `baseline` use only 0/1
// (clean/fail) — they collapse Unity's warnings/errors split into a single
// non-zero code, matching the Godot CLI's simpler surface.

/** Canonical CLI exit codes. */
export const EXIT = {
  SUCCESS: 0,
  // Unity uses 2 for ERRORS and 1 for WARNINGS. The Godot CLI collapses both to
  // a single non-zero ERRORS code (1) — the conventional "something went wrong"
  // code that shells and CI expect. `verify` and `baseline` use this; `regression
  // check` uses it for "regression detected".
  ERRORS: 1,
  /** regression: baseline file not found (P15.1 contract). */
  BASELINE_MISSING: 2,
  /** regression: baseline unreadable/unparseable/schema-mismatched (P15.1),
   *  OR bridge timeout/unreachable (wait-for-ready, ping). */
  TIMEOUT: 3,
} as const;
