#!/usr/bin/env node
// P1.9 phase-gate parity smoke — canonical end-to-end route.
//
// Spawns the built stdio MCP server (`dist/index.js`) as a child process,
// points it at a real loopback bridge stub started in this same script, and
// drives the full MCP wire flow (initialize → tools/list → tools/call) over a
// real stdio pipe using the SDK's own `StdioClientTransport`. Then verifies:
//
//   - tools/list advertises `godot_open_mcp_ping`,
//   - `tools/call godot_open_mcp_ping` returns the live PingResponse body
//     from the bridge stub (the "smoke passes" criterion),
//   - a "bridge down" sub-case surfaces a structured `bridge_offline` error
//     that names this project's lock file (the "failure output is actionable"
//     criterion).
//
// This is the scripted, OS-level companion to the in-process integration test
// (`src/integration.test.ts`). The integration test runs on every `npm test`;
// this script is the manual / CI gate that exercises the real stdio boot path
// and the `dist/index.js` build artifact.
//
// Usage:
//   node mcp-server/scripts/p1-parity-smoke.mjs
//   node mcp-server/scripts/p1-parity-smoke.mjs --bridge-down   # failure sub-case only
//
// Exit codes: 0 = pass, 1 = fail, 2 = bootstrap error (server spawn / build).

import { createServer } from "node:http";
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const here = dirname(fileURLToPath(import.meta.url));
const distIndex = resolve(here, "..", "dist", "index.js");

const HEALTHY_PING = {
  connected: true,
  projectPath: "/home/user/MyGame",
  godotVersion: "4.3.1.stable.mono",
  bridgeVersion: "0.0.1",
  mode: "live",
  compiling: false,
  isPlaying: false,
};

const PROJECT_PATH = "/home/user/MyGame";

// ---------------------------------------------------------------------------
// Bridge stub: minimal loopback HTTP server that serves the deterministic
// /ping payload. Mirrors the bridge's BridgeJson.BuildPingJson field set.
// ---------------------------------------------------------------------------

function startBridgeStub(port = 0) {
  return new Promise((resolveBoot, rejectBoot) => {
    const server = createServer((req, res) => {
      if (req.url === "/ping") {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(HEALTHY_PING));
        return;
      }
      res.writeHead(404);
      res.end();
    });
    server.on("error", rejectBoot);
    server.listen(port, "127.0.0.1", () => {
      const addr = server.address();
      const bound = typeof addr === "object" && addr ? addr.port : 0;
      resolveBoot({
        port: bound,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

// ---------------------------------------------------------------------------
// MCP client over a real stdio pipe to the spawned server child.
// ---------------------------------------------------------------------------

async function bootMcpServer(bridgePort) {
  // The SDK's StdioClientTransport spawns the server child itself from a
  // { command, args, env, cwd } descriptor. We hand it the same `node
  // dist/index.js` invocation the production stdio config uses, plus the env
  // override that aims the server at our bridge stub.
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [distIndex],
    stderr: "inherit",
    env: {
      // Point the server at the stub. The override bypasses the deterministic
      // port formula so we never collide with a real Godot editor running on
      // the same project.
      GODOT_OPEN_MCP_BRIDGE_PORT: String(bridgePort),
      GODOT_PROJECT_PATH: PROJECT_PATH,
    },
  });

  const client = new Client(
    { name: "p1-parity-smoke", version: "0.0.0-smoke" },
    { capabilities: {} },
  );
  await client.connect(transport);

  return {
    client,
    shutdown: async () => {
      try { await client.close(); } catch {}
      // Closing the client transport closes the server's stdin; the server
      // process exits on its own via the transport.onclose hook. Wait briefly
      // so a follow-up case does not race a zombie.
    },
  };
}

function assert(cond, msg) {
  if (!cond) {
    throw new Error(`ASSERT FAILED: ${msg}`);
  }
}

async function runHealthyCase() {
  process.stderr.write("[p1-smoke] case: healthy bridge → tools/list + tools/call ping\n");
  const bridge = await startBridgeStub();
  let mcp;
  try {
    mcp = await bootMcpServer(bridge.port);

    // 1. tools/list must advertise godot_open_mcp_ping.
    const { tools } = await mcp.client.listTools();
    const names = tools.map((t) => t.name);
    assert(
      names.includes("godot_open_mcp_ping"),
      `tools/list must advertise godot_open_mcp_ping (got: ${names.join(", ")})`,
    );

    // 2. tools/call ping returns the live PingResponse body.
    const result = await mcp.client.callTool({
      name: "godot_open_mcp_ping",
      arguments: {},
    });
    assert(result.isError !== true, "healthy ping must not be isError");
    assert(Array.isArray(result.content) && result.content.length > 0, "content array non-empty");
    const block = result.content[0];
    assert(block.type === "text", "first content block is text");
    const body = JSON.parse(block.text);
    for (const key of Object.keys(HEALTHY_PING)) {
      assert(
        JSON.stringify(body[key]) === JSON.stringify(HEALTHY_PING[key]),
        `ping body field '${key}' = ${JSON.stringify(body[key])} (expected ${JSON.stringify(HEALTHY_PING[key])})`,
      );
    }
    process.stderr.write("[p1-smoke]   ✔ healthy case passed\n");
  } finally {
    if (mcp) await mcp.shutdown();
    await bridge.close();
  }
}

async function runBridgeDownCase() {
  process.stderr.write("[p1-smoke] case: bridge down → structured bridge_offline with lock-file hint\n");
  // Boot the bridge stub briefly to discover a free port, then close it so the
  // server points at a port with no listener.
  const probe = await startBridgeStub();
  const downPort = probe.port;
  await probe.close();

  let mcp;
  try {
    mcp = await bootMcpServer(downPort);
    const result = await mcp.client.callTool({
      name: "godot_open_mcp_ping",
      arguments: {},
    });
    assert(result.isError === true, "bridge-down ping must be isError");
    const block = result.content[0];
    assert(block.type === "text", "first content block is text");
    const body = JSON.parse(block.text);
    assert(body.error?.code === "bridge_offline", `error code = ${body.error?.code}`);
    assert(
      /~\/\.godot-open-mcp\/instances\//.test(body.error?.message ?? ""),
      `offline hint must name the project lock file (message: ${body.error?.message})`,
    );
    process.stderr.write("[p1-smoke]   ✔ bridge-down case passed\n");
    // Report the actionable hint so a human running the smoke sees the owner
    // area on failure too.
    process.stderr.write(`[p1-smoke]   offline hint: ${body.error.message}\n`);
  } finally {
    if (mcp) await mcp.shutdown();
  }
}

async function main() {
  const args = new Set(process.argv.slice(2));
  const onlyDown = args.has("--bridge-down");

  process.stderr.write(`[p1-smoke] P1.9 phase-gate parity smoke\n`);
  process.stderr.write(`[p1-smoke] server: ${distIndex}\n`);

  // Bootstrap check: the server build must exist. CI runs `npm run build`
  // before this; a local developer must too.
  if (!existsSync(distIndex)) {
    process.stderr.write(
      `[p1-smoke] FATAL: ${distIndex} not found. Run \`npm run build\` in mcp-server/ first.\n`,
    );
    process.exit(2);
  }

  try {
    if (!onlyDown) {
      await runHealthyCase();
    }
    await runBridgeDownCase();
    process.stderr.write("[p1-smoke] PASS — P1.9 parity smoke green\n");
    process.exit(0);
  } catch (err) {
    process.stderr.write(`[p1-smoke] FAIL — ${err?.message ?? err}\n`);
    if (err?.stack) process.stderr.write(err.stack + "\n");
    process.exit(1);
  }
}

main().catch((err) => {
  process.stderr.write(`[p1-smoke] bootstrap error: ${err?.message ?? err}\n`);
  process.exit(2);
});
