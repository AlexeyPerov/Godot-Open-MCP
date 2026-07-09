#if TOOLS
#nullable enable

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Registration entry point for the P3.6 gate meta-tools — the explicit pre/post-mutation gate
    /// workflow surface. Mirrors <see cref="NodeTools.RegisterNodeTools"/> / SceneTools.RegisterSceneTools:
    /// a single <c>Register*Tools()</c> the plugin calls on enable, registering every tool family
    /// member with <see cref="BridgeToolRegistry"/>. Idempotent (the registry de-dupes by name).
    ///
    /// <para>
    /// Three tools, all read-only (<c>isMutating:false</c>, group <c>core</c>), so they bypass the
    /// gate dispatch path and surface their JSON output verbatim as the <c>result</c> field:
    /// <list type="bullet">
    ///   <item><c>godot_open_mcp_validate_edit</c> — scoped verify pass over <c>res://</c> paths
    ///   (<see cref="ValidateEditTool"/>).</item>
    ///   <item><c>godot_open_mcp_checkpoint_create</c> — capture a project-health baseline into
    ///   <see cref="CheckpointStore"/> (<see cref="CheckpointCreateTool"/>).</item>
    ///   <item><c>godot_open_mcp_delta</c> — compare current state against a stored checkpoint
    ///   (<see cref="DeltaTool"/>).</item>
    /// </list>
    /// Together they let an agent run the explicit checkpoint → mutate → delta workflow in one
    /// session. Registered on the same enable surface as the node/scene families so a re-enable after
    /// an assembly reload refreshes the handler references.
    /// </para>
    /// </summary>
    internal static class GateTools
    {
        /// <summary>MCP tool name for the scoped verify pass.</summary>
        internal const string ValidateEditToolName = "godot_open_mcp_validate_edit";

        /// <summary>MCP tool name for the checkpoint-capture tool.</summary>
        internal const string CheckpointCreateToolName = "godot_open_mcp_checkpoint_create";

        /// <summary>MCP tool name for the before/after delta tool.</summary>
        internal const string DeltaToolName = "godot_open_mcp_delta";

        /// <summary>
        /// Register the gate meta-tool family. Safe to call again on re-enable — the registry is
        /// idempotent. Registered as <c>core</c> group (the meta-tools are not a domain family like
        /// node/scene/script; they sit in the always-visible core surface alongside ping).
        /// </summary>
        internal static void RegisterGateTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ValidateEditToolName,
                isMutating: false,
                defaultGate: "off",
                group: "core",
                handler: ValidateEditTool.Execute));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: CheckpointCreateToolName,
                isMutating: false,
                defaultGate: "off",
                group: "core",
                handler: CheckpointCreateTool.Execute));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: DeltaToolName,
                isMutating: false,
                defaultGate: "off",
                group: "core",
                handler: DeltaTool.Execute));
        }
    }
}
#endif
