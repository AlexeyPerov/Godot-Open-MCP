// `godot_open_mcp_reflection_method_call` tool definition (P5.1).
//
// Mutating (default gate "enforce"). Invokes a resolved C# method via reflection — static or
// instance — and returns a JSON-serializable result. The handler lives in the bridge
// (POST /tools/godot_open_mcp_reflection_method_call); this file is the catalog metadata only —
// name / description / input schema — advertised to AI clients over stdio ListTools.
//
// Adapted from Unity Open MCP's mcp-server/src/tools/invoke-method.ts (adapt fidelity): the same
// type_name / method_name / args / arg_type_names / generic_arg_types / is_static / assembly_name /
// max_depth surface, plus the same overload disambiguation (arg_type_names), generic binding
// (generic_arg_types), and CLR alias acceptance. The Godot-specific targeting deltas
// (documented in ReflectionTools.Call):
//   - `node_path` is the PRIMARY instance target — resolves a node from the edited scene (Godot-native,
//     stable across reloads). Replaces Unity's object_id-first targeting.
//   - `object_id` is accepted but only honored against a stable handle registry; v1 has none, so a
//     non-zero object_id surfaces 'unsupported_target' rather than silently fabricating an instance.
//   - `execute_in_main_thread` (default true) is accepted and echoed; the dispatcher already marshals
//     every handler to the main thread in v1, so the flag is a forward-compat no-op for an off-thread
//     invoke path (deferred). It does NOT bypass the main-thread hop today.
// Activator.CreateInstance is restricted to pure POCOs — Godot.Object subclasses require an explicit
// node_path target.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// The bridge fetch timeout default (mirrors `BRIDGE_DEFAULT_TIMEOUT_MS` in live-client.ts). Inlined
// here rather than imported because the constant lives as a module-private in live-client; the other
// mutating tool definitions (editor-application-set-state) follow the same inline-default convention.
const BRIDGE_DEFAULT_TIMEOUT_MS = 30_000;

export const reflectionMethodCall: Tool = {
  name: "godot_open_mcp_reflection_method_call",
  description:
    "Invoke a C# method via reflection — static or instance — and return a JSON-serializable " +
    "result. Mutating: runs the full gate cycle (checkpoint → invoke → validate → delta) by " +
    "default. Supports generic methods via generic_arg_types and overload disambiguation via " +
    "arg_type_names (when multiple methods share a name). Instance targets are resolved by node_path " +
    "(a node in the edited scene — the primary Godot-native target); Activator is allowed only for " +
    "pure POCOs, never Godot.Object subclasses. The invoke runs on the editor main thread. The " +
    "return value is serialized with depth + cycle + per-list-item guards (Godot.Object instances " +
    "are summarized, not walked). Use reflection_method_find first to discover exact signatures. " +
    "Error codes: validation_error (missing type_name/method_name), type_not_found, method_not_found, " +
    "ambiguous_match (multiple overloads, no arg_type_names), invalid_argument (count/type coercion), " +
    "missing_target, unsupported_target (object_id without a handle registry), instantiation_error, " +
    "invoke_failed (target threw — message includes exception type + message, no large stacks).",
  inputSchema: {
    type: "object",
    required: ["type_name", "method_name", "paths_hint"],
    properties: {
      type_name: {
        type: "string",
        description:
          "Required: declaring type full name (preferred) or simple name. Use " +
          "reflection_method_find to discover the exact name. assembly_name disambiguates when the " +
          "simple name is ambiguous.",
      },
      method_name: {
        type: "string",
        description: "Required: method name to invoke. Overloads are disambiguated by arg_type_names.",
      },
      args: {
        type: "array",
        items: {},
        description:
          "Positional arguments (JSON-serializable). Coerced to the resolved parameter types: " +
          "primitives (bool/int/long/float/double/string/char), enums (by name, case-insensitive), " +
          "Nullable<T>. Nested objects/arrays are passed as raw JSON strings in v1.",
      },
      arg_type_names: {
        type: "array",
        items: { type: "string" },
        description:
          "Optional explicit parameter type names (full or simple, e.g. " +
          "[\"Godot.Node\", \"Int32\"]) used to disambiguate overloads when multiple methods share " +
          "method_name. Length must match the overload's parameter count. CLR aliases (int/Int32, " +
          "float/Single, ...) are accepted. When omitted and the name is ambiguous, the dispatch " +
          "fails with 'ambiguous_match'.",
      },
      generic_arg_types: {
        type: "array",
        items: { type: "string" },
        description:
          "Type-name strings substituted for the method's generic parameters when invoking a generic " +
          "method. Length must match the method's generic parameter count. Omit for non-generic methods.",
      },
      is_static: {
        type: "boolean",
        default: false,
        description:
          "True to invoke a static method (no instance target). Default false (instance method).",
      },
      assembly_name: {
        type: "string",
        description: "Optional assembly simple name to disambiguate an ambiguous type_name.",
      },
      node_path: {
        type: "string",
        description:
          "Primary instance target: scene-tree path relative to the edited scene root " +
          "('Main/Player', '/root/Main/Player', or '.' for the root). The resolved node must be " +
          "assignable to type_name. Required for instance methods on Godot.Object subclasses.",
      },
      object_id: {
        type: "integer",
        default: 0,
        description:
          "Secondary instance target: a stable handle registry id from a prior tool. v1 has no " +
          "handle registry, so a non-zero object_id fails with 'unsupported_target'. Prefer node_path.",
      },
      execute_in_main_thread: {
        type: "boolean",
        default: true,
        description:
          "Echoed in the result; the invoke always runs on the main thread in v1 (forward-compat " +
          "flag for a future off-thread pure-logic path).",
      },
      max_depth: {
        type: "integer",
        default: 4,
        minimum: 0,
        description: "Max recursion depth when serializing the returned object graph (default 4).",
      },
      max_items: {
        type: "integer",
        default: 100,
        minimum: 0,
        description:
          "Max items emitted per list/enumerable in the returned object graph (default 100). " +
          "Truncated lists report a __truncated__ count.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the gate validates only these paths after the invoke. Mandatory for a " +
          "mutating call (the dispatcher rejects an empty hint with 'paths_hint_required' before the " +
          "invoke runs). Scope to whatever the invoke may touch (e.g. a res:// resource path or a " +
          "scene path); there is no whole-project fallback.",
      },
      gate: {
        type: "string",
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. 'enforce' (default): run checkpoint → invoke → validate → delta; new errors " +
          "fail the dispatch. 'warn': run the cycle but never hard-fail. 'off': skip the cycle " +
          "(paths_hint is still required).",
      },
      timeout_ms: {
        type: "integer",
        default: BRIDGE_DEFAULT_TIMEOUT_MS,
        description: "Max milliseconds to wait for the invoke to complete on the main thread.",
      },
    },
    additionalProperties: false,
  },
};
