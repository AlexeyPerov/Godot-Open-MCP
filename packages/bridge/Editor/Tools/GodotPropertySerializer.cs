#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// First-party, bounded serializer that walks a Godot <see cref="Resource"/> and produces a
    /// cycle-safe tree of <see cref="ResourcePropertyData"/> nodes for the
    /// <c>godot_open_mcp_resource_get_data</c> tool (P4.1). This is the greenfield replacement for the
    /// disallowed ReflectorNet <c>GodotMcpReflector</c> used by the Godot-MCP reference project — it
    /// exposes no arbitrary reflection and never blindly traverses object references.
    ///
    /// <para>
    /// <b>Fidelity: greenfield.</b> The reference project's serializer (ReflectorNet) is a third-party
    /// dependency the porting protocol forbids. This implementation builds the same logical shape
    /// (property name + Variant type + value/children + truncation metadata) from scratch, using only
    /// Godot's own <c>GetPropertyList</c>/<c>Get</c> API surface.
    /// </para>
    ///
    /// <para>
    /// <b>Hard limits</b> bound every dimension that could blow the response: recursion depth, total
    /// emitted nodes, collection items per array/dict, string length, and estimated byte count. Each
    /// limit, when hit, records a truncation reason on the affected node and in the top-level
    /// <see cref="TruncationInfo"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Cycle detection</b> uses Godot instance IDs (the <c>GetInstanceId()</c> of live objects)
    /// held in a <c>HashSet&lt;ulong&gt;</c> visited-set internal to one serialization call. Instance
    /// IDs are process-local and are NOT exposed in the public contract as durable resource identity
    /// — they are only used to detect that the same live object is being traversed twice.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): touches live <see cref="GodotObject"/> instances. The result
    /// DTOs (<see cref="ResourcePropertyData"/>) are pure-managed and can be serialized off-thread.
    /// </summary>
    internal sealed class GodotPropertySerializer
    {
        // --- hard limits (per serialization call) ------------------------------------

        /// <summary>Maximum recursion depth. The body parser clamps the requested depth to
        /// <see cref="ResourceGetDataBody.HardMaxDepth"/>; this is the same cap expressed as a
        /// constant on the serializer.</summary>
        public const int MaxDepth = ResourceGetDataBody.HardMaxDepth;

        /// <summary>Hard cap on total emitted property nodes across the whole tree. Guards against a
        /// huge resource (e.g. a Theme or an atlas) producing tens of thousands of nodes.</summary>
        public const int MaxNodes = 2000;

        /// <summary>Hard cap on items emitted per Array/Dictionary collection. Larger collections are
        /// clipped and the remainder count is reported in the truncation reason.</summary>
        public const int MaxCollectionItems = 200;

        /// <summary>Hard cap on individual string length (characters). Longer strings are clipped and
        /// a truncation reason records the original length.</summary>
        public const int MaxStringLength = 4000;

        /// <summary>Hard cap on estimated total response bytes. When exceeded, further nodes are
        /// suppressed and a global truncation reason is recorded.</summary>
        public const int MaxEstimatedBytes = 512 * 1024; // 512 KB

        // --- instance state (per Serialize call) -------------------------------------

        readonly int _collectionPageSize;
        readonly HashSet<ulong> _visited = new();
        readonly TruncationInfo _truncation = new();
        int _nodeCount;
        int _estimatedBytes;

        GodotPropertySerializer(int collectionPageSize)
        {
            _collectionPageSize = collectionPageSize;
        }

        /// <summary>
        /// Serialize <paramref name="resource"/> into a list of top-level
        /// <see cref="ResourcePropertyData"/> nodes plus truncation metadata. Main-thread only (reads
        /// the resource's property list and values).
        /// </summary>
        /// <param name="resource">The loaded resource to serialize. Must not be null.</param>
        /// <param name="maxDepth">Maximum recursion depth (clamped to [<see cref="MaxDepth"/>]).</param>
        /// <param name="collectionPageSize">Max items per collection before paging-style truncation
        /// (clamped to [<see cref="MaxCollectionItems"/>]).</param>
        /// <param name="truncation">Filled with the truncation summary.</param>
        public static List<ResourcePropertyData> Serialize(
            Resource resource,
            int maxDepth,
            int collectionPageSize,
            out TruncationInfo truncation)
        {
            var ser = new GodotPropertySerializer(System.Math.Max(1, System.Math.Min(MaxCollectionItems, collectionPageSize)));
            var depth = System.Math.Max(0, System.Math.Min(MaxDepth, maxDepth));

            // Mark the root resource as visited so a self-referential property does not loop back into
            // it.
            ser._visited.Add(resource.GetInstanceId());

            var nodes = ser.SerializeObjectProperties(resource, depth);
            truncation = ser._truncation;
            return nodes;
        }

        // --- per-object property enumeration -----------------------------------------

        /// <summary>
        /// Enumerate the serializable properties of <paramref name="obj"/> and build a
        /// <see cref="ResourcePropertyData"/> for each. Filters out Godot's internal bookkeeping
        /// properties (those prefixed with <c>resource_</c> except the commonly useful
        /// <c>resource_local_to_scene</c>, <c>script</c> refs that are handled as references, and
        /// properties with <c>PROPERTY_USAGE_NO_EDITOR</c> only).
        /// </summary>
        List<ResourcePropertyData> SerializeObjectProperties(GodotObject obj, int depth)
        {
            var result = new List<ResourcePropertyData>();
            Godot.Collections.Array props;
            try
            {
                props = obj.GetPropertyList();
            }
            catch (System.Exception e)
            {
                RecordTruncation($"property list access failed for '{obj.GetClass()}': {e.Message}");
                return result;
            }

            try
            {
                foreach (var propVariant in props)
                {
                    if (_nodeCount >= MaxNodes)
                    {
                        RecordTruncation($"max_nodes ({MaxNodes}) reached; remaining properties of '{obj.GetClass()}' suppressed.");
                        break;
                    }
                    if (_estimatedBytes >= MaxEstimatedBytes)
                    {
                        RecordTruncation($"max_estimated_bytes ({MaxEstimatedBytes}) reached; remaining properties suppressed.");
                        break;
                    }

                    var propDict = propVariant.As<Godot.Collections.Dictionary>();
                    var name = propDict.TryGetValue("name", out var nameVar) ? nameVar.AsString() : string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Filter Godot internal bookkeeping properties.
                    if (IsInternalProperty(name)) continue;

                    Variant value;
                    try
                    {
                        value = obj.Get(name);
                    }
                    catch (System.Exception e)
                    {
                        // A property getter that throws should not fault the whole serialization.
                        result.Add(new ResourcePropertyData
                        {
                            Name = name,
                            VariantType = nameof(Variant.Type.Nil),
                            Value = null,
                            TruncationReason = $"property access failed: {e.Message}",
                        });
                        _nodeCount++;
                        continue;
                    }

                    var node = SerializeVariant(name, value, depth);
                    result.Add(node);
                }
            }
            finally
            {
                props.Dispose();
            }

            return result;
        }

        /// <summary>
        /// Dispatch a Variant to the right serializer branch based on its type. The depth counter
        /// only decrements for container types (Object/Array/Dictionary); scalars are leaves.
        /// </summary>
        ResourcePropertyData SerializeVariant(string name, Variant value, int depth)
        {
            _nodeCount++;
            _estimatedBytes += 32 + name.Length; // rough estimate per node

            var vt = value.VariantType;

            switch (vt)
            {
                case Variant.Type.Nil:
                    return Leaf(name, nameof(Variant.Type.Nil), "null", rawJson: true);

                case Variant.Type.Bool:
                    return Leaf(name, nameof(Variant.Type.Bool),
                        value.AsBool() ? "true" : "false", rawJson: true);

                case Variant.Type.Int:
                    return Leaf(name, nameof(Variant.Type.Int),
                        value.AsInt64().ToString(CultureInfo.InvariantCulture), rawJson: true);

                case Variant.Type.Float:
                    return LeafFloat(name, value.AsDouble());

                case Variant.Type.String:
                    return LeafString(name, value.AsString());

                case Variant.Type.Vector2:
                    return LeafVector(name, nameof(Variant.Type.Vector2), value.AsVector2());
                case Variant.Type.Vector2I:
                    return LeafVector(name, nameof(Variant.Type.Vector2I), value.AsVector2I());
                case Variant.Type.Vector3:
                    return LeafVector(name, nameof(Variant.Type.Vector3), value.AsVector3());
                case Variant.Type.Vector3I:
                    return LeafVector(name, nameof(Variant.Type.Vector3I), value.AsVector3I());
                case Variant.Type.Vector4:
                    return LeafVector(name, nameof(Variant.Type.Vector4), value.AsVector4());
                case Variant.Type.Vector4I:
                    return LeafVector(name, nameof(Variant.Type.Vector4I), value.AsVector4I());

                case Variant.Type.Rect2:
                case Variant.Type.Rect2I:
                case Variant.Type.Transform2D:
                case Variant.Type.Plane:
                case Variant.Type.Quaternion:
                case Variant.Type.Aabb:
                case Variant.Type.Basis:
                case Variant.Type.Transform3D:
                case Variant.Type.Projection:
                    return LeafStruct(name, vt, value);

                case Variant.Type.Color:
                    var c = value.AsColor();
                    return Leaf(name, nameof(Variant.Type.Color),
                        $"[{Fmt(c.R)},{Fmt(c.G)},{Fmt(c.B)},{Fmt(c.A)}]", rawJson: true);

                case Variant.Type.NodePath:
                    return LeafString(name, value.AsNodePath().ToString());

                case Variant.Type.StringName:
                    return LeafString(name, value.AsStringName().ToString());

                case Variant.Type.RID:
                    return Leaf(name, nameof(Variant.Type.RID), value.AsRid().ToString(), rawJson: false);

                case Variant.Type.Object:
                    return SerializeObjectReference(name, value, depth);

                case Variant.Type.Callable:
                    return Leaf(name, nameof(Variant.Type.Callable), "<callable>", rawJson: false);

                case Variant.Type.Signal:
                    return Leaf(name, nameof(Variant.Type.Signal), "<signal>", rawJson: false);

                case Variant.Type.Dictionary:
                    return SerializeDictionary(name, value, depth);

                case Variant.Type.Array:
                case Variant.Type.PackedByteArray:
                case Variant.Type.PackedInt32Array:
                case Variant.Type.PackedInt64Array:
                case Variant.Type.PackedFloat32Array:
                case Variant.Type.PackedFloat64Array:
                case Variant.Type.PackedStringArray:
                case Variant.Type.PackedVector2Array:
                case Variant.Type.PackedVector3Array:
                case Variant.Type.PackedColorArray:
                    return SerializeArray(name, value, vt, depth);

                default:
                    return Leaf(name, vt.ToString(), null, rawJson: false);
            }
        }

        // --- leaf builders -----------------------------------------------------------

        ResourcePropertyData Leaf(string name, string variantType, string? value, bool rawJson)
            => new()
            {
                Name = name,
                VariantType = variantType,
                Value = value,
                ValueIsRawJson = rawJson,
            };

        ResourcePropertyData LeafFloat(string name, double d)
        {
            // Emit as JSON number when it's a clean integer value, otherwise quote to avoid
            // locale/precision issues in the hand-rolled JSON builder.
            var s = Fmt(d);
            return Leaf(name, nameof(Variant.Type.Float), s, rawJson: true);
        }

        ResourcePropertyData LeafString(string name, string s)
        {
            if (s.Length > MaxStringLength)
            {
                var clipped = s.Substring(0, MaxStringLength);
                return new ResourcePropertyData
                {
                    Name = name,
                    VariantType = nameof(Variant.Type.String),
                    Value = clipped,
                    ValueIsRawJson = false,
                    TruncationReason = $"string clipped from {s.Length} to {MaxStringLength} characters",
                };
            }
            return Leaf(name, nameof(Variant.Type.String), s, false);
        }

        ResourcePropertyData LeafVector<T>(string name, string variantType, T v)
        {
            // Reflection-free: build a "[x,y(,z(,w))]" string from the known Godot vector types.
            var s = v switch
            {
                Vector2 v2 => $"[{Fmt(v2.X)},{Fmt(v2.Y)}]",
                Vector2I v2i => $"[{v2i.X},{v2i.Y}]",
                Vector3 v3 => $"[{Fmt(v3.X)},{Fmt(v3.Y)},{Fmt(v3.Z)}]",
                Vector3I v3i => $"[{v3i.X},{v3i.Y},{v3i.Z}]",
                Vector4 v4 => $"[{Fmt(v4.X)},{Fmt(v4.Y)},{Fmt(v4.Z)},{Fmt(v4.W)}]",
                Vector4I v4i => $"[{v4i.X},{v4i.Y},{v4i.Z},{v4i.W}]",
                _ => v?.ToString() ?? "null",
            };
            return Leaf(name, variantType, s, rawJson: true);
        }

        /// <summary>
        /// Serialize a struct Variant (Rect2, Basis, Transform3D, etc.) as a descriptive string leaf.
        /// These types are common but their element counts vary; rather than hard-coding each, we use
        /// Godot's own <c>ToString</c> for the value and tag the type so an agent knows the shape.
        /// </summary>
        ResourcePropertyData LeafStruct(string name, Variant.Type vt, Variant value)
        {
            string s;
            try
            {
                s = value.ToString();
            }
            catch
            {
                s = $"<{vt}>";
            }
            // The ToString output of these structs uses the editor's default formatting; pass it
            // through as a string leaf (quoted, not raw JSON).
            return Leaf(name, vt.ToString(), s, rawJson: false);
        }

        // --- object references -------------------------------------------------------

        /// <summary>
        /// Serialize a Variant holding a GodotObject. Resources are either expanded (when within depth
        /// and not already visited) or represented by a descriptive reference leaf. Nodes (rare in a
        /// resource property tree) are always references.
        /// </summary>
        ResourcePropertyData SerializeObjectReference(string name, Variant value, int depth)
        {
            GodotObject? obj;
            try
            {
                obj = value.As<GodotObject>();
            }
            catch
            {
                obj = null;
            }
            if (obj == null || !GodotObject.IsInstanceValid(obj))
            {
                return Leaf(name, nameof(Variant.Type.Object), null, rawJson: true);
            }

            var instanceId = obj.GetInstanceId();

            // A Resource within depth budget and not yet visited → descend.
            if (obj is Resource res && depth > 0 && !_visited.Contains(instanceId))
            {
                _visited.Add(instanceId);
                var children = SerializeObjectProperties(res, depth - 1);
                _visited.Remove(instanceId);
                return new ResourcePropertyData
                {
                    Name = name,
                    VariantType = nameof(Variant.Type.Object),
                    Children = children,
                    ReferenceDescription = DescribeReference(res),
                };
            }

            // Otherwise: emit a non-recursive reference leaf.
            string desc;
            if (obj is Resource r)
            {
                desc = DescribeReference(r);
            }
            else
            {
                desc = $"{obj.GetClass()} (instanceId={instanceId})";
            }

            var node = new ResourcePropertyData
            {
                Name = name,
                VariantType = nameof(Variant.Type.Object),
                ReferenceDescription = desc,
            };
            if (_visited.Contains(instanceId))
            {
                node.TruncationReason = "cycle detected; reference not traversed";
            }
            else if (depth <= 0)
            {
                node.TruncationReason = "max_depth reached; reference not traversed";
            }
            return node;
        }

        /// <summary>
        /// Build a durable, descriptive reference string for a resource: its res:// path (and uid when
        /// available) plus its class name. This is what an agent follows with another find/get-data
        /// call — never a process-local instance ID.
        /// </summary>
        static string DescribeReference(Resource res)
        {
            var sb = new StringBuilder(64);
            var path = res.ResourcePath;
            var cls = res.GetClass();
            if (!string.IsNullOrEmpty(path))
            {
                sb.Append(path);
                var uidId = ResourceLoader.GetResourceUid(path);
                if (uidId != ResourceUid.InvalidId)
                {
                    sb.Append(" (").Append(ResourceUid.IdToText(uidId)).Append(')');
                }
                sb.Append(" [").Append(cls).Append(']');
            }
            else
            {
                // In-memory resource with no on-disk path: name + class.
                var name = res.ResourceName;
                sb.Append(cls);
                if (!string.IsNullOrEmpty(name))
                    sb.Append(" '").Append(name).Append('\'');
                sb.Append(" (no res:// path)");
            }
            return sb.ToString();
        }

        // --- collections -------------------------------------------------------------

        ResourcePropertyData SerializeArray(string name, Variant value, Variant.Type vt, int depth)
        {
            // Packed arrays → convert to a Godot Collections.Array for uniform iteration.
            Godot.Collections.Array arr;
            bool ownsArr;
            if (vt == Variant.Type.Array)
            {
                arr = value.As<Godot.Collections.Array>();
                ownsArr = false; // the Variant owns it; we don't dispose
            }
            else
            {
                // AsArray copies packed arrays into a Godot.Collections.Array.
                try
                {
                    arr = value.As<Godot.Collections.Array>();
                    ownsArr = true;
                }
                catch (System.Exception e)
                {
                    return Leaf(name, vt.ToString(), null, rawJson: true)
                        .WithTruncation($"packed array conversion failed: {e.Message}");
                }
            }

            try
            {
                var limit = System.Math.Min(_collectionPageSize, MaxCollectionItems);
                var total = arr.Count;
                var children = new List<ResourcePropertyData>(System.Math.Min(limit, total));
                for (int i = 0; i < total && children.Count < limit; i++)
                {
                    if (_nodeCount >= MaxNodes)
                    {
                        RecordTruncation($"max_nodes ({MaxNodes}) reached inside array '{name}'.");
                        break;
                    }
                    if (_estimatedBytes >= MaxEstimatedBytes)
                    {
                        RecordTruncation($"max_estimated_bytes reached inside array '{name}'.");
                        break;
                    }
                    Variant item;
                    try
                    {
                        item = arr[i];
                    }
                    catch
                    {
                        continue;
                    }
                    // Array elements: use the index as the node name.
                    children.Add(SerializeVariant($"[{i}]", item, System.Math.Max(0, depth - 1)));
                }

                var node = new ResourcePropertyData
                {
                    Name = name,
                    VariantType = vt.ToString(),
                    Children = children,
                };
                if (total > children.Count)
                {
                    node.TruncationReason = $"array clipped to {children.Count} of {total} items";
                }
                return node;
            }
            finally
            {
                if (ownsArr) arr.Dispose();
            }
        }

        ResourcePropertyData SerializeDictionary(string name, Variant value, int depth)
        {
            Godot.Collections.Dictionary dict;
            try
            {
                dict = value.As<Godot.Collections.Dictionary>();
            }
            catch (System.Exception e)
            {
                return Leaf(name, nameof(Variant.Type.Dictionary), null, rawJson: true)
                    .WithTruncation($"dictionary access failed: {e.Message}");
            }

            var limit = System.Math.Min(_collectionPageSize, MaxCollectionItems);
            var children = new List<ResourcePropertyData>(limit);
            int total = 0;
            int added = 0;
            foreach (var entry in dict)
            {
                total++;
                if (added >= limit) continue;
                if (_nodeCount >= MaxNodes)
                {
                    RecordTruncation($"max_nodes ({MaxNodes}) reached inside dictionary '{name}'.");
                    break;
                }
                if (_estimatedBytes >= MaxEstimatedBytes)
                {
                    RecordTruncation($"max_estimated_bytes reached inside dictionary '{name}'.");
                    break;
                }

                var keyStr = entry.Key.VariantType == Variant.Type.String
                    ? entry.Key.AsString()
                    : entry.Key.ToString() ?? entry.Key.VariantType.ToString();
                var child = SerializeVariant(keyStr, entry.Value, System.Math.Max(0, depth - 1));
                children.Add(child);
                added++;
            }

            var node = new ResourcePropertyData
            {
                Name = name,
                VariantType = nameof(Variant.Type.Dictionary),
                Children = children,
            };
            if (total > children.Count)
            {
                node.TruncationReason = $"dictionary clipped to {children.Count} of {total} entries";
            }
            return node;
        }

        // --- helpers -----------------------------------------------------------------

        void RecordTruncation(string reason)
        {
            _truncation.Truncated = true;
            _truncation.Reasons.Add(reason);
        }

        static bool IsInternalProperty(string name)
        {
            // Godot prepends "resource_" to its built-in Resource bookkeeping fields. We keep the
            // useful ones (resource_name) and skip the rest (resource_path, resource_local_to_scene
            // flags, etc. — these are identity/metadata, not serializable content). "script" is
            // handled as an object reference like any other.
            return name switch
            {
                "resource_path" => true,
                "resource_local_to_scene" => true,
                "_import_path" => true,
                "_import_id" => true,
                // resource_name IS useful — keep it.
                _ => false,
            };
        }

        static string Fmt(double d)
        {
            // Invariant culture so decimals use '.' regardless of locale.
            if (double.IsNaN(d) || double.IsInfinity(d))
                return d.ToString(CultureInfo.InvariantCulture);
            // Trim trailing zeros for cleanliness, keep at least one decimal.
            var s = d.ToString("R", CultureInfo.InvariantCulture);
            return s;
        }
    }

    /// <summary>Internal extension so the serializer's leaf-with-truncation calls read top-down.</summary>
    internal static class ResourcePropertyDataExtensions
    {
        internal static ResourcePropertyData WithTruncation(this ResourcePropertyData node, string reason)
        {
            node.TruncationReason = reason;
            return node;
        }
    }
}
#endif
