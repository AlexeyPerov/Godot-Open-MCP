// P5.4 — bridge event stream client.
//
// The MCP server runs behind a stdio transport, so it can't directly forward bridge SSE to MCP
// notifications without a background SSE reader. This module owns that reader: a single SSE
// subscription per server process, parsed into events and pushed into an in-memory queue. The
// `godot_open_mcp_pull_events` tool drains the queue per call so an agent can poll incremental
// console / editor-state events without burning an HTTP round-trip on every check.
//
// Lifecycle:
//   - ensureSubscription() is called on every tool dispatch; it starts the SSE reader once and is a
//     no-op afterwards.
//   - The reader reconnects with the last subscriber id so it keeps its cursor across disconnects
//     (a 10-minute SSE timeout or a Godot reload).
//   - On bridge-unavailable (connection refused), ensureSubscription records the failure and the
//     tool surfaces a `connected:false` + `lastError` result instead of throwing.
//
// No new runtime deps — uses only `node:crypto`, the global `fetch`, and ReadableStream.
//
// Adapted from Unity Open MCP's mcp-server/src/event-stream.ts (copy fidelity): same QUEUE_CAPACITY
// (500 client-side), same parseSseBlock contract, same reconnect-via-subscriber-id, same auth
// header merge. Intentional deltas: Godot tool name (`godot_open_mcp_pull_events`, no
// `unity_senses_*` alias); no tool-router wrapper — the index.ts dispatcher calls pull() directly.

import { randomBytes } from "node:crypto";

export interface BridgeEvent {
  seq: number;
  ts: string;
  type: "log" | "editor_state" | "ready" | "missed" | "close";
  logType?: string;
  message?: string;
  stack?: string;
  state?: string;
  isCompiling?: boolean;
  isPlaying?: boolean;
}

export interface PullResult {
  subscriberId: string;
  events: BridgeEvent[];
  /** Count of events dropped from the queue before this pull (overflow). */
  dropped: number;
  /** Whether the SSE reader is currently connected. */
  connected: boolean;
  /** Whether this pull started the subscription (first call). */
  started: boolean;
  /** Last reconnect failure reason, when `connected` is false. */
  lastError: string | null;
}

/** Client-side queue cap. Distinct from the bridge ring's 1024. */
const QUEUE_CAPACITY = 500;

/** Reconnect backoff after a dropped / failed SSE connection (ms). */
const RECONNECT_MS = 2000;

export class BridgeEventStream {
  private subscriberId: string;
  private queue: BridgeEvent[] = [];
  private dropped = 0;
  private connected = false;
  private lastError: string | null = null;
  private abortController: AbortController | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private started = false;
  /**
   * Latched by {@link stop} so work that settles *after* the stop cannot resurrect the subscription.
   * Both the connect `.catch` and `pump`'s `finally` call {@link scheduleReconnect}, and aborting the
   * controller is what makes them run — so without this latch, `stop()` armed a fresh reconnect
   * timer, whose `connect()` failed, whose `.catch` armed another, forever. The process could then
   * never exit (an unref'd timer chain plus a live loopback fetch), which is why the shutdown
   * `eventStream.stop()` in the CLI entrypoint had no effect.
   */
  private stopped = false;

  constructor(
    private readonly baseUrl: string,
    subscriberId?: string,
    private readonly authToken?: string,
  ) {
    this.subscriberId = subscriberId ?? randomBytes(16).toString("hex");
  }

  /**
   * Start the SSE reader if it isn't running. Safe to call repeatedly; returns true when a fresh
   * subscription was started this call. The reader persists across pull() calls so every pull
   * amortizes one connection.
   */
  ensureSubscription(): boolean {
    if (this.started) return false;
    // A fresh subscription clears the stop latch, so a stream can be restarted after stop().
    this.stopped = false;
    this.started = true;
    this.connect();
    return true;
  }

  private connect(): void {
    if (this.stopped) return;
    if (this.abortController) return;
    this.abortController = new AbortController();
    const url =
      `${this.baseUrl}/events?subscriber=${encodeURIComponent(this.subscriberId)}&max_per_poll=100`;

    // SSE read is streaming; consume the body manually so we can split on double-newline event
    // boundaries. P5.2 — carry the bearer token so the stream is gated the same way as tool/ping
    // requests.
    const headers: Record<string, string> = { Accept: "text/event-stream" };
    if (this.authToken) headers["Authorization"] = `Bearer ${this.authToken}`;
    fetch(url, {
      method: "GET",
      headers,
      signal: this.abortController.signal,
    })
      .then((res) => {
        if (!res.ok || !res.body) {
          throw new Error(`HTTP ${res.status}`);
        }
        this.connected = true;
        this.lastError = null;
        this.pump(res.body);
      })
      .catch((err: unknown) => {
        this.connected = false;
        const message = err instanceof Error ? err.message : String(err);
        // AbortError means we intentionally stopped; treat as a clean disconnect (no lastError).
        this.lastError = message.includes("abort") ? null : message;
        this.scheduleReconnect();
      });
  }

  private async pump(body: ReadableStream<Uint8Array>): Promise<void> {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    try {
      // eslint-disable-next-line no-constant-condition
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        let sep: number;
        // SSE events are separated by a blank line.
        while ((sep = buffer.indexOf("\n\n")) >= 0) {
          const block = buffer.slice(0, sep);
          buffer = buffer.slice(sep + 2);
          this.handleBlock(block);
        }
      }
    } catch (err: unknown) {
      // Network drop mid-stream — schedule a reconnect and record the failure.
      const message = err instanceof Error ? err.message : String(err);
      this.lastError = message;
    } finally {
      this.connected = false;
      try {
        await reader.cancel();
      } catch {
        /* ignore */
      }
      this.scheduleReconnect();
    }
  }

  private handleBlock(block: string): void {
    const evt = BridgeEventStream.parseSseBlock(block);
    if (evt) this.enqueue(evt);
  }

  /**
   * Parse one SSE block (the text between two blank-line separators) into a BridgeEvent, or null
   * when the block carries no data. Exposed (static) for unit testing — the SSE reader loop calls it
   * via handleBlock.
   *
   * <para>
   * Reassembles multi-line <c>data:</c> blocks (e.g. a log stack split across lines) with embedded
   * newlines before JSON.parse. Non-JSON payloads (e.g. control events with a raw string) are kept
   * as `{raw}` rather than throwing. Falls back seq → Date.now() and ts → now() when the bridge
   * omits them.
   * </para>
   */
  static parseSseBlock(block: string): BridgeEvent | null {
    let eventName = "message";
    const dataLines: string[] = [];
    for (const line of block.split("\n")) {
      if (line.startsWith("event:")) {
        eventName = line.slice(6).trim();
      } else if (line.startsWith("data:")) {
        // SSE spec: a single leading space after the colon is stripped.
        dataLines.push(line.slice(5).replace(/^ /, ""));
      }
    }
    if (dataLines.length === 0) return null;
    const data = dataLines.join("\n");

    let parsed: Record<string, unknown> = {};
    try {
      parsed = JSON.parse(data) as Record<string, unknown>;
    } catch {
      // Non-JSON payload (e.g. a close reason); keep raw so the agent still sees the event.
      parsed = { raw: data };
    }

    const evt: BridgeEvent = {
      seq: typeof parsed.seq === "number" ? parsed.seq : Date.now(),
      ts: typeof parsed.ts === "string" ? parsed.ts : new Date().toISOString(),
      type: eventName as BridgeEvent["type"],
    };

    if (eventName === "log") {
      evt.logType = typeof parsed.logType === "string" ? parsed.logType : "log";
      evt.message = typeof parsed.message === "string" ? parsed.message : "";
      evt.stack = typeof parsed.stack === "string" ? parsed.stack : undefined;
    } else if (eventName === "editor_state") {
      evt.state = typeof parsed.state === "string" ? parsed.state : "";
      evt.isCompiling = parsed.isCompiling === true;
      evt.isPlaying = parsed.isPlaying === true;
    } else if (eventName === "missed") {
      // SSE "missed" marker — surfaces as its own event so the agent sees the ring gap.
      evt.message = `missed=${parsed.missed ?? "unknown"}`;
    }

    return evt;
  }

  private enqueue(evt: BridgeEvent): void {
    this.queue.push(evt);
    while (this.queue.length > QUEUE_CAPACITY) {
      this.queue.shift();
      this.dropped++;
    }
  }

  private scheduleReconnect(): void {
    if (this.stopped) return;
    if (this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.abortController = null;
      this.connect();
    }, RECONNECT_MS);
  }

  /** Drain all queued events. The subscription keeps running. Clamps to (0, 1000], default 100. */
  drain(maxEvents: number): BridgeEvent[] {
    const cap = maxEvents > 0 && maxEvents <= 1000 ? maxEvents : 100;
    const out = this.queue.slice(0, cap);
    this.queue = this.queue.slice(cap);
    return out;
  }

  /** One-shot pull: start the subscription if needed and drain. Returns the pull result envelope. */
  pull(maxEvents: number): PullResult {
    const started = this.ensureSubscription();
    const events = this.drain(maxEvents);
    return {
      subscriberId: this.subscriberId,
      events,
      dropped: this.dropped,
      connected: this.connected,
      started,
      lastError: this.lastError,
    };
  }

  /** Stop the reader and clear state. Idempotent; used in tests and on server shutdown. */
  stop(): void {
    // Latch first: aborting the controller below makes any in-flight fetch/pump settle, and their
    // handlers call scheduleReconnect().
    this.stopped = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.abortController) {
      this.abortController.abort();
      this.abortController = null;
    }
    this.started = false;
    this.connected = false;
    this.queue = [];
    this.dropped = 0;
  }

  /** Whether the SSE reader is currently connected (best-effort snapshot for the tool result). */
  get isConnected(): boolean {
    return this.connected;
  }

  /** The subscriber id used across reconnects. */
  get id(): string {
    return this.subscriberId;
  }
}
