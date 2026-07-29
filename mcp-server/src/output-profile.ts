// Token-budget-aware output profiles + uniform paging.
//
// Single source of truth for two envelope concerns shared by the heavy tools
// (find_references / dependencies and future readers):
//
//  1. **Output profiles** (`compact` | `balanced` | `full`) — a uniform knob that
//     maps onto each tool's existing `detail` axis (summary / normal / verbose).
//     The profile is the public, documented param; `detail` remains as a
//     backwards-compatible alias.
//
//  2. **Uniform paging** (`page_size` / `cursor` / `next_cursor`) — a resumable
//     cursor. Legacy caps stay as aliases (they request a single bounded page).
//     Every paginated response carries a `pagination` block.
//
// Copied from Unity Open MCP's mcp-server/src/output-profile.ts (copy fidelity
// for the profile + paging contract). Intentional deltas: Godot omits the
// verify-result folding helpers until a Godot consumer needs them; the cursor
// / profile / applyPaging surface is byte-compatible with Unity's.

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

// ===========================================================================
// Output profiles.
// ===========================================================================

export type OutputProfile = "compact" | "balanced" | "full";

export const PROFILES: readonly OutputProfile[] = ["compact", "balanced", "full"];

export function isOutputProfile(value: unknown): value is OutputProfile {
  return value === "compact" || value === "balanced" || value === "full";
}

/**
 * The per-tool `detail` string each profile maps to. The detail axis already
 * exists on every heavy tool (summary / normal / verbose); the profile is the
 * documented public name for it.
 */
export type DetailLevel = "summary" | "normal" | "verbose";

export function profileToDetail(profile: OutputProfile): DetailLevel {
  switch (profile) {
    case "compact":
      return "summary";
    case "balanced":
      return "normal";
    case "full":
      return "verbose";
  }
}

/**
 * Resolve the effective detail level for a call.
 *
 * Precedence: an explicit `profile` wins; otherwise an explicit legacy `detail`
 * is honored (back-compat alias); otherwise the per-tool `fallback` is used.
 * `compact` is the documented default for the heavy families.
 */
export function resolveDetail(
  profile: OutputProfile | undefined,
  legacyDetail: string | undefined,
  fallback: DetailLevel,
): DetailLevel {
  if (profile) return profileToDetail(profile);
  if (
    legacyDetail === "summary" ||
    legacyDetail === "normal" ||
    legacyDetail === "verbose"
  ) {
    return legacyDetail;
  }
  return fallback;
}

/**
 * Read `profile` / `detail` off a raw args object and return the effective
 * detail level. Returns the profile too (when present) so callers can branch on
 * compact vs. expanded for result folding.
 */
export function readProfileAndDetail(
  args: Record<string, unknown>,
  fallback: DetailLevel = "summary",
): { detail: DetailLevel; profile: OutputProfile | undefined } {
  const profileRaw = args.profile;
  const profile = isOutputProfile(profileRaw) ? profileRaw : undefined;
  const legacyDetail = typeof args.detail === "string" ? args.detail : undefined;
  return { detail: resolveDetail(profile, legacyDetail, fallback), profile };
}

// ===========================================================================
// Cursor encode / decode.
//
// The cursor is an opaque continuation token, not a capability: it is a plain
// human-readable string of the form `<toolKey>:<offset>`. We keep it readable
// (no base64) so it shows up cleanly in logs and agent transcripts, and so a
// mismatched cursor (wrong tool) fails loudly instead of silently paging the
// wrong data.
// ===========================================================================

export interface CursorParts {
  toolKey: string;
  offset: number;
}

export function encodeCursor(toolKey: string, offset: number): string {
  return `${toolKey}:${offset}`;
}

export function splitCursor(cursor: string | undefined, expectedToolKey: string): number {
  if (typeof cursor !== "string" || cursor === "") return 0;
  const colon = cursor.lastIndexOf(":");
  if (colon <= 0) return 0;
  const toolKey = cursor.slice(0, colon);
  const offsetStr = cursor.slice(colon + 1);
  // A cursor minted for a different tool (or a stale cursor from before a tool
  // rename) must not silently page the wrong data — treat it as "start over".
  if (toolKey !== expectedToolKey) return 0;
  const offset = parseNonNegativeInt(offsetStr);
  return offset === null ? 0 : offset;
}

function parseNonNegativeInt(value: string): number | null {
  if (value.length === 0) return null;
  let out = 0;
  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    if (ch < "0" || ch > "9") return null;
    out = out * 10 + (ch.charCodeAt(0) - "0".charCodeAt(0));
  }
  return out;
}

// ===========================================================================
// Paging.
// ===========================================================================

export interface PagingInput {
  page_size?: number;
  cursor?: string;
}

export interface PaginationBlock {
  /** Effective page size applied (clamped to >=1). */
  page_size: number;
  /** Cursor this page was requested with (null when this is the first page). */
  cursor: string | null;
  /** Cursor to fetch the next page (null when this is the last page). */
  next_cursor: string | null;
  /** Items remaining after this page (the resumable tail). */
  truncated: number;
}

export interface PageResult<T> {
  page: T[];
  block: PaginationBlock;
}

/**
 * Slice `items` into one page starting at the cursor offset.
 *
 * - `page_size` <= 0 ⇒ disabled: returns the whole list with a terminal block
 *   (next_cursor null, truncated 0). This is the back-compat / alias path.
 * - A cursor with the wrong tool key resets to offset 0 (see splitCursor).
 * - Count invariant: `page.length + block.truncated == items.length - offset`.
 */
export function applyPaging<T>(
  items: readonly T[],
  toolKey: string,
  input: PagingInput,
): PageResult<T> {
  const pageSize =
    typeof input.page_size === "number" && input.page_size > 0
      ? Math.floor(input.page_size)
      : 0;

  if (pageSize <= 0) {
    return {
      page: items.slice(),
      block: {
        page_size: 0,
        cursor:
          typeof input.cursor === "string" && input.cursor !== ""
            ? input.cursor
            : null,
        next_cursor: null,
        truncated: 0,
      },
    };
  }

  const offset = Math.min(splitCursor(input.cursor, toolKey), items.length);
  const page = items.slice(offset, offset + pageSize);
  const remaining = items.length - (offset + page.length);

  const nextCursor =
    remaining > 0 ? encodeCursor(toolKey, offset + page.length) : null;

  return {
    page,
    block: {
      page_size: pageSize,
      cursor:
        typeof input.cursor === "string" && input.cursor !== ""
          ? input.cursor
          : null,
      next_cursor: nextCursor,
      truncated: remaining,
    },
  };
}

/**
 * Return a shallow copy of `result` with `pagination` set. Idempotent: a result
 * that already carries a `pagination` block is returned unchanged.
 */
export function attachPagination<T extends object>(
  result: T,
  block: PaginationBlock,
): T & { pagination: PaginationBlock } {
  const r = result as T & { pagination?: PaginationBlock };
  if (r.pagination !== undefined) return r as T & { pagination: PaginationBlock };
  return { ...result, pagination: block };
}

// ===========================================================================
// Result-block helpers — operate on the CallToolResult text block.
// ===========================================================================

/**
 * Parse the first text content block of a CallToolResult as JSON. Returns null
 * when there is no text block or it is not a JSON object.
 */
export function parseResultBody(result: {
  content: ReadonlyArray<{ type: string; text?: unknown }>;
}): Record<string, unknown> | null {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    return null;
  }
  try {
    const parsed = JSON.parse(first.text);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
  } catch {
    // fall through
  }
  return null;
}

/**
 * Rewrite the first text content block of a CallToolResult with a new JSON
 * body. Other content blocks are preserved untouched.
 */
export function withResultBody(
  result: CallToolResult,
  body: Record<string, unknown>,
): CallToolResult {
  const textIndex = result.content.findIndex((c) => c.type === "text");
  const newBlock = { type: "text" as const, text: JSON.stringify(body) };
  if (textIndex < 0) {
    return { ...result, content: [newBlock, ...result.content] };
  }
  const newContent = result.content.slice();
  newContent[textIndex] = newBlock;
  return { ...result, content: newContent };
}
