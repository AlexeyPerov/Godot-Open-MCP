# Validation Suite rules

## Scope

Rules for `validation-suite/` — standalone guided manual-validation app.
Root `AGENTS.md` also applies. Overview: [`README.md`](README.md).

## Standalone app

- Tauri + SvelteKit stack, but a **self-contained product**. Engine-neutral
  orchestration lives in `packages/core/`; engine specifics live in
  `engine-profiles/godot.json` and scenario JSON. The suite invokes the engine
  only through the `godot-open-mcp` CLI (`run-tool`) as a subprocess — it never
  embeds the MCP server. Architecture is adapted from Unity Open MCP's
  validation suite.

## Scenario conventions

- Hand-author scenarios as JSON under `scenarios/<engineId>/<pack>/`.
- Required fields: `id`, `title`, `milestone`, `engineId`, `requirementLevel`
  (`required-core` | `required-extended` | `optional`), and `steps`.
- Prefer stable ids like `<pack>-<slug>` matching the filename stem.
  Step ids are unique within a scenario. Patch ops are the pinned vocabulary
  validated by `packages/core` (`replace_line_contains`,
  `insert_after_line_contains`, `insert_before_line_contains`,
  `trim_trailing_whitespace`).
- Loader/types in `packages/core/src/` are canonical; reject unknown step or
  action types rather than silently ignoring them.

## Artifacts

- **Hand-authored:** scenario JSON, engine profiles, UI/Rust source.
- **Generated at runtime (do not commit):** project-local suite state under
  `.godot-open-mcp/ValidationSuite/`, fixtures under `_ValidationSuite/`.
  Demo `.gitignore` already excludes both.
- Export summaries are operator artifacts, not repo sources.

## Checklist integration

- Suite export/sign-off can index maintainer checklists when `specs/` is
  available. Public clones are not blocked by missing specs.
- Do not put milestone/spec identifiers into user-facing product docs.

## Verification

- `npm run test:core`, `npm run check`, and `cargo test` in `src-tauri/` after
  core/UI/Rust changes.
