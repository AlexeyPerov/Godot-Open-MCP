#if TOOLS
#nullable enable

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Resolves the project-level gate default — the middle slot of the gate-precedence chain
    /// (<c>packages/bridge/AGENTS.md</c> §Gate policy):
    /// <list type="number">
    ///   <item>Request body <c>gate</c> (agent opt-in per call).</item>
    ///   <item>Project default (a <c>.godot-open-mcp/settings.json</c> the user sets — NOT implemented in v1).</item>
    ///   <item>Tool default (the <c>BridgeToolEntry.DefaultGate</c> every tool registers with).</item>
    /// </list>
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>BridgeGateDefaultPolicy</c> (copy fidelity for the valid-mode
    /// set and the precedence description; adapt for the storage). Unity reads its default from
    /// <c>BridgeProjectSettings</c> (<c>.unity-open-mcp/settings.json</c>); v1 here ships NO project
    /// settings file, so <see cref="GetProjectDefault"/> returns <c>null</c> and the dispatcher falls
    /// through to the tool default. Wiring the settings reader (P5+) is a localized change here — the
    /// precedence chain and the valid-mode set are the stable contract.
    /// </para>
    /// </summary>
    internal static class GateDefaultPolicy
    {
        /// <summary>Wire string for <see cref="GateMode.Enforce"/>.</summary>
        public const string Enforce = "enforce";

        /// <summary>Wire string for <see cref="GateMode.Warn"/>.</summary>
        public const string Warn = "warn";

        /// <summary>Wire string for <see cref="GateMode.Off"/>.</summary>
        public const string Off = "off";

        /// <summary>
        /// True when <paramref name="mode"/> is one of the three valid wire strings. Case-sensitive —
        /// matches <see cref="GatePolicy.ParseMode"/>, which treats an unknown value as Enforce, so a
        /// typo never silently disables the gate.
        /// </summary>
        internal static bool IsValid(string? mode) => mode == Enforce || mode == Warn || mode == Off;

        /// <summary>
        /// The project-level gate default, or <c>null</c> when no project setting is configured. v1 has
        /// no settings reader, so this always returns <c>null</c> and the dispatcher falls through to
        /// the tool's <c>DefaultGate</c>. The signature stays stable so the settings reader (P5+) is a
        /// localized change behind this method, not a dispatcher edit.
        /// </summary>
        internal static string? GetProjectDefault() => null;

        /// <summary>
        /// Human-readable precedence description, surfaced in tooling / docs. Mirrors Unity's
        /// <c>DescribePrecedence</c> so the agent-facing contract is identical across the two projects.
        /// </summary>
        internal static string DescribePrecedence() =>
            "Precedence: request body `gate` > project default (.godot-open-mcp/settings.json) > tool-level default.";
    }
}
#endif
