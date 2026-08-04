# Godot Open MCP — CI templates

Provider-agnostic CI templates for running Godot verify scans and regression gates in standard CI. These templates target a **game project** that uses Godot Open MCP — they are meant to be copied into your game repo and adjusted, not run from this toolkit repo.

## What the CLI surface gives you

The `godot-open-mcp-cli` package ships three CI commands (see `godot-open-mcp-cli --help`):

| Command | Purpose | Exit codes |
|---|---|---|
| `verify [path]` | Run an offline verify scan (broken references + missing scripts) | 0 clean / 1 issues at/above `--fail-on` |
| `baseline create\|update [path]` | Create or refresh the regression baseline | 0 ok / 1 write error |
| `regression check [path]` | Compare current scan against the baseline | 0 ok / 1 regression / 2 baseline missing / 3 baseline invalid |

### Key advantage: no editor on the runner

The verify / baseline / regression commands run through an **offline disk scanner** that parses `.tscn` / `.tres` files directly. Unlike Unity-based pipelines, **no Godot editor needs to be installed or running on the CI runner** — only Node.js. This keeps CI fast and removes the licensing/headless-editor headache entirely.

### Exit-code contract

| Code | Meaning | CI behavior |
|---|---|---|
| 0 | Success — no issues / no regression / baseline written | continue |
| 1 | Errors — verify found issues at/above `--fail-on`, or a regression was detected | **fail the job** |
| 2 | Baseline missing — `regression check` could not find the baseline file | **fail the job** |
| 3 | Baseline invalid / timeout — baseline unreadable / schema-mismatched, or a bridge call timed out | **fail the job** |

Codes `2` and `3` are **regression-specific**. For `verify` and `baseline`, only `0` (clean / written) and `1` (issues / error) are used.

### Offline scope

The offline scanner detects two rule families unambiguously from disk:

- **`broken_references`** — an `[ext_resource]` whose `path=` does not exist on disk and whose `uid=` is not in the project's uid index.
- **`missing_scripts`** — a node's `script = ExtResource(...)` attachment pointing at a script that does not exist.

The richer verify rules (project health, scene structure, materials/shaders, script audit, animation) run through the **live** verify surface (`validate_edit`) inside the editor and are **CI-excluded** from the offline baseline. The baseline records them in `ciExcludedRules` so a consumer never mistakes "absent offline" for "clean". If you need those rules in CI, run them through a live editor bridge (see the `wait-for-ready` + MCP tool flow) in addition to the offline gate.

## Prerequisites

1. **Node.js 18+** on the runner (the CLI has zero runtime dependencies — `npx` just works).
2. `GODOT_PROJECT_PATH` (or `--project <path>`) pointing at your Godot project root (the directory containing `project.godot`).
3. The `godot-open-mcp-cli` package available — either install it (`npm i -D godot-open-mcp-cli`) or use `npx`.

A Godot editor is **not** required for the offline verify / baseline / regression commands. You only need the editor on the runner if you also run live-bridge health checks (`wait-for-ready`, `ping`, `status`) or live verify rules.

## Templates

- [`github-actions/godot-verify.yml`](github-actions/godot-verify.yml) — a GitHub Actions workflow with two jobs: verify-on-PR, regression-on-main + baseline-refresh.
- [`gitlab-ci/godot-verify.yml`](gitlab-ci/godot-verify.yml) — the same jobs as a GitLab CI pipeline.

Copy the relevant file into your repo, adjust the project path, and wire it to your trigger of choice.

## Typical pipeline shape

```text
┌─ pull request ──────────────────────────────────────┐
│  verify (whole-project offline scan, --fail-on error) │   exit 1 fails the PR
└──────────────────────────────────────────────────────┘

┌─ push to main ───────────────────────────────────────────────────┐
│  regression check  →  baseline update (commit back to main)      │   keeps the baseline fresh
└──────────────────────────────────────────────────────────────────┘
```

The baseline file (`CI/godot-open-mcp-baseline.json` by default) is **committed to your repo**. `baseline update` runs on main after a regression check passes, so the next PR is compared against the known-good state.

### Threshold semantics

`regression check --threshold N` (default `0`) sets the global error-count-delta gate: a regression is flagged when `current.errors - baseline.errors > N` (strict `>`; a delta equal to the threshold is tolerated). Use `--per-category-threshold ruleId=N` (repeatable) to override the threshold per rule — a rule with no explicit entry falls back to the global threshold. The overall verdict is the OR of the global gate and every per-rule gate.
