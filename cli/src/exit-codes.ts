// CLI exit-code contract.
//
// The CLI exposes a small deterministic exit-code scheme so CI pipelines and
// scripts can branch on outcome without parsing JSON. Adapted from Unity Open
// MCP's 4-level scheme (mcp-server/src/cli/exit-codes.ts) — the verify /
// baseline / regression commands that need the WARNINGS/TIMEOUT distinction
// land in later P6 plans; P6.1 ships the contract so every command agrees on
// the codes from day one.
//
//   0  success           — no issues (or command completed).
//   1  warnings / errors — a generic failure (unknown command, bad args, a
//                          command that did not succeed). The WARNINGS-vs-ERRORS
//                          split is reserved for future verify commands; until
//                          those land, non-success commands use ERRORS (1).
//   3  timeout/unreachable — the bridge never became reachable, or a call
//                            timed out.
//
// P6.1 only emits SUCCESS and ERRORS — TIMEOUT is defined for parity so later
// command modules (wait-for-ready, ping) can adopt it without renumbering.

/** Canonical CLI exit codes. */
export const EXIT = {
  SUCCESS: 0,
  // Unity uses 2 for ERRORS and 1 for WARNINGS. The Godot CLI does not yet have
  // a verify/regression surface that distinguishes the two, so we collapse both
  // to a single non-zero ERRORS code (1) — the conventional "something went
  // wrong" code that shells and CI expect. When verify/regression CLI commands
  // arrive, revisit whether the 4-level split is worth reintroducing.
  ERRORS: 1,
  TIMEOUT: 3,
} as const;
