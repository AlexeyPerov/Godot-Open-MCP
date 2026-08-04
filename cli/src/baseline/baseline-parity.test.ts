// Parity guard for the CLI's baseline/offline copies (P15.2).
//
// The CLI copies five modules from `mcp-server/src/` verbatim (zero-dep, no
// cross-package import — see the header in `baseline-schema.ts`). This test
// asserts each CLI copy matches its mcp-server source byte-for-byte (after
// stripping the CLI copy-notice header block) so the two never drift silently.
//
// Mirrors the role of `instance-discovery.test.ts` (which guards the
// instance-discovery copy). When a module changes, update BOTH copies in the
// same commit.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
// Tests compile to dist-test/ (same subtree as src/), so from
// dist-test/baseline/ we go up two levels to cli/ then into src/.
const cliSrc = join(here, "..", "..", "src");
// From dist-test/baseline/ up three levels to the repo root.
const repoRoot = join(here, "..", "..", "..");
const mcpSrc = join(repoRoot, "mcp-server", "src");

interface CopyPair {
  /** CLI copy path, relative to cli/src. */
  cli: string;
  /** mcp-server source path, relative to mcp-server/src. */
  mcp: string;
}

const PAIRS: CopyPair[] = [
  { cli: "baseline/baseline-schema.ts", mcp: "baseline/baseline-schema.ts" },
  { cli: "baseline/regression-compare.ts", mcp: "baseline/regression-compare.ts" },
  { cli: "baseline/scan.ts", mcp: "baseline/scan.ts" },
  { cli: "offline/project-index.ts", mcp: "offline/project-index.ts" },
  { cli: "offline/project-paths.ts", mcp: "offline/project-paths.ts" },
];

/**
 * Strip the `[CLI COPY]` notice block from a CLI copy so the body can be
 * compared against the mcp-server source. The notice is a contiguous block of
 * `//` comment lines starting with the `[CLI COPY]` marker and ending at the
 * bare `//` separator line that precedes the original module header. We strip
 * from the `[CLI COPY]` line through that separator (inclusive); the remainder
 * is the original module body, byte-identical to the source.
 */
function stripCliCopyNotice(text: string): string {
  const lines = text.split(/\r?\n/);
  // Find the [CLI COPY] marker line.
  const markerIdx = lines.findIndex((l) => l.includes("[CLI COPY]"));
  if (markerIdx < 0) return text; // no notice — nothing to strip
  // From the marker, find the next bare `//` separator line (the empty
  // comment line that delimits the notice from the original header).
  for (let i = markerIdx + 1; i < lines.length; i++) {
    if (lines[i]!.trim() === "//") {
      // Strip from the first line (before the marker there's nothing) through
      // the separator inclusive. Lines before the marker are preserved (none
      // in practice — the marker is always line 1).
      return lines.slice(0, markerIdx).concat(lines.slice(i + 1)).join("\n");
    }
  }
  // No separator found — fall back to stripping all leading comment lines.
  return lines.slice(0, markerIdx).concat(lines.slice(markerIdx + 1).filter((l) => !l.trimStart().startsWith("//"))).join("\n");
}

function readText(p: string): string {
  return readFileSync(p, "utf-8");
}

test("every CLI baseline/offline copy matches its mcp-server source", () => {
  for (const pair of PAIRS) {
    const cliText = readText(join(cliSrc, pair.cli));
    const mcpText = readText(join(mcpSrc, pair.mcp));
    const cliBody = stripCliCopyNotice(cliText);
    assert.equal(
      cliBody,
      mcpText,
      `CLI copy '${pair.cli}' has drifted from mcp-server '${pair.mcp}'. ` +
        `Update BOTH copies in the same commit (see baseline-schema.ts header).`,
    );
  }
});

test("every CLI copy carries the [CLI COPY] notice", () => {
  for (const pair of PAIRS) {
    const cliText = readText(join(cliSrc, pair.cli));
    assert.ok(
      cliText.includes("[CLI COPY]"),
      `CLI copy '${pair.cli}' is missing its [CLI COPY] notice header.`,
    );
  }
});
