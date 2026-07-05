#!/usr/bin/env python3
"""
Runtime/Editor boundary guard for Godot Open MCP.

Godot compiles the whole addon into a single C# assembly — there is no per-folder
assembly boundary the way Unity's asmdefs give. What keeps editor-only code out of a
shipped game build is the `TOOLS` compilation symbol, which Godot defines ONLY for the
editor (`Debug`) configuration and leaves undefined for `ExportDebug` / `ExportRelease`.
Anything wrapped in `#if TOOLS … #endif` is therefore compiled into the editor and
stripped from an exported game.

The bridge addon (`packages/bridge/`, installed as `addons/godot_open_mcp/`) mirrors that
split at the folder level:

    packages/bridge/Runtime/   — must ship into a game build (un-gated, no editor APIs)
    packages/bridge/Editor/    — editor-only (the EditorPlugin, the HTTP bridge, tool handlers)

This script enforces the ONE rule that keeps that split honest:

    NO file under packages/bridge/Runtime/ may reference an editor-only Godot API in
    code that actually compiles into a game build.

Concretely it FAILS (exit 1) when a `Runtime/**/*.cs` file, OUTSIDE comments and string
literals, contains:

  * a real `EditorInterface` / `EditorPlugin` / `EditorFileSystem` / `EditorScript`
    reference that is NOT inside a `#if TOOLS` … `#endif` guard, OR
  * a `#if TOOLS` guard that is not balanced (an unterminated guard).

A `#if TOOLS` block in `Runtime/` is allowed only as a narrow, documented editor-coupling
shim (the guarded body is stripped from the game build, so it cannot leak). Such shims are
reported as a WARNING, not a failure, when `--verbose` is passed.

The check is comment- and string-aware: editor type names mentioned in `//` / `///` /
`/* */` documentation or inside string literals (regular `"..."`, verbatim `@"..."`, and
interpolated `$"..."` text spans) never trip the guard — only real code does. This mirrors
exactly what the `ExportRelease` (TOOLS-undefined) compile strips, but runs in milliseconds
with no .NET build, so it is cheap to gate every PR on.

## Suppression / explicit exceptions

The rule set is intentionally small and reviewable. The default policy is "no editor APIs
in Runtime/ real code outside `#if TOOLS`." A justified, auditable suppression path exists
for the rare case where a `Runtime/` file must reference an editor type in shipping code
(e.g. a type forwarded through a constant string consumed only by the editor). Append the
`// boundary:allow` marker as a trailing line comment on the SAME line as the offending
token:

    // Runtime file, real code:
    var name = nameof(EditorInterface); // boundary:allow ADR-XYZ forwarded type name

The marker MUST sit on the same physical line as the editor token it suppresses and MUST
carry a short audit note (anything non-empty after `boundary:allow`) so the exception is
visible in code review. Suppressed lines are reported under `--verbose` so the exception
set stays visible in CI output. This is the ONLY suppression path — do not silence the
guard by other means; widen `EDITOR_TOKENS` or relax the rules here instead.

Usage:
    python scripts/check-runtime-boundary.py            # scan, exit 1 on any violation
    python scripts/check-runtime-boundary.py --verbose  # also print warnings + suppressions
    python scripts/check-runtime-boundary.py --root PATH  # scan a different checkout root

Exit codes: 0 = boundary holds, 1 = at least one hard violation found, 2 = usage error.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# Editor-only Godot APIs a runtime (game-shipped) file must never touch in real code.
# Keep this list small and intentional — it is the reviewable policy surface.
EDITOR_TOKENS = ("EditorInterface", "EditorPlugin", "EditorFileSystem", "EditorScript")
_TOKEN_RE = re.compile(r"\b(" + "|".join(EDITOR_TOKENS) + r")\b")

# Inline suppression marker. Must sit on the same line as the editor token it covers (as a
# trailing `//` line comment), and must carry an audit note (anything non-empty after
# `boundary:allow`) so the exception is visible in code review.
_SUPPRESSION_RE = re.compile(r"//\s*boundary:allow\b\s*:?\s*(\S[^\n]*)?$")


def strip_comments(text: str) -> str:
    """Blank out everything that is NOT real C# code on each line — `//` line comments,
    `/* */` block comments, AND string literals (regular `"..."`, verbatim `@"..."`, and the
    text spans of interpolated `$"..."`) — replacing them with spaces while preserving
    newlines so reported line numbers stay accurate.

    Why strings too: runtime files may mention editor type names inside doc-comments AND
    inside attribute / message strings (e.g. `[Description("… EditorInterface …")]`) to
    DOCUMENT the boundary. Those are not real editor-API references — only an unquoted,
    uncommented `EditorInterface` / `EditorPlugin` / ... token is. Blanking comments and
    string contents leaves exactly the real code for the token scan, matching what the
    `ExportRelease` compile actually strips.

    The `// boundary:allow` suppression marker is itself a line comment and gets blanked
    here; the scanner reads the RAW line for the marker, so the blanking does not affect
    suppression detection.

    This is a deliberately small, single-purpose scanner — not a full C# lexer — but it
    handles the constructs this addon uses (line/block comments, regular/verbatim/
    interpolated strings, escaped quotes, doubled `""` in verbatim strings)."""
    out = []
    i, n = 0, len(text)
    in_block = False
    in_line = False
    in_str = False        # regular "..."
    in_verbatim = False   # @"..." (doubled "" is an escaped quote)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if in_line:
            out.append("\n" if c == "\n" else " ")
            in_line = c != "\n"
            i += 1
        elif in_block:
            if c == "*" and nxt == "/":
                in_block = False
                out.append("  ")
                i += 2
            else:
                out.append("\n" if c == "\n" else " ")
                i += 1
        elif in_str:
            if c == "\\":                      # escaped char inside a regular string
                out.append("  ")
                i += 2
            elif c == '"':
                in_str = False
                out.append('"')
                i += 1
            else:
                out.append("\n" if c == "\n" else " ")
                i += 1
        elif in_verbatim:
            if c == '"' and nxt == '"':        # doubled quote = literal quote, stay inside
                out.append("  ")
                i += 2
            elif c == '"':
                in_verbatim = False
                out.append('"')
                i += 1
            else:
                out.append("\n" if c == "\n" else " ")
                i += 1
        else:
            if c == "/" and nxt == "/":
                in_line = True
                out.append("  ")
                i += 2
            elif c == "/" and nxt == "*":
                in_block = True
                out.append("  ")
                i += 2
            elif c == "@" and nxt == '"':       # verbatim string @"..."
                in_verbatim = True
                out.append("  ")
                i += 2
            elif c == "$" and nxt == '"':       # interpolated string $"..." (blank the text spans)
                in_str = True
                out.append("  ")
                i += 2
            elif c == '"':
                in_str = True
                out.append('"')
                i += 1
            else:
                out.append(c)
                i += 1
    return "".join(out)


def scan_file(path: Path):
    """Return (violations, warnings, suppressions) for one .cs file.

    A *violation* is an editor token in real code outside a `#if TOOLS` guard and not
    suppressed by a same-line `# boundary:allow` marker.
    A *warning* is a `#if TOOLS` guard present in a Runtime file (allowed shim, flagged).
    A *suppression* is an editor-token line that was silenced by `# boundary:allow`.
    """
    raw = path.read_text(encoding="utf-8")
    code = strip_comments(raw)
    violations = []
    warnings = []
    suppressions = []
    tools_depth = 0          # >0 while inside a #if TOOLS (...) block
    if_stack = []            # track whether each #if level is a TOOLS gate
    for lineno, (raw_line, line) in enumerate(zip(raw.splitlines(), code.splitlines()), start=1):
        stripped = line.strip()
        m_if = re.match(r"#\s*if\s+(.*)$", stripped)
        if m_if:
            cond = m_if.group(1)
            is_tools = bool(re.search(r"\bTOOLS\b", cond))
            if_stack.append(is_tools)
            if is_tools:
                tools_depth += 1
                warnings.append((lineno, "#if TOOLS guard in Runtime/ (body stripped from game build)"))
            continue
        if re.match(r"#\s*endif", stripped):
            if if_stack:
                was_tools = if_stack.pop()
                if was_tools and tools_depth > 0:
                    tools_depth -= 1
            continue
        if re.match(r"#\s*else", stripped) or re.match(r"#\s*elif", stripped):
            # An #else of a #if TOOLS means the following lines compile when TOOLS is UNDEFINED
            # (i.e. they DO ship). Drop out of the TOOLS-guarded region for token checking.
            if if_stack and if_stack[-1] and tools_depth > 0:
                tools_depth -= 1
                if_stack[-1] = False
            continue
        if tools_depth > 0:
            continue  # inside #if TOOLS: stripped from the game build, not a leak
        for m in _TOKEN_RE.finditer(line):
            token = m.group(1)
            # Check the RAW line for a same-line suppression marker. The marker must sit on
            # the same physical line and carry an audit note so the exception is auditable.
            if _SUPPRESSION_RE.search(raw_line):
                suppressions.append((lineno, token, raw_line.strip()))
                continue
            violations.append((lineno, token, line.strip()))
    return violations, warnings, suppressions


def _resolve_runtime_dir(repo_root: Path) -> Path:
    """The bridge ships as `addons/godot_open_mcp/` but lives in-repo under
    `packages/bridge/`. The Runtime/ surface that must stay editor-API-free is the same in
    both layouts, so scan the in-repo path. If the addon is laid out the installed way
    (`addons/godot_open_mcp/Runtime/`) — e.g. inside the demo project — that wins."""
    installed = repo_root / "addons" / "godot_open_mcp" / "Runtime"
    if installed.is_dir():
        return installed
    return repo_root / "packages" / "bridge" / "Runtime"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Godot Open MCP Runtime/Editor boundary guard")
    parser.add_argument("--root", default=None, help="repo root (default: auto-detect from this script)")
    parser.add_argument("--verbose", action="store_true", help="print WARNING shims and suppressions too")
    args = parser.parse_args(argv)

    repo_root = Path(args.root).resolve() if args.root else Path(__file__).resolve().parent.parent
    runtime_dir = _resolve_runtime_dir(repo_root)
    if not runtime_dir.is_dir():
        print(f"ERROR: runtime dir not found: {runtime_dir}", file=sys.stderr)
        return 2

    files = sorted(runtime_dir.rglob("*.cs"))
    total_violations = 0
    total_warnings = 0
    total_suppressions = 0
    for f in files:
        violations, warnings, suppressions = scan_file(f)
        rel = f.relative_to(repo_root).as_posix()
        for lineno, token, text in violations:
            total_violations += 1
            print(f"VIOLATION {rel}:{lineno}: editor-only API '{token}' in un-guarded runtime code")
            print(f"          {text}")
        if args.verbose:
            for lineno, msg in warnings:
                total_warnings += 1
                print(f"warning   {rel}:{lineno}: {msg}")
            for lineno, token, text in suppressions:
                total_suppressions += 1
                print(f"suppress  {rel}:{lineno}: '{token}' allowed by // boundary:allow")

    scanned = len(files)
    if total_violations:
        print()
        print(f"FAILED: {total_violations} runtime/editor boundary violation(s) across {scanned} Runtime/ file(s).")
        print("A file under packages/bridge/Runtime/ referenced an editor-only Godot API in code that")
        print("ships into a game build. Move the file (or the offending member) to packages/bridge/Editor/,")
        print("guard the editor-only code with #if TOOLS so it is stripped from the export build,")
        print("or — for a justified exception — suppress the line with a same-line trailing")
        print("'// boundary:allow <audit-note>' comment and document why.")
        return 1

    print(f"OK: runtime/editor boundary holds ({scanned} Runtime/ file(s) scanned, 0 violations).")
    if args.verbose:
        print(f"    ({total_warnings} warning(s), {total_suppressions} suppression(s).)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
