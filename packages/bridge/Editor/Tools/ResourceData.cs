#nullable enable
using System.Collections.Generic;
using System.Text;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Lightweight identity record for a Godot resource on disk — returned by
    /// <c>godot_open_mcp_resource_find</c> and as the identity header of
    /// <c>godot_open_mcp_resource_get_data</c>. Carries the resource's <c>res://</c> path, its
    /// <c>uid://</c> (when assigned), and the Godot type the importer recorded for it. The Godot
    /// analog of Unity Open MCP's per-asset hit (path + GUID + type) — the Unity GUID/local ID pair
    /// becomes the canonical Godot path plus an optional UID.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so it is unit-testable in the
    /// binary-less xUnit host. The on-editor handler (<see cref="ResourceTools"/>) populates it; this
    /// type only holds data and knows how to serialize itself via <see cref="BridgeJson"/>.
    /// </para>
    /// </summary>
    internal sealed class ResourceIdentity
    {
        /// <summary><c>res://</c> path of the resource, e.g. <c>res://materials/wood.tres</c>.</summary>
        public string ResourcePath { get; set; } = string.Empty;

        /// <summary><c>uid://</c> identifier (e.g. <c>uid://abc123</c>), or null when the resource has
        /// no UID.</summary>
        public string? Uid { get; set; } = null;

        /// <summary>Godot type recorded for the resource by the import pipeline (e.g.
        /// <c>StandardMaterial3D</c>, <c>Resource</c>), or null when unknown.</summary>
        public string? Type { get; set; } = null;

        /// <summary>Append this identity as a JSON object. Field order is fixed (resourcePath, uid,
        /// type) so diffing clients don't flap on reordering.</summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"resourcePath\":").Append(BridgeJson.EscapeString(ResourcePath)).Append(',');
            sb.Append("\"uid\":").Append(BridgeJson.EscapeString(Uid)).Append(',');
            sb.Append("\"type\":").Append(BridgeJson.EscapeString(Type));
            sb.Append('}');
        }

        public string ToJsonString()
        {
            var sb = new StringBuilder(96);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }

    /// <summary>
    /// One property of a Godot resource as returned by <c>godot_open_mcp_resource_get_data</c>. The
    /// Godot analog of Unity Open MCP's serialized property node, adapted to Godot's Variant type
    /// system. Each node carries a name, the Godot <c>Variant.VariantType</c> label, and either a leaf
    /// <see cref="Value"/> (scalars, vectors, colors) or a list of <see cref="Children"/> for nested
    /// resources, arrays, and dictionaries. Object references that would create a cycle or an
    /// unbounded graph are represented by a descriptive <see cref="ReferenceDescription"/> leaf and
    /// never blindly traversed.
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>): the on-editor
    /// <see cref="GodotPropertySerializer"/> walks the live <c>Resource</c> and builds these nodes on
    /// the main thread, then the result is serialized off-thread. This separation lets the DTO's
    /// <see cref="AppendJsonTo"/> be unit-tested without a live Godot object.
    /// </para>
    /// </summary>
    internal sealed class ResourcePropertyData
    {
        /// <summary>Property name as Godot's <c>GetPropertyList</c> reports it.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Godot Variant type label (e.g. <c>Nil</c>, <c>Int</c>, <c>Vector3</c>,
        /// <c>Color</c>, <c>Object</c>, <c>Array</c>, <c>Dictionary</code>). The serializer fills this
        /// from <c>Variant.VariantType</c>.</summary>
        public string VariantType { get; set; } = string.Empty;

        /// <summary>Leaf value for scalar properties. JSON-ready: scalars are emitted as-is, vectors
        /// and colors as arrays of numbers, strings are JSON-escaped by <see cref="AppendJsonTo"/>.
        /// Null when the node is not a leaf (it has <see cref="Children"/>) or when the value was
        /// truncated/suppressed.</summary>
        public string? Value { get; set; }

        /// <summary>True when <see cref="Value"/> should be emitted as a raw JSON token (number,
        /// boolean, array) rather than a quoted string. The serializer sets this for numeric/bool
        /// leaves; defaults to false (string).</summary>
        public bool ValueIsRawJson { get; set; }

        /// <summary>Children for nested resources, arrays, and dictionaries. Null for leaf
        /// properties.</summary>
        public List<ResourcePropertyData>? Children { get; set; }

        /// <summary>When non-null, this node is an object reference that was NOT traversed (to avoid
        /// cycles or an unbounded graph). Carries a durable identity (res:// path + uid when
        /// available) so an agent can follow it with another call instead of the serializer
        /// descending into it.</summary>
        public string? ReferenceDescription { get; set; }

        /// <summary>Truncation reason when this node's value or children were clipped (e.g.
        /// <c>max_string_length</c>, <c>max_collection_items</c>). Null when nothing was truncated at
        /// this node.</summary>
        public string? TruncationReason { get; set; }

        /// <summary>Append this property node as a JSON object. Field order is fixed (name,
        /// variantType, value, children, referenceDescription, truncationReason) for stable diffs.</summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(BridgeJson.EscapeString(Name)).Append(',');
            sb.Append("\"variantType\":").Append(BridgeJson.EscapeString(VariantType)).Append(',');

            // value: null, raw JSON token, or quoted string.
            sb.Append("\"value\":");
            if (Value == null)
            {
                sb.Append("null");
            }
            else if (ValueIsRawJson)
            {
                sb.Append(Value); // already a number/bool/array literal
            }
            else
            {
                sb.Append(BridgeJson.EscapeString(Value));
            }
            sb.Append(',');

            // children: null or array.
            sb.Append("\"children\":");
            if (Children == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('[');
                for (int i = 0; i < Children.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Children[i].AppendJsonTo(sb);
                }
                sb.Append(']');
            }
            sb.Append(',');

            sb.Append("\"referenceDescription\":").Append(BridgeJson.EscapeString(ReferenceDescription)).Append(',');
            sb.Append("\"truncationReason\":").Append(BridgeJson.EscapeString(TruncationReason));
            sb.Append('}');
        }

        public string ToJsonString()
        {
            var sb = new StringBuilder(128);
            AppendJsonTo(sb);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Truncation metadata for a <c>resource_get_data</c> result. Reports what was clipped by the
    /// serializer's hard limits so an agent knows the response is a bounded projection, not the full
    /// property graph.
    /// </summary>
    internal sealed class TruncationInfo
    {
        /// <summary>True when any node, collection, or string was clipped.</summary>
        public bool Truncated { get; set; }

        /// <summary>Human-readable reasons collected during serialization (e.g.
        /// <c>"max_depth reached at 'albedo_texture'"</c>, <c>"array truncated to 50 of 1200 items"</c>).
        /// Empty when <see cref="Truncated"/> is false.</summary>
        public List<string> Reasons { get; } = new();

        /// <summary>Append as a JSON object: <c>{"truncated":bool,"truncationReasons":[...]}</c>.</summary>
        internal void AppendJsonTo(StringBuilder sb)
        {
            sb.Append('{');
            sb.Append("\"truncated\":").Append(Truncated ? "true" : "false").Append(',');
            sb.Append("\"truncationReasons\":[");
            for (int i = 0; i < Reasons.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(Reasons[i]));
            }
            sb.Append(']');
            sb.Append('}');
        }
    }
}
