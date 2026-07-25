// Tests for addon source resolution (src/utils/addon-source.ts).
//
// The monorepo-relative default is the documented behavior of `install-plugin` when `--source` is
// omitted, and it is resolved from this module's own location. That resolution used `__dirname`,
// which does not exist in this package (cli/package.json sets `"type": "module"`), so every
// `install-plugin` without `--source` threw `ReferenceError: __dirname is not defined`. It was
// invisible to `tsc` because @types/node declares `__dirname` as a global, and invisible to CI
// because scripts/prepare-demo-addon.mjs always passes an explicit `--source`.
//
// These tests pin the ESM-safe behavior: the function must be callable and must never throw.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";

import { defaultMonorepoSource } from "./addon-source.js";

test("defaultMonorepoSource: does not throw (no __dirname in an ESM package)", () => {
  // The regression this guards is a ReferenceError here, not a particular return value.
  assert.doesNotThrow(() => defaultMonorepoSource());
});

test("defaultMonorepoSource: returns null or an existing packages/bridge dir", () => {
  const result = defaultMonorepoSource();
  if (result === null) return; // npm-installed CLI with no checkout nearby — a valid outcome
  assert.equal(typeof result, "string");
  assert.ok(path.isAbsolute(result), "the resolved source must be an absolute path");
  assert.ok(
    fs.existsSync(path.join(result, "plugin.cfg")),
    "a non-null result must point at a directory containing plugin.cfg",
  );
});

test("defaultMonorepoSource: resolves to packages/bridge when run from the checkout", () => {
  const result = defaultMonorepoSource();
  if (result === null) return; // not running from a checkout
  assert.equal(path.basename(result), "bridge");
  assert.equal(path.basename(path.dirname(result)), "packages");
});

test("defaultMonorepoSource: is stable across calls", () => {
  assert.equal(defaultMonorepoSource(), defaultMonorepoSource());
});
