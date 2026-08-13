#nullable enable

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // PhantomCamera pack request bodies.
    //
    // PhantomCamera is a THIRD-PARTY GDScript addon (phantom-camera by ramokz).
    // Its classes (PhantomCamera2D / PhantomCamera3D) register into ClassDB at
    // runtime when the addon is enabled — they are NOT statically available to
    // the C# bridge, so the handlers detect them via ClassDB.ClassExists and
    // operate via duck-typed property Set/Get. These body types carry only the
    // scalars an agent sends; the handlers resolve targets / detect the addon.
    //
    // Pure-managed (no Godot API surface, no `#if TOOLS`), so the parsing logic
    // is unit-testable in the binary-less xUnit host. Extraction primitives live
    // on `JsonScalar` (declared once in TilemapBodies.cs, reused here).
    //
    // follow_mode / look_at_mode are PhantomCamera addon enum ordinals (0..6 /
    // 0..3 respectively). The int values are passed straight through to the
    // addon's properties — the pack documents them but does NOT re-validate the
    // range (the addon is the authority; an out-of-range value surfaces as an
    // addon-side error, wrapped as execution_error by the handler).
    // ===========================================================================

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_phantom_camera_create</c>.
    /// Carries the common node-creation shape (name / parent_node_path /
    /// position) plus the <c>dimension</c> ("2d" / "3d", default "3d") that
    /// selects PhantomCamera2D vs PhantomCamera3D. Position is "x,y,z" for the
    /// 3D variant and "x,y" for the 2D variant.
    /// </summary>
    internal sealed class PhantomCameraCreateBody
    {
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }
        internal string? Dimension { get; private set; }

        /// <summary>Normalized dimension token: "2d" or "3d" (default "3d" for an
        /// absent / unrecognized value). The handler selects the addon class from
        /// this.</summary>
        internal string EffectiveDimension
            => string.Equals(Dimension, "2d", System.StringComparison.OrdinalIgnoreCase) ? "2d" : "3d";

        internal static PhantomCameraCreateBody Parse(string? body)
        {
            var parsed = new PhantomCameraCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            parsed.Dimension = JsonScalar.ExtractString(body, "dimension");
            return parsed;
        }

        PhantomCameraCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for
    /// <c>godot_open_mcp_phantom_camera_set_target</c>. Carries the camera
    /// target (node_path) and the scene node to follow (target_node_path). Sets
    /// the addon's <c>follow_target</c>.
    /// </summary>
    internal sealed class PhantomCameraSetTargetBody
    {
        internal string? NodePath { get; private set; }
        internal string? TargetNodePath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal bool HasTargetNodePath => !string.IsNullOrEmpty(TargetNodePath);

        internal static PhantomCameraSetTargetBody Parse(string? body)
        {
            var parsed = new PhantomCameraSetTargetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.TargetNodePath = JsonScalar.ExtractString(body, "target_node_path");
            return parsed;
        }

        PhantomCameraSetTargetBody() { }
    }

    /// <summary>
    /// Parsed request body for
    /// <c>godot_open_mcp_phantom_camera_set_priority</c>. Carries the camera
    /// target (node_path) and the integer priority (higher wins — raising it
    /// switches the active camera).
    /// </summary>
    internal sealed class PhantomCameraSetPriorityBody
    {
        internal string? NodePath { get; private set; }
        internal int Priority { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static PhantomCameraSetPriorityBody Parse(string? body)
        {
            var parsed = new PhantomCameraSetPriorityBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Priority = JsonScalar.ExtractInt(body, "priority", defaultValue: 0);
            return parsed;
        }

        PhantomCameraSetPriorityBody() { }
    }

    /// <summary>
    /// Parsed request body for
    /// <c>godot_open_mcp_phantom_camera_set_follow</c>. Carries the camera
    /// target (node_path), the follow_mode ordinal (required), and an optional
    /// follow target (target_node_path). <c>follow_mode</c> is the addon's
    /// FollowMode enum ordinal (0 none / 1 glued / 2 simple_follow / 3
    /// group_follow / 4 path_follow / 5 framed / 6 third_person).
    /// </summary>
    internal sealed class PhantomCameraSetFollowBody
    {
        internal string? NodePath { get; private set; }
        internal int FollowMode { get; private set; }
        internal string? TargetNodePath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal bool HasTargetNodePath => !string.IsNullOrEmpty(TargetNodePath);

        internal static PhantomCameraSetFollowBody Parse(string? body)
        {
            var parsed = new PhantomCameraSetFollowBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.FollowMode = JsonScalar.ExtractInt(body, "follow_mode", defaultValue: 0);
            parsed.TargetNodePath = JsonScalar.ExtractString(body, "target_node_path");
            return parsed;
        }

        PhantomCameraSetFollowBody() { }
    }

    /// <summary>
    /// Parsed request body for
    /// <c>godot_open_mcp_phantom_camera_set_look_at</c>. Carries the camera
    /// target (node_path), the required look-at target node
    /// (target_node_path), and an optional look_at_mode ordinal.
    /// <c>look_at_mode</c> is the addon's LookAtMode enum ordinal (0 none / 1
    /// mimic / 2 simple / 3 group); when absent the mode is left unchanged (only
    /// the target is set). Uses <see cref="JsonScalar.ExtractIntOrNull"/> so an
    /// absent key is distinguishable from an explicit 0.
    /// </summary>
    internal sealed class PhantomCameraSetLookAtBody
    {
        internal string? NodePath { get; private set; }
        internal string? TargetNodePath { get; private set; }
        internal int? LookAtMode { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal bool HasTargetNodePath => !string.IsNullOrEmpty(TargetNodePath);

        internal static PhantomCameraSetLookAtBody Parse(string? body)
        {
            var parsed = new PhantomCameraSetLookAtBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.TargetNodePath = JsonScalar.ExtractString(body, "target_node_path");
            parsed.LookAtMode = JsonScalar.ExtractIntOrNull(body, "look_at_mode");
            return parsed;
        }

        PhantomCameraSetLookAtBody() { }
    }

    /// <summary>
    /// Parsed request body for
    /// <c>godot_open_mcp_phantom_camera_get</c> (read-only). Carries only the
    /// camera target (node_path).
    /// </summary>
    internal sealed class PhantomCameraGetBody
    {
        internal string? NodePath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static PhantomCameraGetBody Parse(string? body)
        {
            var parsed = new PhantomCameraGetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            return parsed;
        }

        PhantomCameraGetBody() { }
    }
}
