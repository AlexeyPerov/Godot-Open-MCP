# Verify package rules

## Scope

Rules for `packages/verify/` — the scoped health-check addon for Godot Open MCP. Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- Godot 4.3+ C# mono addon. Editor-only code under `Editor/`.
- No dependency on the bridge package — verify must stay usable standalone (the bridge depends on verify, not the reverse).
- Tests live under `Tests/`.

## Verify rules

- Every rule implements `IVerifyRule` (`Editor/Core/IVerifyRule.cs`) and lives in its own folder under `Editor/Rules/{RuleName}/`.
- **Every rule must declare a stable `Id`** — surfaced in MCP tool responses, the capability catalog, and the gate delta.
- **Every `VerifyIssue` must carry an `IssueCode`**. v1 issue codes include: `broken_scene_reference`, `missing_script`, import/uid-related codes. Issue codes link rules to fixes.
- Severity (`Error` / `Warning`) is set per-issue, not per-rule. The gate delta treats Errors as failures; Warnings are informational.

## Fixes

- Every fix implements `IFixProvider` and registers via `FixProviderRegistry`.
- Every fix must declare a `FixId` and implement `CanFix(issueId)`.
- `Safe: true` fixes are the only ones the gate will auto-suggest.

## Capability catalog sync

- The MCP-side rule catalog (`mcp-server/src/capabilities/rule-catalog.ts`) mirrors implemented rules and their issue codes/severities. Update the MCP catalog in the same task when rules change so `godot_open_mcp_capabilities` stays accurate.

## Verification

- C# changes: add or update the narrowest test in `Tests/`.
- Rule changes: verify issue → fix linkage in tests and via `build-capabilities.test.ts`.
