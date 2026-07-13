#!/usr/bin/env node
// Bin shim — forwards to the compiled ESM entry (dist/index.js). Using a JS
// shim (not a direct `dist/index.js` bin) so the published layout stays stable
// even if the entry module moves, and the shebang lives in a plain JS file.
import "../dist/index.js";
