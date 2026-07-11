#if TOOLS
#nullable enable
using System;
using System.Globalization;
using Godot;
using GodotOpenMcp.Bridge.Runtime.Screenshot;
using SMath = GodotOpenMcp.Bridge.Runtime.Screenshot.ScreenshotMath;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Screenshot tool family (P4.8) — the Godot analog of Unity Open MCP's screenshot /
    /// screenshot-camera agent-senses family and the Godot-MCP reference's
    /// <c>Tool_Screenshot.{Viewport,Camera,Isolated}</c>. Three read-only tools:
    /// <list type="bullet">
    /// <item><description><c>godot_open_mcp_screenshot_viewport</c> — capture the active editor
    /// 2D/3D viewport.</description></item>
    /// <item><description><c>godot_open_mcp_screenshot_camera</c> — off-screen capture from a
    /// <c>Camera2D</c>/<c>Camera3D</c> in the edited scene.</description></item>
    /// <item><description><c>godot_open_mcp_screenshot_isolated</c> — render a <c>Node3D</c> in an
    /// isolated world from one of six orthographic directions, with configurable background.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// All three are read-only (gate <c>off</c>, group <c>editor</c>): they create transient
    /// editor render nodes (an off-screen <c>SubViewport</c> + clone camera/light) but free every
    /// temporary node in <c>finally</c> on success, error, and timeout, and they write no project
    /// files. The encoded PNG is returned in the bridge image envelope (<c>mediaType</c> +
    /// base64 <c>data</c> + metadata); the MCP server's <c>live-client.ts</c> converts that into
    /// an MCP image content block so the base64 never appears inside a text JSON block on success.
    /// </para>
    ///
    /// <para>
    /// <b>Fidelity: adapt.</b> The capture behavior follows the Godot-MCP reference (Godot viewport
    /// / SubViewport / World3D APIs); the image envelope follows Unity Open MCP's
    /// <c>CaptureInlineTool</c> (media-type-tagged base64). The McpPlugin <c>ResponseCallTool.Image
    /// </c> return path from the Godot-MCP reference is NOT used — this is the Godot Open MCP
    /// bridge envelope.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): the handlers touch <c>EditorInterface</c>,
    /// <c>SceneTree</c>, and the rendering APIs. The pure-managed pieces
    /// (<c>ScreenshotViewportBody</c>, <c>ScreenshotCameraBody</c>, <c>ScreenshotIsolatedBody</c>,
    /// <c>ScreenshotResult</c>, <see cref="ScreenshotMath"/>) live outside the guard and are
    /// unit-tested in the binary-less host.
    /// </summary>
    internal static class ScreenshotTools
    {
        internal const string ScreenshotViewportToolName = "godot_open_mcp_screenshot_viewport";
        internal const string ScreenshotCameraToolName = "godot_open_mcp_screenshot_camera";
        internal const string ScreenshotIsolatedToolName = "godot_open_mcp_screenshot_isolated";

        /// <summary>
        /// Register the screenshot tool family (P4.8). All three tools are read-only / gate-free
        /// (group <c>editor</c>, default gate <c>off</c>) — they create transient render nodes but
        /// free them on every path and write no project files. Registered once at plugin enable;
        /// safe to call again on re-enable (the registry is idempotent).
        /// </summary>
        internal static void RegisterScreenshotTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ScreenshotViewportToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: ScreenshotViewport));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ScreenshotCameraToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: ScreenshotCamera));
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: ScreenshotIsolatedToolName,
                isMutating: false,
                defaultGate: "off",
                group: "editor",
                handler: ScreenshotIsolated));
        }

        // --- godot_open_mcp_screenshot_viewport -----------------------------------

        static ToolDispatchResult ScreenshotViewport(string body)
        {
            var parsed = ScreenshotViewportBody.Parse(body);
            var mode = parsed.HasMode ? parsed.Mode.Trim().ToLowerInvariant() : "3d";
            if (mode != "2d" && mode != "3d")
                return ToolDispatchResult.Fail("invalid_capture_mode",
                    $"Unknown viewport mode '{parsed.Mode}'. Use '2d' or '3d'.");

            var result = ScreenshotService.CaptureViewport(mode);
            if (!result.Success)
                return ToolDispatchResult.Fail(result.Code!, result.Error!);

            var b64 = Convert.ToBase64String(result.Png!);
            var caption = $"{mode.ToUpperInvariant()} editor viewport ({result.Width}x{result.Height})";
            var json = ScreenshotResult.BuildImageSuccess(
                b64, result.Width, result.Height, result.Png!.Length,
                "viewport", caption, result.Clamped, source: mode);
            return ToolDispatchResult.Ok(json);
        }

        // --- godot_open_mcp_screenshot_camera -------------------------------------

        static ToolDispatchResult ScreenshotCamera(string body)
        {
            var parsed = ScreenshotCameraBody.Parse(body);
            if (parsed.Camera.IsEmpty)
                return ToolDispatchResult.Fail("node_not_found",
                    "A camera node_ref with node_path or instance_id is required.");

            var camera = ResolveNode(parsed.Camera);
            if (camera == null)
                return ToolDispatchResult.Fail("node_not_found",
                    $"Could not resolve a camera node from {DescribeRef(parsed.Camera)}.");
            if (!GodotObject.IsInstanceValid(camera))
                return ToolDispatchResult.Fail("node_not_found",
                    $"Resolved camera node is freed/invalid ({DescribeRef(parsed.Camera)}).");

            var source = string.IsNullOrEmpty(camera.Name) ? "<camera>" : camera.Name.ToString();
            var result = ScreenshotService.CaptureCamera(camera, parsed.Width, parsed.Height);
            if (!result.Success)
                return ToolDispatchResult.Fail(result.Code!, result.Error!);

            var b64 = Convert.ToBase64String(result.Png!);
            var caption = $"Camera '{source}' ({result.Width}x{result.Height})";
            var json = ScreenshotResult.BuildImageSuccess(
                b64, result.Width, result.Height, result.Png!.Length,
                "camera", caption, result.Clamped,
                source: camera.HasNode("Camera3D") || camera is Camera3D ? $"{source} (Camera3D)" : $"{source} (Camera2D)");
            return ToolDispatchResult.Ok(json);
        }

        // --- godot_open_mcp_screenshot_isolated -----------------------------------

        static ToolDispatchResult ScreenshotIsolated(string body)
        {
            var parsed = ScreenshotIsolatedBody.Parse(body);
            if (parsed.Target.IsEmpty)
                return ToolDispatchResult.Fail("node_not_found",
                    "A target node_ref with node_path or instance_id is required.");

            // Validate the view/background strings before touching the tree.
            if (!SMath.TryParseView(parsed.CameraView, out var view))
                return ToolDispatchResult.Fail("invalid_capture_mode",
                    $"Unknown camera_view '{parsed.CameraView}'. Use one of: front, back, left, right, top, bottom.");
            var bg = parsed.Background.Trim().ToLowerInvariant();
            if (bg != "solid_color" && bg != "transparent")
                return ToolDispatchResult.Fail("invalid_capture_mode",
                    $"Unknown background '{parsed.Background}'. Use 'solid_color' or 'transparent'.");

            var target = ResolveNode(parsed.Target);
            if (target == null)
                return ToolDispatchResult.Fail("node_not_found",
                    $"Could not resolve the target node from {DescribeRef(parsed.Target)}.");
            if (!GodotObject.IsInstanceValid(target))
                return ToolDispatchResult.Fail("node_not_found",
                    $"Resolved target node is freed/invalid ({DescribeRef(parsed.Target)}).");
            if (target is not Node3D target3D)
                return ToolDispatchResult.Fail("invalid_isolated_node",
                    $"Target must be a Node3D; got {target.GetClass()}.");

            var result = ScreenshotService.CaptureIsolated(
                target3D, view, bg, parsed.BackgroundColor,
                parsed.FieldOfView, parsed.NearClipPlane, parsed.FarClipPlane,
                parsed.Padding, parsed.Resolution,
                out var boundsMetadata);
            if (!result.Success)
                return ToolDispatchResult.Fail(result.Code!, result.Error!);

            var b64 = Convert.ToBase64String(result.Png!);
            var sourceName = string.IsNullOrEmpty(target.Name) ? "<node>" : target.Name.ToString();
            var caption = $"Isolated '{sourceName}' from {view} view ({result.Width}x{result.Height})";
            var json = ScreenshotResult.BuildImageSuccess(
                b64, result.Width, result.Height, result.Png!.Length,
                "isolated", caption, result.Clamped,
                source: $"{sourceName} ({view})",
                extraMetadata: sb =>
                {
                    if (!string.IsNullOrEmpty(boundsMetadata))
                        sb.Append(boundsMetadata);
                });
            return ToolDispatchResult.Ok(json);
        }

        // --- node resolution (shared with P4.6 selection semantics) ----------------

        /// <summary>
        /// Resolve a node ref against the currently-edited scene root. instance_id (priority 1) →
        /// node_path (priority 2). Returns null when unresolved. Mirrors the resolution precedence
        /// in <c>EditorSelectionTools</c>.
        /// </summary>
        static Node? ResolveNode(ScreenshotCameraBody.NodeRef r)
        {
            var edited = EditorInterface.Singleton.GetEditedSceneRoot();
            if (edited == null) return null;

            if (r.HasInstanceId)
            {
                try
                {
                    var node = GodotObject.InstanceFromId(r.InstanceId);
                    return node as Node;
                }
                catch { return null; }
            }
            if (r.HasNodePath)
            {
                try { return edited.GetNodeOrNull(new NodePath(r.NodePath)); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>Resolve the isolated target ref (same precedence, Node3D-relevant).</summary>
        static Node? ResolveNode(ScreenshotIsolatedBody.NodeRef r)
        {
            var edited = EditorInterface.Singleton.GetEditedSceneRoot();
            if (edited == null) return null;

            if (r.HasInstanceId)
            {
                try
                {
                    var node = GodotObject.InstanceFromId(r.InstanceId);
                    return node as Node;
                }
                catch { return null; }
            }
            if (r.HasNodePath)
            {
                try { return edited.GetNodeOrNull(new NodePath(r.NodePath)); }
                catch { return null; }
            }
            return null;
        }

        static string DescribeRef(ScreenshotCameraBody.NodeRef r) =>
            r.HasInstanceId ? $"instance_id={r.InstanceId}" :
            r.HasNodePath ? $"node_path='{r.NodePath}'" : "(empty ref)";

        static string DescribeRef(ScreenshotIsolatedBody.NodeRef r) =>
            r.HasInstanceId ? $"instance_id={r.InstanceId}" :
            r.HasNodePath ? $"node_path='{r.NodePath}'" : "(empty ref)";
    }
}
#endif
