// Healthy demo fixture script — a minimal Godot `Node`-derived C# class that
// the main scene attaches to the `Main` root. Build-safe (compiles into the
// demo's single assembly alongside the materialized addon) and runtime-safe
// (no editor APIs, no `[Tool]`). The deterministic `FixtureMarker` constant
// exists so an offline `scene_get_data` / `resource_get_data` round-trip has
// a stable, assertable value. See demo/README.md.
//
// Part of the Godot Open MCP demo integration fixture (P9.4). NOT shipped in
// the addon — lives under demo/Scripts/ and compiles into GodotOpenMcp.Demo.
#nullable enable
using Godot;

namespace GodotOpenMcp.Demo.Scripts;

/// <summary>
/// Deterministic, assertable marker exposed by the demo root. Read by
/// `scripts/demo-smoke.mjs` after `scene_get_data res://Main.tscn` returns the
/// offline-parsed tree.
/// </summary>
public partial class DemoRoot : Node
{
    /// <summary>
    /// A stable scalar that an offline read can pin. Kept as a constant so it
    /// is embedded in the assembly and visible to reflection / inspector tools
    /// without runtime state.
    /// </summary>
    public const string FixtureMarker = "godot-open-mcp-demo-main";
}
