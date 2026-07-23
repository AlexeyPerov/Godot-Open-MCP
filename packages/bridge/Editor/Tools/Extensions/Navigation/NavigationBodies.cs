#nullable enable
using System;
using System.Globalization;

namespace GodotOpenMcp.Bridge.Editor
{
    // ===========================================================================
    // P12.2 navigation pack request bodies.
    //
    // Each tool that needs structured extraction gets a tiny sealed body type. They
    // mirror the hand-rolled `IndexOf`-substring style already used by the P12.1
    // tilemap bodies (which in turn ported NodeCreateBody / NodeFindBody — see
    // packages/bridge/AGENTS.md §Transport: the bridge deliberately carries no
    // typed JSON DOM dependency on the hot path). Pure-managed (no Godot API
    // surface, no `#if TOOLS`), so the parsing logic is unit-testable in the
    // binary-less xUnit host.
    //
    // The shared extraction primitives live in the P12.1 `JsonScalar` static class
    // (GodotOpenMcp.Bridge.Editor namespace, sibling of this file). P12.2 is the
    // "third family" the P12.1 comment anticipated — it reuses JsonScalar rather
    // than duplicating it a third time, and extends it with the two extractors the
    // navigation pack needs (ExtractFloat / ExtractBool).
    //
    // Dimension handling: every tool that can target/create either a 2D or 3D node
    // accepts a `dimension` string ("2d" | "3d"). The body parser records the raw
    // value; the handler normalizes via <see cref="NavigationDimensionParser"/> —
    // Unknown when absent or not one of the two valid tokens, surfaced as
    // `invalid_parameter` by the handler.
    // ===========================================================================

    /// <summary>
    /// Normalized dimension token extracted from a request body. <see cref="Unknown"/>
    /// covers both "absent" and "not a valid token"; the handler turns that into
    /// <c>invalid_parameter</c> so an agent cannot create a half-resolved node.
    /// </summary>
    internal enum NavDimension
    {
        Unknown = 0,
        TwoD = 2,
        ThreeD = 3,
    }

    /// <summary>
    /// Map a raw <c>dimension</c> string ("2d" | "3d") to a <see cref="NavDimension"/>.
    /// Returns <see cref="NavDimension.Unknown"/> for null / empty / unrecognized tokens so
    /// the handler can surface a single <c>invalid_parameter</c> error. Case-insensitive to
    /// tolerate an agent sending "2D" / "3D".
    /// </summary>
    internal static class NavigationDimensionParser
    {
        internal static NavDimension Parse(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return NavDimension.Unknown;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "2d": return NavDimension.TwoD;
                case "3d": return NavDimension.ThreeD;
                default: return NavDimension.Unknown;
            }
        }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_defaults</c> (P12.2). A pure
    /// helper — the handler returns recommended starter scalars for the requested
    /// dimension. Only <c>dimension</c> is read.
    /// </summary>
    internal sealed class NavigationDefaultsBody
    {
        internal NavDimension Dimension { get; private set; }

        internal static NavigationDefaultsBody Parse(string? body)
        {
            var parsed = new NavigationDefaultsBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = NavigationDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            return parsed;
        }

        NavigationDefaultsBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_region_create</c> (P12.2). Mirrors
    /// <c>node_create</c> for the shape an agent already knows (name / parent_node_path /
    /// position), plus the <c>dimension</c> that selects NavigationRegion2D vs NavigationRegion3D.
    /// </summary>
    internal sealed class NavigationRegionCreateBody
    {
        internal NavDimension Dimension { get; private set; }
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        internal static NavigationRegionCreateBody Parse(string? body)
        {
            var parsed = new NavigationRegionCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = NavigationDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            return parsed;
        }

        NavigationRegionCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_region_set_mesh</c> (P12.2). Carries
    /// the region target (node_path) and the <c>res://</c> path of the navigation resource to
    /// assign (NavigationPolygon in 2D, NavigationMesh in 3D). Dimension is inferred from the
    /// resolved node's class in the handler — not parsed here.
    /// </summary>
    internal sealed class NavigationRegionSetMeshBody
    {
        internal string? NodePath { get; private set; }
        internal string? MeshPath { get; private set; }

        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);
        internal bool HasMeshPath => !string.IsNullOrEmpty(MeshPath);

        internal static NavigationRegionSetMeshBody Parse(string? body)
        {
            var parsed = new NavigationRegionSetMeshBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.MeshPath = JsonScalar.ExtractString(body, "mesh_path");
            return parsed;
        }

        NavigationRegionSetMeshBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_agent_create</c> (P12.2). Same shape
    /// as region create: <c>dimension</c> selects NavigationAgent2D vs NavigationAgent3D, plus
    /// the standard name / parent_node_path / position fields.
    /// </summary>
    internal sealed class NavigationAgentCreateBody
    {
        internal NavDimension Dimension { get; private set; }
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }

        internal static NavigationAgentCreateBody Parse(string? body)
        {
            var parsed = new NavigationAgentCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = NavigationDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            return parsed;
        }

        NavigationAgentCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_agent_configure</c> (P12.2). Carries
    /// the agent target (node_path) plus the clamped scalar properties the handler applies. The
    /// known scalar set is fixed (radius / height / max_speed / path_desired_distance /
    /// target_desired_distance / avoidance_enabled); unknown top-level keys are ignored by the
    /// body parser (the schema declares <c>additionalProperties:false</c>, so a conforming client
    /// never sends any). Each scalar is extracted individually rather than via a free-form map so
    /// the handler can clamp each one to its valid range.
    ///
    /// <para>
    /// Float scalars: <see cref="JsonScalar.ExtractFloat"/> returns null when the key is absent —
    /// the handler treats null as "leave unchanged" and applies only the keys the agent sent. A
    /// present-but-invalid value (non-numeric) also returns null and is silently skipped; the
    /// handler does NOT error on a single bad scalar, matching <c>node_modify</c>'s non-aborting
    /// contract.
    /// </para>
    /// </summary>
    internal sealed class NavigationAgentConfigureBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal float? Radius { get; private set; }
        internal float? Height { get; private set; }
        internal float? MaxSpeed { get; private set; }
        internal float? PathDesiredDistance { get; private set; }
        internal float? TargetDesiredDistance { get; private set; }
        internal bool? AvoidanceEnabled { get; private set; }

        internal static NavigationAgentConfigureBody Parse(string? body)
        {
            var parsed = new NavigationAgentConfigureBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            parsed.Radius = JsonScalar.ExtractFloat(body, "radius");
            parsed.Height = JsonScalar.ExtractFloat(body, "height");
            parsed.MaxSpeed = JsonScalar.ExtractFloat(body, "max_speed");
            parsed.PathDesiredDistance = JsonScalar.ExtractFloat(body, "path_desired_distance");
            parsed.TargetDesiredDistance = JsonScalar.ExtractFloat(body, "target_desired_distance");
            parsed.AvoidanceEnabled = JsonScalar.ExtractBool(body, "avoidance_enabled");
            return parsed;
        }

        NavigationAgentConfigureBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_link_create</c> (P12.2). Carries the
    /// <c>dimension</c> that selects NavigationLink2D vs NavigationLink3D, the standard name /
    /// parent_node_path / position fields, plus the link's <c>start_position</c> /
    /// <c>end_position</c> (parsed as raw strings — the handler coerces to Vector2 / Vector3 based
    /// on dimension) and the <c>bidirectional</c> flag.
    /// </summary>
    internal sealed class NavigationLinkCreateBody
    {
        internal NavDimension Dimension { get; private set; }
        internal string? Name { get; private set; }
        internal string? ParentNodePath { get; private set; }
        internal string? Position { get; private set; }
        internal string? StartPosition { get; private set; }
        internal string? EndPosition { get; private set; }
        internal bool? Bidirectional { get; private set; }

        internal bool HasStartPosition => !string.IsNullOrEmpty(StartPosition);
        internal bool HasEndPosition => !string.IsNullOrEmpty(EndPosition);

        internal static NavigationLinkCreateBody Parse(string? body)
        {
            var parsed = new NavigationLinkCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.Dimension = NavigationDimensionParser.Parse(JsonScalar.ExtractString(body, "dimension"));
            parsed.Name = JsonScalar.ExtractString(body, "name");
            parsed.ParentNodePath = JsonScalar.ExtractString(body, "parent_node_path");
            parsed.Position = JsonScalar.ExtractString(body, "position");
            parsed.StartPosition = JsonScalar.ExtractString(body, "start_position");
            parsed.EndPosition = JsonScalar.ExtractString(body, "end_position");
            parsed.Bidirectional = JsonScalar.ExtractBool(body, "bidirectional");
            return parsed;
        }

        NavigationLinkCreateBody() { }
    }

    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_navigation_get</c> (P12.2). Read-only — carries
    /// only the navigation node target (node_path). Dimension is inferred from the resolved node's
    /// class in the handler.
    /// </summary>
    internal sealed class NavigationGetBody
    {
        internal string? NodePath { get; private set; }
        internal bool HasNodePath => !string.IsNullOrEmpty(NodePath);

        internal static NavigationGetBody Parse(string? body)
        {
            var parsed = new NavigationGetBody();
            if (string.IsNullOrEmpty(body)) return parsed;
            parsed.NodePath = JsonScalar.ExtractString(body, "node_path");
            return parsed;
        }

        NavigationGetBody() { }
    }
}
