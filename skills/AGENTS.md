# Skills rules

## Scope

Rules for `skills/` — agent-facing playbooks installed into Godot game projects.
Inherits root `AGENTS.md`; deeper rules win on overlap. Human catalog: [`docs/skills.md`](../docs/skills.md).

## Canonical skill

- `skills/godot-open-mcp/SKILL.md` is the **single** operational playbook every agent reads first.
  It is lean (~150 lines) on purpose: schemas, rule catalogs, fix lists, and gate JSON envelopes
  belong in `docs/api/mcp-tools.md`, not in the skill.
- Target line budget: 130–180. Exceed only with a documented reason recorded in the commit.
- No domain pack skills live here yet. Domain playbooks are a Phase 12 concern; reserved group ids
  (`tilemap`, `navigation`, `particles`, `animation`, `csg`) appear in the catalog today with empty
  rosters and may carry their own `skills/<domain>/SKILL.md` when their pack ships.

## Install-path source of truth

- `skills/client-paths.json` is the single source of truth for per-client skill destinations
  (`clients`) and the MCP-client → skill-target mapping (`mcpClientMapping`). Schema:
  `skills/client-paths.schema.json` (validates shape only).
- Cross-field rules (every MCP-client id matches the CLI `agentRegistry`, every mapping value
  names a key in `clients`, no duplicate output path, `templateRelativePath` exists) are enforced
  by `scripts/check-skill.mjs`. Those checks deliberately read the TypeScript `agentRegistry`
  (`cli/src/utils/agents.ts`) as the source of truth rather than duplicating the id list.
- Do not hardcode client skill paths in TypeScript, Rust, or docs. Future consumers (a setup-skills
  CLI command, the Hub wizard, a project-specific skill generator) must read this file.

## Ownership and sync

| Skill | Owner of sync with tool changes |
|---|---|
| `skills/godot-open-mcp/SKILL.md` | MCP package — see [Agent skill sync](../mcp-server/AGENTS.md#agent-skill-sync) |
| `skills/client-paths.json` + schema | MCP package — keep `mcpClientMapping` keys in lock-step with `cli/src/utils/agents.ts` `agentRegistry` |

When an MCP tool, capability, route policy, gate workflow, or `manage_tools` action changes, update
**both** `docs/api/mcp-tools.md` and (when agent workflow is affected) the skill. The skill does not
duplicate API tables — it links them.

## Forbidden content

The skill is shipped into game projects and read by AI agents that lack repository context. It
must not contain:

- Internal phase/spec references (e.g. `P3.6`, `M22`, `specs/...`, roadmap ids).
- Forbidden reference-project names (see root `AGENTS.md` §Naming rule exception). Only
  [Unity Open MCP](https://github.com/AlexeyPerov/Unity-Open-MCP) may be named, and only where the
  comparison is genuinely useful.
- Concrete secrets — no bearer tokens, no maintainer home paths, no machine-specific absolute paths.
  Use `/absolute/path/to/MyGame` placeholders for examples.
- A `batch` route claim or any headless-editor fallback. Godot has none.
- `suggest` / `activate_for` / `intent` `manage_tools` actions — they are not in the shipped contract.

## Verification

After any skill or client-paths edit:

```bash
node scripts/check-skill.mjs
```

The audit enforces: file existence, line budget, every backticked `godot_open_mcp_*` token is a
registered tool, no unprefixed tool id is presented as an MCP tool, no `batch` route claim, only
shipped `manage_tools` actions are named, every client mapping key matches the CLI registry, every
mapping value names a declared client, `templateRelativePath` exists, no internal/reference leakage,
no concrete secret/home path. The audit runs in CI alongside the mcp-server test job.

After `client-paths.json` edits that change `mcpClientMapping` keys, also re-read
`cli/src/utils/agents.ts` to confirm every shipped agent id is represented (the audit enforces this,
but a human review keeps the intent clear).

## Installed copies

When a future setup-skills command or Hub wizard writes the skill into a game project's client
folders (`.cursor/skills/godot-open-mcp/SKILL.md`, `.claude/skills/godot-open-mcp/SKILL.md`, …),
those installed copies are **project-local**. Edit the toolkit template here, not copies under
`demo/` or other projects, unless intentionally refreshing a fixture.
