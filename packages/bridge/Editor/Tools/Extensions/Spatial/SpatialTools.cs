#if TOOLS
#nullable enable
using System.Globalization;
using System.Text;
using Godot;
using Godot.Collections;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Spatial-query pack (P16.6) — one read-only typed tool,
    /// <c>godot_open_mcp_spatial_query</c>, that probes the edited scene's physics
    /// world (2D / 3D) via Godot's <c>PhysicsDirectSpaceState2D/3D</c>. The sixth
    /// and final Phase 16 typed-editor-breadth family; mirrors the P16.1–P16.5
    /// packs' folder layout (<c>Tools/Extensions/&lt;Family&gt;/</c>), registration
    /// shape, and group assignment.
    ///
    /// <para>
    /// <b>Fidelity:</b> adapt — the query-kind / shape-enum / collision-mask /
    /// bounded-results contract and the hit-result shape are adapted from Unity
    /// Open MCP's <c>Spatial/ManageSpatialQueryTool.cs</c>. The Godot physics
    /// dispatch (<c>PhysicsDirectSpaceState</c> intersect_ray / intersect_shape /
    /// intersect_point) is greenfield. Intentional deltas:
    /// (1) Godot DirectSpaceState instead of Unity Physics/Physics2D;
    /// (2) <c>ray</c> uses Godot-native from→to endpoints (not origin/direction);
    /// (3) <c>exclude</c> takes collider NODE PATHS (resolved to RIDs here) because
    /// Godot RIDs don't round-trip through JSON and node paths are stable +
    /// human-readable — hit results echo <c>node_path</c> so an agent can feed a
    /// prior hit back into <c>exclude</c>;
    /// (4) <c>size</c> uses Godot's full-extent convention (BoxShape3D.Size /
    /// RectangleShape2D.Size), not half-extents;
    /// (5) live-only — queries target the edited scene's viewport physics space;
    /// an inactive / locked space surfaces <c>no_active_space</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Read-only.</b> Creates no scene state, marks nothing unsaved, writes no
    /// files. Gate-free (defaultGate "off", isMutating false) — like screenshots.
    /// The handler runs on the editor main thread (every EditorInterface / physics
    /// API call routes through the dispatcher); it never touches project files.
    /// </para>
    ///
    /// Editor-only (<c>#if TOOLS</c>): every code path touches
    /// <see cref="EditorInterface"/> and live physics node objects. The pure-
    /// managed pieces (<see cref="SpatialQueryBody"/> / <see cref="SpatialShape"/>
    /// / <see cref="SpatialShapeCompat"/> / the *Parser classes) live outside this
    /// guard and are unit-tested.
    /// </summary>
    internal static class SpatialTools
    {
        internal const string SpatialQueryToolName = "godot_open_mcp_spatial_query";

        // Result-hit cap applied even when Godot returns more, so a huge overlap
        // can never blow the agent's context budget. Mirrors the Unity tool's max /
        // max_distance bounding philosophy.
        internal const int HardResultCap = 256;

        /// <summary>
        /// Register the spatial-query tool. Read-only (defaultGate "off",
        /// isMutating false), group <c>spatial</c>. Registered once at plugin
        /// enable; idempotent (the registry replaces on re-register).
        /// </summary>
        internal static void RegisterSpatialTools()
        {
            BridgeToolRegistry.Register(new BridgeToolEntry(
                name: SpatialQueryToolName,
                isMutating: false,
                defaultGate: "off",
                group: "spatial",
                handler: Query));
        }

        // ===========================================================================
        // godot_open_mcp_spatial_query
        // ===========================================================================

        /// <summary>
        /// Handler for <c>godot_open_mcp_spatial_query</c>. Resolves the edited
        /// scene's active physics space (2D or 3D) and dispatches a ray / shape /
        /// point query against it. Hit results carry the collider node path, the
        /// Godot instance id, the physics RID (informational), and the contact
        /// position / normal / shape index; shape / point results are bounded by
        /// <c>max_results</c> and report <c>truncated</c> when more hits existed.
        ///
        /// <para>
        /// Structured failures: <c>invalid_parameter</c> (bad query_type /
        /// dimension / shape↔dimension mismatch / missing from-to-position / bad
        /// geometry), <c>no_edited_scene</c>, <c>no_active_space</c> (no resolvable
        /// physics world — most reliable with a playing scene), <c>execution_error</c>
        /// (the space refused the query, e.g. locked mid-step).
        /// </para>
        /// </summary>
        internal static ToolDispatchResult Query(string body)
        {
            var request = SpatialQueryBody.Parse(body);

            if (request.QueryType == SpatialQueryType.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "spatial_query requires 'query_type' to be one of: \"ray\", \"shape\", \"point\".");
            if (request.Dimension == SpatialDimension.Unknown)
                return ToolDispatchResult.Fail(
                    "invalid_parameter",
                    "spatial_query requires 'dimension' to be one of: \"2d\", \"3d\".");

            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root == null)
                return ToolDispatchResult.Fail(
                    "no_edited_scene",
                    "No scene is currently being edited; open a .tscn before calling spatial_query.");

            // Resolve the active physics space from the edited scene's viewport
            // world. The edited scene is instanced under the editor's viewport, so
            // its World2D/3D carries the queryable broadphase. A null DirectSpaceState
            // means no physics world is active (typical in some edit-mode states);
            // surface no_active_space so the agent knows to start the scene playing.
            var viewport = root.GetViewport();
            if (viewport == null)
                return ToolDispatchResult.Fail("no_active_space",
                    "The edited scene has no viewport; cannot resolve a physics space.");

            uint mask = request.Mask ?? uint.MaxValue;
            var exclude = BuildExcludeRids(root, request.Exclude);

            try
            {
                if (request.Dimension == SpatialDimension.ThreeD)
                {
                    var world = viewport.FindWorld3D();
                    var state = world?.DirectSpaceState;
                    if (state == null)
                        return NoActiveSpace("3d");
                    return request.QueryType switch
                    {
                        SpatialQueryType.Ray => Ray3D(state, request, mask, exclude),
                        SpatialQueryType.Shape => Shape3D(state, request, mask, exclude),
                        SpatialQueryType.Point => Point3D(state, request, mask, exclude),
                        _ => BadQueryType(),
                    };
                }
                else
                {
                    var world = viewport.FindWorld2D();
                    var state = world?.DirectSpaceState;
                    if (state == null)
                        return NoActiveSpace("2d");
                    return request.QueryType switch
                    {
                        SpatialQueryType.Ray => Ray2D(state, request, mask, exclude),
                        SpatialQueryType.Shape => Shape2D(state, request, mask, exclude),
                        SpatialQueryType.Point => Point2D(state, request, mask, exclude),
                        _ => BadQueryType(),
                    };
                }
            }
            catch (System.Exception e)
            {
                // A locked space (query mid physics-step) or any other engine fault
                // surfaces as execution_error rather than crashing the dispatch.
                return ToolDispatchResult.Fail("execution_error",
                    $"Physics query failed: {e.Message}");
            }
        }

        // ===========================================================================
        // ray
        // ===========================================================================

        static ToolDispatchResult Ray3D(PhysicsDirectSpaceState3D state, SpatialQueryBody request, uint mask, Array<Rid> exclude)
        {
            if (!request.HasFrom || !request.HasTo)
                return ToolDispatchResult.Fail("missing_parameter",
                    "spatial_query ray requires 'from' and 'to' (\"x,y,z\") in 3d.");
            if (!TryParseVector3(request.From, out var from))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'from' must be three comma-separated numbers (\"x,y,z\").");
            if (!TryParseVector3(request.To, out var to))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'to' must be three comma-separated numbers (\"x,y,z\").");

            var p = PhysicsRayQueryParameters3D.Create(from, to, mask, exclude);
            p.CollideWithBodies = request.CollideWithBodies;
            p.CollideWithAreas = request.CollideWithAreas;

            var hit = state.IntersectRay(p);
            return RayResult(hit, "3d");
        }

        static ToolDispatchResult Ray2D(PhysicsDirectSpaceState2D state, SpatialQueryBody request, uint mask, Array<Rid> exclude)
        {
            if (!request.HasFrom || !request.HasTo)
                return ToolDispatchResult.Fail("missing_parameter",
                    "spatial_query ray requires 'from' and 'to' (\"x,y\") in 2d.");
            if (!TryParseVector2(request.From, out var from))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'from' must be two comma-separated numbers (\"x,y\").");
            if (!TryParseVector2(request.To, out var to))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'to' must be two comma-separated numbers (\"x,y\").");

            var p = PhysicsRayQueryParameters2D.Create(from, to, mask, exclude);
            p.CollideWithBodies = request.CollideWithBodies;
            p.CollideWithAreas = request.CollideWithAreas;

            var hit = state.IntersectRay(p);
            return RayResult(hit, "2d");
        }

        static ToolDispatchResult RayResult(Dictionary hit, string dimension)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"query_type\":\"ray\",");
            sb.Append("\"dimension\":").Append(BridgeJson.EscapeString(dimension)).Append(',');
            if (hit == null || hit.Count == 0)
            {
                sb.Append("\"hit\":false}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            sb.Append("\"hit\":true,");
            sb.Append("\"result\":");
            AppendHit(sb, hit, dimension);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ===========================================================================
        // shape
        // ===========================================================================

        static ToolDispatchResult Shape3D(PhysicsDirectSpaceState3D state, SpatialQueryBody request, uint mask, Array<Rid> exclude)
        {
            if (request.Shape == SpatialShape.Unknown)
                return ToolDispatchResult.Fail("invalid_parameter",
                    "spatial_query shape requires 'shape' to be one of: \"circle\", \"sphere\", \"rectangle\", \"box\", \"capsule\".");
            if (!SpatialShapeCompat.IsCompatible(request.Shape, SpatialDimension.ThreeD))
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"Shape '{SpatialShapeParser.ToSchemaString(request.Shape)}' is not a 3d shape. Use sphere / box / capsule in 3d.");
            if (!request.HasPosition)
                return ToolDispatchResult.Fail("missing_parameter",
                    "spatial_query shape requires 'position' (\"x,y,z\") in 3d.");
            if (!TryParseVector3(request.Position, out var center))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'position' must be three comma-separated numbers (\"x,y,z\").");

            var shape = BuildShape3D(request);
            if (shape == null)
                return ToolDispatchResult.Fail("invalid_parameter",
                    "The requested 3d shape is missing required geometry (radius / size / height).");

            var basis = ParseRotation3D(request.Rotation);
            var p = new PhysicsShapeQueryParameters3D
            {
                Shape = shape,
                Transform = new Transform3D(basis, center),
                CollisionMask = mask,
                CollideWithBodies = request.CollideWithBodies,
                CollideWithAreas = request.CollideWithAreas,
            };
            p.Exclude = exclude;
            var hits = state.IntersectShape(p);
            return MultiResult(hits, "shape", "3d", SpatialShapeParser.ToSchemaString(request.Shape), request.MaxResults);
        }

        static ToolDispatchResult Shape2D(PhysicsDirectSpaceState2D state, SpatialQueryBody request, uint mask, Array<Rid> exclude)
        {
            if (request.Shape == SpatialShape.Unknown)
                return ToolDispatchResult.Fail("invalid_parameter",
                    "spatial_query shape requires 'shape' to be one of: \"circle\", \"sphere\", \"rectangle\", \"box\", \"capsule\".");
            if (!SpatialShapeCompat.IsCompatible(request.Shape, SpatialDimension.TwoD))
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"Shape '{SpatialShapeParser.ToSchemaString(request.Shape)}' is not a 2d shape. Use circle / rectangle / capsule in 2d.");
            if (!request.HasPosition)
                return ToolDispatchResult.Fail("missing_parameter",
                    "spatial_query shape requires 'position' (\"x,y\") in 2d.");
            if (!TryParseVector2(request.Position, out var center))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'position' must be two comma-separated numbers (\"x,y\").");

            var shape = BuildShape2D(request);
            if (shape == null)
                return ToolDispatchResult.Fail("invalid_parameter",
                    "The requested 2d shape is missing required geometry (radius / size / height).");

            float rot = ParseRotation2D(request.Rotation);
            var p = new PhysicsShapeQueryParameters2D
            {
                Shape = shape,
                Transform = new Transform2D(rot, center),
                CollisionMask = mask,
                CollideWithBodies = request.CollideWithBodies,
                CollideWithAreas = request.CollideWithAreas,
            };
            p.Exclude = exclude;
            var hits = state.IntersectShape(p);
            return MultiResult(hits, "shape", "2d", SpatialShapeParser.ToSchemaString(request.Shape), request.MaxResults);
        }

        // ===========================================================================
        // point
        // ===========================================================================

        static ToolDispatchResult Point3D(PhysicsDirectSpaceState3D state, SpatialQueryBody request, uint mask, Array<Rid> exclude)
        {
            if (!request.HasPosition)
                return ToolDispatchResult.Fail("missing_parameter",
                    "spatial_query point requires 'position' (\"x,y,z\") in 3d.");
            if (!TryParseVector3(request.Position, out var pos))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'position' must be three comma-separated numbers (\"x,y,z\").");

            var p = PhysicsPointQueryParameters3D.Create(pos, mask, request.CollideWithBodies, request.CollideWithAreas);
            p.Exclude = exclude;
            var hits = state.IntersectPoint(p);
            return MultiResult(hits, "point", "3d", null, request.MaxResults);
        }

        static ToolDispatchResult Point2D(PhysicsDirectSpaceState2D state, SpatialQueryBody request, uint mask, Array<Rid> exclude)
        {
            if (!request.HasPosition)
                return ToolDispatchResult.Fail("missing_parameter",
                    "spatial_query point requires 'position' (\"x,y\") in 2d.");
            if (!TryParseVector2(request.Position, out var pos))
                return ToolDispatchResult.Fail("invalid_parameter",
                    "'position' must be two comma-separated numbers (\"x,y\").");

            var p = PhysicsPointQueryParameters2D.Create(pos, mask, request.CollideWithBodies, request.CollideWithAreas);
            p.Exclude = exclude;
            var hits = state.IntersectPoint(p);
            return MultiResult(hits, "point", "2d", null, request.MaxResults);
        }

        // ===========================================================================
        // result builders
        // ===========================================================================

        /// <summary>
        /// Render a shape / point multi-hit result. The hit array is bounded to
        /// <paramref name="maxResults"/> (clamped to <see cref="HardResultCap"/>);
        /// <c>truncated</c> reports whether more hits existed than were returned.
        /// </summary>
        static ToolDispatchResult MultiResult(System.Collections.IEnumerable hits, string queryType, string dimension, string? shapeToken, int maxResults)
        {
            int cap = System.Math.Max(1, System.Math.Min(maxResults, HardResultCap));
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"query_type\":").Append(BridgeJson.EscapeString(queryType)).Append(',');
            sb.Append("\"dimension\":").Append(BridgeJson.EscapeString(dimension)).Append(',');
            if (shapeToken != null)
                sb.Append("\"shape\":").Append(BridgeJson.EscapeString(shapeToken)).Append(',');
            sb.Append("\"max_results\":").Append(cap).Append(',');

            int shown = 0;
            bool truncated = false;
            sb.Append("\"results\":[");
            bool first = true;
            foreach (var entry in hits)
            {
                if (entry is Dictionary hit)
                {
                    if (shown >= cap) { truncated = true; break; }
                    if (!first) sb.Append(',');
                    first = false;
                    AppendHit(sb, hit, dimension);
                    shown++;
                }
            }
            sb.Append("],");
            sb.Append("\"count\":").Append(shown).Append(',');
            sb.Append("\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        /// <summary>
        /// Append one hit dictionary as a JSON object. Keys are read defensively —
        /// a ray hit always carries position/normal/shape; a point hit may omit
        /// them, so each field is emitted only when present.
        /// </summary>
        static void AppendHit(StringBuilder sb, Dictionary hit, string dimension)
        {
            sb.Append('{');
            bool first = true;

            // node_path: the collider is the CollisionObject node (Area2D/3D,
            // RigidBody, StaticBody, CharacterBody, AnimatableBody). Cast to Node
            // and read its scene path so an agent can feed it straight back into
            // exclude. A freed / non-node collider emits null.
            string? nodePath = null;
            if (hit.ContainsKey("collider"))
            {
                var obj = hit["collider"].AsGodotObject();
                if (obj is Node n && GodotObject.IsInstanceValid(n))
                    nodePath = n.GetPath().ToString();
            }
            first = AppendStringIfHas(sb, "node_path", nodePath, first);

            // collider_id: the Godot instance id (a stable-per-session integer).
            // Emitted as a JSON number; absent → null.
            if (hit.ContainsKey("collider_id"))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\"collider_id\":").Append(hit["collider_id"].AsInt64());
            }
            else
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\"collider_id\":null");
            }

            // rid: the physics RID (informational). Godot RIDs do not round-trip
            // through JSON cleanly; rendered via GD.VarToStr and quoted.
            if (!first) sb.Append(',');
            first = false;
            sb.Append("\"rid\":").Append(BridgeJson.EscapeString(hit.ContainsKey("rid") ? GD.VarToStr(hit["rid"]) : ""));

            if (hit.ContainsKey("position"))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("\"position\":").Append(dimension == "3d"
                    ? Vec3Json(hit["position"].AsVector3())
                    : Vec2Json(hit["position"].AsVector2()));
            }
            if (hit.ContainsKey("normal"))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("\"normal\":").Append(dimension == "3d"
                    ? Vec3Json(hit["normal"].AsVector3())
                    : Vec2Json(hit["normal"].AsVector2()));
            }
            if (hit.ContainsKey("shape"))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("\"shape\":").Append(hit["shape"].AsInt32());
            }
            if (hit.ContainsKey("face_index"))
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("\"face_index\":").Append(hit["face_index"].AsInt32());
            }
            sb.Append('}');
        }

        static bool AppendStringIfHas(StringBuilder sb, string key, string? value, bool first)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(BridgeJson.EscapeString(value));
            return false;
        }

        // ===========================================================================
        // shape construction
        // ===========================================================================

        /// <summary>
        /// Build a 3D <see cref="Shape3D"/> from the request geometry. Returns null
        /// when a required dimension is missing / non-positive — the caller surfaces
        /// invalid_parameter. <c>size</c> is Godot's full extent (BoxShape3D.Size).
        /// </summary>
        static Shape3D? BuildShape3D(SpatialQueryBody request)
        {
            switch (request.Shape)
            {
                case SpatialShape.Sphere:
                {
                    if (!request.Radius.HasValue || request.Radius.Value <= 0f) return null;
                    return new SphereShape3D { Radius = request.Radius.Value };
                }
                case SpatialShape.Box:
                {
                    if (!TryParseVector3(request.Size, out var size)) return null;
                    if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return null;
                    return new BoxShape3D { Size = size };
                }
                case SpatialShape.Capsule:
                {
                    if (!request.Radius.HasValue || request.Radius.Value <= 0f) return null;
                    if (!request.Height.HasValue || request.Height.Value <= 0f) return null;
                    return new CapsuleShape3D { Radius = request.Radius.Value, Height = request.Height.Value };
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// Build a 2D <see cref="Shape2D"/> from the request geometry. Returns null
        /// when a required dimension is missing / non-positive. <c>size</c> is
        /// Godot's full extent (RectangleShape2D.Size).
        /// </summary>
        static Shape2D? BuildShape2D(SpatialQueryBody request)
        {
            switch (request.Shape)
            {
                case SpatialShape.Circle:
                {
                    if (!request.Radius.HasValue || request.Radius.Value <= 0f) return null;
                    return new CircleShape2D { Radius = request.Radius.Value };
                }
                case SpatialShape.Rectangle:
                {
                    if (!TryParseVector2(request.Size, out var size)) return null;
                    if (size.X <= 0f || size.Y <= 0f) return null;
                    return new RectangleShape2D { Size = size };
                }
                case SpatialShape.Capsule:
                {
                    if (!request.Radius.HasValue || request.Radius.Value <= 0f) return null;
                    if (!request.Height.HasValue || request.Height.Value <= 0f) return null;
                    return new CapsuleShape2D { Radius = request.Radius.Value, Height = request.Height.Value };
                }
                default:
                    return null;
            }
        }

        // ===========================================================================
        // exclude → RID resolution
        // ===========================================================================

        /// <summary>
        /// Resolve each exclude node path to its physics RID. Colliders in the
        /// edited scene are <c>CollisionObject2D/3D</c> nodes; their <c>GetRid()</c>
        /// yields the physics-server RID the query exclude consumes. Unknown paths
        /// and non-collider nodes are silently skipped — exclude is advisory, never
        /// worth failing a query over. Resolves against the edited scene root first,
        /// then falls back to the SceneTree root so a path rooted at "/root/..."
        /// also works.
        /// </summary>
        static Array<Rid> BuildExcludeRids(Node root, System.Collections.Generic.List<string> excludePaths)
        {
            var rids = new Array<Rid>();
            if (excludePaths == null || excludePaths.Count == 0) return rids;
            var treeRoot = root.GetTree()?.Root;
            foreach (var path in excludePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                Node? node = NodeTools.ResolvePath(root, path);
                if (node == null && treeRoot != null)
                    node = NodeTools.ResolvePath(treeRoot, path);
                if (node == null) continue;
                switch (node)
                {
                    case CollisionObject3D co3: rids.Add(co3.GetRid()); break;
                    case CollisionObject2D co2: rids.Add(co2.GetRid()); break;
                }
            }
            return rids;
        }

        // ===========================================================================
        // parsing + formatting helpers
        // ===========================================================================

        static ToolDispatchResult NoActiveSpace(string dimension)
            => ToolDispatchResult.Fail("no_active_space",
                $"No active {dimension} physics world is available. spatial_query needs a running physics space — " +
                "play the scene (most reliable) or open an edited scene whose viewport carries a physics world.");

        static ToolDispatchResult BadQueryType()
            => ToolDispatchResult.Fail("invalid_parameter",
                "Unknown 'query_type'. Expected one of: ray, shape, point.");

        static Basis ParseRotation3D(string? rotation)
        {
            if (string.IsNullOrWhiteSpace(rotation)) return Basis.Identity;
            if (!TryParseVector3(rotation, out var eulerDeg)) return Basis.Identity;
            return new Basis().FromEuler(new Vector3(
                Mathf.DegToRad(eulerDeg.X), Mathf.DegToRad(eulerDeg.Y), Mathf.DegToRad(eulerDeg.Z)));
        }

        static float ParseRotation2D(string? rotation)
        {
            if (string.IsNullOrWhiteSpace(rotation)) return 0f;
            if (float.TryParse(rotation.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var deg))
                return Mathf.DegToRad(deg);
            return 0f;
        }

        static bool TryParseVector2(string? text, out Vector2 v)
        {
            v = Vector2.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 2) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }

        static bool TryParseVector3(string? text, out Vector3 v)
        {
            v = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split(',');
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        static string Vec2Json(Vector2 v)
        {
            var sb = new StringBuilder(32);
            sb.Append('[').Append(Float(v.X)).Append(',').Append(Float(v.Y)).Append(']');
            return sb.ToString();
        }

        static string Vec3Json(Vector3 v)
        {
            var sb = new StringBuilder(48);
            sb.Append('[').Append(Float(v.X)).Append(',').Append(Float(v.Y)).Append(',').Append(Float(v.Z)).Append(']');
            return sb.ToString();
        }

        static string Float(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
#endif
