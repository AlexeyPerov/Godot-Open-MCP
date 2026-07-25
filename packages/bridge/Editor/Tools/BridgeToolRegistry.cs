#if TOOLS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Bridge-side tool registry (P2.1). Maps a tool name (<c>godot_open_mcp_*</c>) to its
    /// handler delegate so the HTTP dispatcher (<see cref="BridgeHttpServer"/>) can resolve a
    /// <c>POST /tools/{name}</c> request without a giant switch statement. Each entry also
    /// carries the catalog metadata the bridge AGENTS.md mandates: whether the tool is mutating,
    /// its default gate mode, and its group id (the latter two are forward-compat no-ops until
    /// P3.5 / P8.1).
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>BridgeToolRegistry</c> (copy fidelity for the
    /// name→handler map + <c>TryDispatch</c> shape), but starts minimal and manual: Unity scans
    /// assemblies for <c>[BridgeTool]</c>-attributed methods at boot, while P2.1 registers the
    /// single smoke stub (<see cref="EchoToolName"/>) explicitly from
    /// <c>GodotOpenMcpPlugin._EnterTree</c>. Real tool families (P2.2+) register themselves the
    /// same way — attribute-based discovery is a later-phase convenience, not a P2.1 requirement.
    /// </para>
    /// </summary>
    internal static class BridgeToolRegistry
    {
        /// <summary>
        /// The smoke stub tool registered in P2.1 to verify the dispatch round-trip before any real
        /// tool family lands. It echoes the parsed request body back as the result, so an E2E test
        /// can assert the envelope + main-thread hop without depending on Godot editor state.
        /// </summary>
        internal const string EchoToolName = "godot_open_mcp_echo";

        // Concurrent, not a plain Dictionary: Register runs on the editor main thread (from
        // GodotOpenMcpPlugin._EnterTree, ~40 calls per enable) while TryGet/TryDispatch run on HTTP
        // ThreadPool workers — HandleToolDispatch resolves the entry *before* hopping to the main
        // thread, and BridgeHttpServer.Stop only joins the listener thread, not in-flight handlers.
        // A plugin disable→enable cycle or assembly reload therefore re-registers every tool while a
        // worker may be mid-lookup, which on Dictionary can tear a read or hang inside the resize
        // path rather than merely returning a stale entry.
        static readonly ConcurrentDictionary<string, BridgeToolEntry> _tools =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Register a tool handler. Idempotent for the same name — a re-register replaces the
        /// entry (so a re-enable after a domain reload refreshes the handler reference without
        /// duplicate-key noise).
        /// </summary>
        internal static void Register(BridgeToolEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            _tools[entry.Name] = entry;
        }

        /// <summary>True when <paramref name="name"/> is a registered tool.</summary>
        internal static bool Contains(string name) => _tools.ContainsKey(name);

        /// <summary>
        /// Look up the entry for <paramref name="name"/>. Returns true + the entry via
        /// <paramref name="entry"/> when found; false otherwise. Mirrors Unity's
        /// <c>BridgeToolRegistry.TryGet</c>.
        /// </summary>
        internal static bool TryGet(string name, out BridgeToolEntry entry)
        {
            if (_tools.TryGetValue(name, out var found))
            {
                entry = found;
                return true;
            }
            entry = null!;
            return false;
        }

        /// <summary>
        /// Dispatch <paramref name="name"/> with <paramref name="body"/>. Returns the handler's
        /// <see cref="ToolDispatchResult"/>, or <c>null</c> when the tool is not registered (the
        /// caller surfaces <c>tool_not_found</c>). Mirrors Unity's
        /// <c>BridgeToolRegistry.TryDispatch</c>.
        /// </summary>
        internal static ToolDispatchResult? TryDispatch(string name, string body)
        {
            if (!_tools.TryGetValue(name, out var entry))
                return null;
            return entry.Handler(body);
        }

        /// <summary>Enumerate every registered entry (name + metadata). Used by the future
        /// <c>GET /tools</c> capability endpoint.</summary>
        internal static IEnumerable<BridgeToolEntry> All() => _tools.Values;

        /// <summary>
        /// Register the P2.1 smoke stub. Echoes the request body back as the result. Registered
        /// once at plugin enable; safe to call again on re-enable (idempotent replace).
        /// </summary>
        internal static void RegisterEchoStub()
        {
            Register(new BridgeToolEntry(
                name: EchoToolName,
                isMutating: false,
                defaultGate: "off",
                group: "core",
                handler: body =>
                {
                    // Echo the raw body back as {"echo": <body or null>}. A non-JSON body is
                    // surfaced as a string so the round-trip still completes — the stub's job is
                    // to prove the dispatch path, not to validate input.
                    var safe = string.IsNullOrEmpty(body) ? "null" : body;
                    return ToolDispatchResult.Ok("{\"echo\":" + safe + "}");
                }));
        }

        /// <summary>Test-only: clear every registered tool so a test starts from a clean registry.</summary>
        internal static void ResetForTests() => _tools.Clear();
    }

    /// <summary>
    /// One registered tool: its name, catalog metadata, and handler delegate. The handler takes
    /// the raw request body and returns a <see cref="ToolDispatchResult"/>; it runs on the editor
    /// main thread (the dispatcher marshals it there before invoking).
    /// </summary>
    internal sealed class BridgeToolEntry
    {
        /// <summary>The MCP tool name (<c>godot_open_mcp_*</c>).</summary>
        public string Name { get; }

        /// <summary>
        /// True when the tool mutates Godot project/editor state. Mutating tools will be routed
        /// through the gate flow in P3.5; in P2.1 the flag is catalog metadata only.
        /// </summary>
        public bool IsMutating { get; }

        /// <summary>
        /// The tool's recommended gate mode (<c>enforce</c> / <c>warn</c> / <c>off</c>).
        /// Forward-compat no-op until P3.5.
        /// </summary>
        public string DefaultGate { get; }

        /// <summary>
        /// Tool-group id from the canonical catalog (<c>core</c>, <c>node</c>, <c>scene</c>,
        /// <c>script</c>, ...). Forward-compat no-op until P8.1 (tool-group visibility).
        /// </summary>
        public string Group { get; }

        /// <summary>
        /// The handler invoked on the main thread. Takes the raw request body string and returns
        /// a dispatch result. Must not throw — exceptions are caught by the dispatcher and
        /// surfaced as <c>execution_error</c>.
        /// </summary>
        public Func<string, ToolDispatchResult> Handler { get; }

        public BridgeToolEntry(
            string name,
            bool isMutating,
            string defaultGate,
            string group,
            Func<string, ToolDispatchResult> handler)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            IsMutating = isMutating;
            DefaultGate = defaultGate;
            Group = group;
        }
    }
}
#endif
