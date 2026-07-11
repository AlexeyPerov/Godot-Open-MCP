#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// The resolved, validated form of one <see cref="ResourcePropertyPatch"/> (P4.2). A
    /// <see cref="ValidatedPatch"/> carries everything needed to apply the assignment in request
    /// order, detect no-ops, and roll back on failure. Built by <see cref="ResourcePropertyPatcher.ValidatePatches"/>
    /// — which is all-or-nothing, so a non-empty error list means <em>no</em> patches were validated
    /// and <em>no</em> mutation should run.
    /// </summary>
    internal sealed class ValidatedPatch
    {
        /// <summary>The original parsed patch (raw path/value + segments).</summary>
        public ResourcePropertyPatch Source;

        /// <summary>The <see cref="GodotObject"/> that owns the property/index/key being set. For a
        /// top-level property this is the root resource; for a nested path it is the parent object
        /// at the end of the traversal (a sub-resource, array, or dictionary).</summary>
        public GodotObject? Target;

        /// <summary>The final segment — describes what to set on <see cref="Target"/> (a property
        /// name, array index, or dictionary key).</summary>
        public PatchPathSegment FinalSegment;

        /// <summary>The Godot Variant type the property expects (e.g. <c>Color</c>,
        /// <c>Vector3</c>, <c>Int32</c>). Drives the value conversion.</summary>
        public Variant.Type ExpectedType;

        /// <summary>The class hint for object-typed properties (e.g. <c>StandardMaterial3D</c>),
        /// used by the resource-reference converter. Null for non-object types.</summary>
        public string? ExpectedClassName;

        /// <summary>The converted Variant value ready to assign. Set during validation.</summary>
        public Variant ConvertedValue;

        /// <summary>The property's value before mutation, captured so <see cref="ResourcePropertyPatcher.HasEffectiveChanges"/>
        /// can detect no-ops and the handler can report <c>no_changes</c>.</summary>
        public Variant OriginalValue;

        public ValidatedPatch(ResourcePropertyPatch source)
        {
            Source = source;
        }
    }

    /// <summary>
    /// The greenfield first-party patch engine for <c>resource_create</c> /
    /// <c>resource_modify</c> (P4.2). Replaces the disallowed ReflectorNet patch model from the
    /// Godot-MCP reference. Three phases, all main-thread:
    /// <list type="number">
    /// <item><description><see cref="ValidatePatches"/> — resolve every path against the live
    /// resource, read each property's expected type/writability, convert each value, and snapshot
    /// the original. All-or-nothing: a single error leaves the returned list empty.</description></item>
    /// <item><description><see cref="HasEffectiveChanges"/> — compare converted vs original for every
    /// patch; when all match, the handler reports <c>no_changes</c> and skips the save.</description></item>
    /// <item><description><see cref="ApplyPatches"/> — set each value in request order.</description></item>
    /// </list>
    ///
    /// <para>
    /// The value converter (<see cref="ConvertValue"/>) is the inverse of
    /// <see cref="GodotPropertySerializer"/>'s forward serializer: it takes a raw JSON token and
    /// the property's <c>Variant.Type</c> and produces a typed Variant. Primitives, vectors,
    /// colors, enums, NodePath/StringName, and resource references (by <c>res://</c> path) are
    /// supported; anything ambiguous fails with a thrown <see cref="ValueConversionException"/>.
    /// </para>
    ///
    /// <para>
    /// Editor-only (<c>#if TOOLS</c>): touches live <see cref="GodotObject"/>,
    /// <see cref="Resource"/>, <c>ClassDB</c>, and <c>Godot.Json</c>. Exercised by headless Godot
    /// smoke tests, not the binary-less xUnit host.
    /// </para>
    /// </summary>
    internal static class ResourcePropertyPatcher
    {
        /// <summary>
        /// Resolve every patch in <paramref name="patches"/> against <paramref name="root"/>,
        /// converting values and snapshotting originals. All-or-nothing: returns an empty list and
        /// a non-empty <paramref name="errors"/> list when any patch fails validation. Never
        /// throws — conversion failures are collected as structured error strings.
        /// </summary>
        /// <param name="root">The live resource to patch.</param>
        /// <param name="patches">The parsed patches (path already classified into segments, or
        /// carrying a <see cref="ResourcePropertyPatch.ParseError"/>).</param>
        /// <param name="errors">Filled with one message per failed patch; empty on full
        /// success.</param>
        /// <returns>The validated patches (empty when any error occurred).</returns>
        internal static List<ValidatedPatch> ValidatePatches(
            Resource root,
            IReadOnlyList<ResourcePropertyPatch> patches,
            out List<string> errors)
        {
            errors = new List<string>();
            var validated = new List<ValidatedPatch>();

            // Duplicate-path detection — two patches writing the same path is ambiguous and rejected
            // (the execution plan mandates unique paths).
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];

                // Grammar errors from the pure-managed parser.
                if (!patch.HasPath)
                {
                    errors.Add($"patch[{i}]: missing 'path'.");
                    continue;
                }
                if (patch.ParseError != null)
                {
                    errors.Add($"patch[{i}] path '{patch.RawPath}': {patch.ParseError}.");
                    continue;
                }

                // Duplicate path.
                if (!seenPaths.Add(patch.RawPath!))
                {
                    errors.Add($"patch[{i}] path '{patch.RawPath}': duplicate path (each patch must target a unique path).");
                    continue;
                }

                var vp = new ValidatedPatch(patch);
                try
                {
                    ResolveAndConvert(root, vp);
                    validated.Add(vp);
                }
                catch (PatchException e)
                {
                    errors.Add($"patch[{i}] path '{patch.RawPath}': {e.Message}.");
                }
            }

            if (errors.Count > 0)
                return new List<ValidatedPatch>();

            return validated;
        }

        /// <summary>
        /// Resolve a patch's segments against <paramref name="root"/> and populate
        /// <see cref="ValidatedPatch.Target"/>, <see cref="ValidatedPatch.FinalSegment"/>,
        /// <see cref="ValidatedPatch.ExpectedType"/>, <see cref="ValidatedPatch.ExpectedClassName"/>,
        /// <see cref="ValidatedPatch.ConvertedValue"/>, <see cref="ValidatedPatch.OriginalValue"/>.
        /// Throws <see cref="PatchException"/> on any resolution/conversion failure.
        /// </summary>
        static void ResolveAndConvert(Resource root, ValidatedPatch vp)
        {
            var segments = vp.Source.Segments;
            if (segments.Count == 0)
                throw new PatchException("path has no segments");

            // Walk all segments except the last; each intermediate segment must resolve to a
            // traversable object (a GodotObject sub-resource, or a Dictionary/Array container).
            GodotObject current = root;
            for (int s = 0; s < segments.Count - 1; s++)
            {
                var seg = segments[s];
                var next = Traverse(current, seg);
                if (next == null)
                    throw new PatchException(
                        $"intermediate segment '{seg}' resolved to null (cannot traverse further)");
                current = next;
            }

            vp.Target = current;
            vp.FinalSegment = segments[segments.Count - 1];

            // Read the property metadata for the final segment so we know the expected type.
            ReadPropertyInfo(vp, out var propertyExists);

            // Capture the original value BEFORE conversion (needed for no-op detection).
            vp.OriginalValue = ReadCurrentValue(vp);

            // Convert the raw value to the expected Variant type.
            var rawValue = vp.Source.RawValue;
            try
            {
                vp.ConvertedValue = ConvertValue(rawValue, vp.ExpectedType, vp.ExpectedClassName);
            }
            catch (ValueConversionException e)
            {
                throw new PatchException($"value conversion failed: {e.Message}");
            }
        }

        /// <summary>
        /// Traverse one intermediate segment on <paramref name="obj"/> and return the object it
        /// resolves to. Throws <see cref="PatchException"/> when the segment does not name a
        /// property or the property is not a traversable object.
        /// </summary>
        static GodotObject? Traverse(GodotObject obj, PatchPathSegment seg)
        {
            if (seg.Kind != SegmentKind.PropertyName)
                throw new PatchException(
                    $"intermediate segment '{seg}' must be a property name; indexing into a non-container is not supported");

            Variant value;
            try
            {
                value = obj.Get(seg.PropertyName!);
            }
            catch (Exception e)
            {
                throw new PatchException($"cannot read property '{seg.PropertyName}': {e.Message}");
            }

            if (value.VariantType == Variant.Type.Nil)
                throw new PatchException($"property '{seg.PropertyName}' is null (cannot traverse)");

            if (value.VariantType == Variant.Type.Object && value.As<GodotObject>() is GodotObject child)
                return child;

            throw new PatchException(
                $"property '{seg.PropertyName}' is type '{value.VariantType}' (expected an Object to traverse)");
        }

        /// <summary>
        /// Read the property list metadata for the final segment to determine the expected
        /// <see cref="Variant.Type"/> and class hint. Sets <see cref="ValidatedPatch.ExpectedType"/>
        /// and <see cref="ValidatedPatch.ExpectedClassName"/>. For index/key segments the type is
        /// read from the array/dictionary element type hint when available; otherwise it falls back
        /// to the current element's type.
        /// </summary>
        static void ReadPropertyInfo(ValidatedPatch vp, out bool propertyExists)
        {
            propertyExists = false;
            var seg = vp.FinalSegment;
            var target = vp.Target!;

            if (seg.Kind == SegmentKind.PropertyName)
            {
                // Look up the property in the class via GetPropertyList.
                var expectedType = Variant.Type.Nil;
                string? expectedClass = null;
                var found = false;
                try
                {
                    foreach (var prop in target.GetPropertyList())
                    {
                        var name = prop["name"].AsString();
                        if (name == seg.PropertyName)
                        {
                            expectedType = (Variant.Type)(int)prop["type"];
                            expectedClass = prop.TryGetValue("class_name", out var cn) ? cn.AsString() : null;
                            if (string.IsNullOrEmpty(expectedClass)) expectedClass = null;
                            found = true;
                            break;
                        }
                    }
                }
                catch
                {
                    // Property-list enumeration failures fall through to the Nil default.
                }

                if (!found)
                    throw new PatchException($"property '{seg.PropertyName}' does not exist on '{target.GetClass()}'");

                vp.ExpectedType = expectedType;
                vp.ExpectedClassName = expectedClass;
                propertyExists = true;
                return;
            }

            // Index/key segment: the target must be an Array or Dictionary. The element type is not
            // strongly declared, so read the current element's type as the expectation (or Nil when
            // the slot is empty — the converter then trusts the parsed JSON's intrinsic type).
            var container = ReadCurrentValue(vp);
            if (seg.Kind == SegmentKind.ArrayIndex)
            {
                if (container.VariantType != Variant.Type.Array)
                    throw new PatchException(
                        $"cannot index into '{seg}' — target property is not an Array");
                vp.ExpectedType = Variant.Type.Nil; // heterogeneous; converter trusts parsed type
                return;
            }

            // DictionaryKey.
            if (container.VariantType != Variant.Type.Dictionary)
                throw new PatchException(
                    $"cannot key into '{seg}' — target property is not a Dictionary");
            vp.ExpectedType = Variant.Type.Nil;
        }

        /// <summary>Read the current value at the patch's final segment.</summary>
        static Variant ReadCurrentValue(ValidatedPatch vp)
        {
            var seg = vp.FinalSegment;
            var target = vp.Target!;

            try
            {
                switch (seg.Kind)
                {
                    case SegmentKind.PropertyName:
                        return target.Get(seg.PropertyName!);
                    case SegmentKind.ArrayIndex:
                        var arr = target.Get(segmentsPropertyNameOf(vp)).AsGodotArray();
                        return seg.ArrayIndex >= 0 && seg.ArrayIndex < arr.Count
                            ? arr[seg.ArrayIndex]
                            : default;
                    case SegmentKind.DictionaryKey:
                        var dict = target.Get(segmentsPropertyNameOf(vp)).AsGodotDictionary();
                        return dict.ContainsKey(seg.DictionaryKey)
                            ? dict[seg.DictionaryKey!]
                            : default;
                }
            }
            catch
            {
                // Reading a missing index/key is not fatal — return Nil.
            }
            return default;
        }

        /// <summary>
        /// For index/key final segments, the parent property name is the penultimate segment
        /// (which must be a PropertyName). For a property-name final segment, the segment itself
        /// is the property. Used by <see cref="ReadCurrentValue"/> to find the container.
        /// </summary>
        static string segmentsPropertyNameOf(ValidatedPatch vp)
        {
            var segs = vp.Source.Segments;
            if (segs.Count < 2)
                throw new PatchException("internal: index/key segment without a parent property");
            var parent = segs[segs.Count - 2];
            if (parent.Kind != SegmentKind.PropertyName)
                throw new PatchException(
                    $"parent of '{vp.FinalSegment}' must be a property name (nested containers are not supported)");
            return parent.PropertyName!;
        }

        /// <summary>
        /// Apply all validated patches in request order. Each assignment is a single
        /// <c>Set</c>/index/dict write. Call this only after <see cref="ValidatePatches"/> returned
        /// a non-empty list with no errors. Never throws on a property write failure (Godot logs a
        /// warning); index/dict writes that throw are left to the caller to catch.
        /// </summary>
        internal static void ApplyPatches(List<ValidatedPatch> patches)
        {
            foreach (var vp in patches)
            {
                var seg = vp.FinalSegment;
                var target = vp.Target!;

                switch (seg.Kind)
                {
                    case SegmentKind.PropertyName:
                        target.Set(seg.PropertyName!, vp.ConvertedValue);
                        break;

                    case SegmentKind.ArrayIndex:
                        {
                            var parentName = segmentsPropertyNameOf(vp);
                            var arr = target.Get(parentName).AsGodotArray();
                            // Grow the array to fit the index (Godot arrays are resizeable).
                            while (arr.Count <= seg.ArrayIndex)
                                arr.Add(default(Variant));
                            arr[seg.ArrayIndex] = vp.ConvertedValue;
                            target.Set(parentName, arr);
                            break;
                        }

                    case SegmentKind.DictionaryKey:
                        {
                            var parentName = segmentsPropertyNameOf(vp);
                            var dict = target.Get(parentName).AsGodotDictionary();
                            dict[seg.DictionaryKey!] = vp.ConvertedValue;
                            target.Set(parentName, dict);
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// True when at least one patch's converted value differs from its original. When false,
        /// the handler should report <c>no_changes</c> and skip the save. Uses Godot's
        /// <c>Variant.Operator.Equal</c> for value equality (covers vectors, colors, arrays).
        /// </summary>
        internal static bool HasEffectiveChanges(List<ValidatedPatch> patches)
        {
            foreach (var vp in patches)
            {
                var equal = VariantEqualityComparer.Equals(vp.ConvertedValue, vp.OriginalValue);
                if (!equal) return true;
            }
            return false;
        }

        // --- value conversion -------------------------------------------------------

        /// <summary>
        /// Convert a raw JSON token (<paramref name="rawJson"/>) to the Godot Variant type the
        /// target property expects. Throws <see cref="ValueConversionException"/> on a mismatch.
        /// When <paramref name="expectedType"/> is <see cref="Variant.Type.Nil"/> (heterogeneous
        /// container element), the parsed JSON's intrinsic type is used directly.
        /// </summary>
        internal static Variant ConvertValue(string? rawJson, Variant.Type expectedType, string? expectedClassName)
        {
            // null JSON → Nil variant (clears the property).
            if (rawJson == null)
                return default;

            // Parse the raw JSON token into a Variant via Godot's JSON parser.
            Variant parsed;
            var parseErr = Json.ParseString(rawJson, out parsed);
            if (parseErr != Error.Ok)
                throw new ValueConversionException($"value is not valid JSON: '{rawJson}'");

            // Heterogeneous container element — trust the parsed type.
            if (expectedType == Variant.Type.Nil)
                return parsed;

            return CoerceToType(parsed, expectedType, expectedClassName);
        }

        /// <summary>
        /// Coerce <paramref name="parsed"/> (a Variant from <c>Json.ParseString</c>) to
        /// <paramref name="target"/>. Throws <see cref="ValueConversionException"/> on ambiguity or
        /// type mismatch. This is the explicit, deterministic converter the execution plan mandates
        /// (no silent value-changing coercion).
        /// </summary>
        static Variant CoerceToType(Variant parsed, Variant.Type target, string? expectedClassName)
        {
            switch (target)
            {
                case Variant.Type.Bool:
                    if (parsed.VariantType == Variant.Type.Bool) return parsed;
                    throw TypeMismatch("bool", parsed);

                case Variant.Type.Int:
                    if (parsed.VariantType == Variant.Type.Int) return parsed;
                    // Allow a float that is a whole number → int.
                    if (parsed.VariantType == Variant.Type.Float)
                    {
                        var d = parsed.AsDouble();
                        if (d == Math.Truncate(d))
                            return Variant.From((long)d);
                    }
                    throw TypeMismatch("int", parsed);

                case Variant.Type.Float:
                    if (parsed.VariantType == Variant.Type.Float) return parsed;
                    if (parsed.VariantType == Variant.Type.Int)
                        return Variant.From((double)parsed.AsInt64());
                    throw TypeMismatch("float", parsed);

                case Variant.Type.String:
                    if (parsed.VariantType == Variant.Type.String) return parsed;
                    // Allow numbers/bools → string (common for enum-by-name-as-number or label fields).
                    return Variant.From(parsed.ToString());

                case Variant.Type.StringName:
                    if (parsed.VariantType == Variant.Type.String) return Variant.From(new StringName(parsed.AsString()));
                    if (parsed.VariantType == Variant.Type.StringName) return parsed;
                    throw TypeMismatch("StringName", parsed);

                case Variant.Type.NodePath:
                    if (parsed.VariantType == Variant.Type.String) return Variant.From(new NodePath(parsed.AsString()));
                    if (parsed.VariantType == Variant.Type.NodePath) return parsed;
                    throw TypeMismatch("NodePath", parsed);

                case Variant.Type.Vector2:
                    return ToVector2(parsed);
                case Variant.Type.Vector2I:
                    return ToVector2I(parsed);
                case Variant.Type.Vector3:
                    return ToVector3(parsed);
                case Variant.Type.Vector3I:
                    return ToVector3I(parsed);
                case Variant.Type.Vector4:
                    return ToVector4(parsed);
                case Variant.Type.Vector4I:
                    return ToVector4I(parsed);
                case Variant.Type.Color:
                    return ToColor(parsed);
                case Variant.Type.Rect2:
                    return ToRect2(parsed);
                case Variant.Type.Quaternion:
                    return ToQuaternion(parsed);
                case Variant.Type.Plane:
                    return ToPlane(parsed);
                case Variant.Type.Object:
                    return ToObject(parsed, expectedClassName);
                case Variant.Type.Array:
                    if (parsed.VariantType == Variant.Type.Array) return parsed;
                    throw TypeMismatch("Array", parsed);
                case Variant.Type.Dictionary:
                    if (parsed.VariantType == Variant.Type.Dictionary) return parsed;
                    throw TypeMismatch("Dictionary", parsed);
                default:
                    // For types not explicitly handled (Rect2I, Transform2D, Basis, Transform3D,
                    // Aabb, Projection, RID, Callable, Signal, Packed*Array), reject — these are
                    // rare on writable resource properties and ambiguous to coerce from JSON.
                    throw new ValueConversionException(
                        $"target Variant type '{target}' is not supported for JSON coercion in v1");
            }
        }

        static double[] ExpectNumberArray(Variant parsed, int expectedCount, string typeName)
        {
            if (parsed.VariantType != Variant.Type.Array)
                throw TypeMismatch(typeName, parsed);
            var arr = parsed.AsGodotArray();
            if (arr.Count != expectedCount)
                throw new ValueConversionException(
                    $"{typeName} expects a {expectedCount}-element array; got {arr.Count}");
            var nums = new double[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                var el = arr[i];
                if (el.VariantType == Variant.Type.Float)
                    nums[i] = el.AsDouble();
                else if (el.VariantType == Variant.Type.Int)
                    nums[i] = el.AsInt64();
                else
                    throw new ValueConversionException(
                        $"{typeName} element[{i}] must be a number; got '{el.VariantType}'");
            }
            return nums;
        }

        static Variant ToVector2(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 2, "Vector2");
            return Variant.From(new Vector2((float)n[0], (float)n[1]));
        }

        static Variant ToVector2I(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 2, "Vector2I");
            return Variant.From(new Vector2I((int)n[0], (int)n[1]));
        }

        static Variant ToVector3(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 3, "Vector3");
            return Variant.From(new Vector3((float)n[0], (float)n[1], (float)n[2]));
        }

        static Variant ToVector3I(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 3, "Vector3I");
            return Variant.From(new Vector3I((int)n[0], (int)n[1], (int)n[2]));
        }

        static Variant ToVector4(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 4, "Vector4");
            return Variant.From(new Vector4((float)n[0], (float)n[1], (float)n[2], (float)n[3]));
        }

        static Variant ToVector4I(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 4, "Vector4I");
            return Variant.From(new Vector4I((int)n[0], (int)n[1], (int)n[2], (int)n[3]));
        }

        static Variant ToColor(Variant parsed)
        {
            // Color is [r, g, b, a] (a optional, defaults 1). Mirrors GodotPropertySerializer.
            if (parsed.VariantType != Variant.Type.Array)
                throw TypeMismatch("Color", parsed);
            var arr = parsed.AsGodotArray();
            if (arr.Count < 3 || arr.Count > 4)
                throw new ValueConversionException(
                    $"Color expects a 3- or 4-element array [r,g,b(,a)]; got {arr.Count}");
            float r = (float)Num(arr[0]), g = (float)Num(arr[1]), b = (float)Num(arr[2]);
            float a = arr.Count == 4 ? (float)Num(arr[3]) : 1f;
            return Variant.From(new Color(r, g, b, a));
        }

        static Variant ToRect2(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 4, "Rect2");
            return Variant.From(new Rect2((float)n[0], (float)n[1], (float)n[2], (float)n[3]));
        }

        static Variant ToQuaternion(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 4, "Quaternion");
            return Variant.From(new Quaternion((float)n[0], (float)n[1], (float)n[2], (float)n[3]));
        }

        static Variant ToPlane(Variant parsed)
        {
            var n = ExpectNumberArray(parsed, 4, "Plane");
            return Variant.From(new Plane((float)n[0], (float)n[1], (float)n[2], (float)n[3]));
        }

        /// <summary>
        /// Convert a JSON value to an Object reference. The only supported shape is
        /// <c>{"resource_path": "res://..."}</c> (or a bare <c>"res://..."</c> string) which loads
        /// the resource via <c>ResourceLoader.Load</c>. <c>null</c> clears the reference. Any other
        /// shape is rejected — type-name injection is not supported (execution plan).
        /// </summary>
        static Variant ToObject(Variant parsed, string? expectedClassName)
        {
            if (parsed.VariantType == Variant.Type.Nil)
                return default;

            string? resPath = null;
            if (parsed.VariantType == Variant.Type.String)
            {
                resPath = parsed.AsString();
            }
            else if (parsed.VariantType == Variant.Type.Dictionary)
            {
                var dict = parsed.AsGodotDictionary();
                if (dict.ContainsKey("resource_path"))
                    resPath = dict["resource_path"].AsString();
            }

            if (string.IsNullOrWhiteSpace(resPath))
                throw new ValueConversionException(
                    "object reference must be a res:// path string or {\"resource_path\":\"res://...\"}");

            if (!ResourceLoader.Exists(resPath))
                throw new ValueConversionException($"referenced resource does not exist: '{resPath}'");

            Resource loaded;
            try
            {
                loaded = ResourceLoader.Load(resPath);
            }
            catch (Exception e)
            {
                throw new ValueConversionException(
                    $"failed to load referenced resource '{resPath}': {e.Message}");
            }

            if (loaded == null)
                throw new ValueConversionException(
                    $"ResourceLoader.Load returned null for '{resPath}'");

            // When the property declares a class hint, verify the loaded resource is compatible.
            if (!string.IsNullOrEmpty(expectedClassName)
                && !ClassDB.IsParentClass(expectedClassName!, loaded.GetClass()))
            {
                throw new ValueConversionException(
                    $"referenced resource '{resPath}' is type '{loaded.GetClass()}', expected '{expectedClassName}'");
            }

            return Variant.From(loaded);
        }

        static double Num(Variant v)
        {
            if (v.VariantType == Variant.Type.Float) return v.AsDouble();
            if (v.VariantType == Variant.Type.Int) return v.AsInt64();
            throw new ValueConversionException($"expected a number; got '{v.VariantType}'");
        }

        static ValueConversionException TypeMismatch(string targetName, Variant parsed)
            => new ValueConversionException(
                $"cannot convert value of type '{parsed.VariantType}' to {targetName}");
    }

    /// <summary>
    /// Thrown when a value cannot be converted to the target Variant type without ambiguity
    /// (execution-plan error code <c>value_type_mismatch</c>). Caught by the validator and surfaced
    /// as a per-patch error string.
    /// </summary>
    internal sealed class ValueConversionException : Exception
    {
        public ValueConversionException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when a patch path cannot be resolved (unknown property, non-traversable intermediate,
    /// bad index/key) — execution-plan error code <c>patch_invalid</c>. Caught by the validator and
    /// surfaced as a per-patch error string.
    /// </summary>
    internal sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    /// <summary>
    /// Compare two <see cref="Variant"/>s for equality using Godot's native equality operator,
    /// which covers scalars, vectors, colors, arrays, and dictionaries. Used by no-op detection.
    /// </summary>
    internal static class VariantEqualityComparer
    {
        public static bool Equals(Variant a, Variant b)
        {
            // Two Nils are equal.
            if (a.VariantType == Variant.Type.Nil && b.VariantType == Variant.Type.Nil)
                return true;
            // Different types are not equal (except int/float numeric equality).
            if (a.VariantType != b.VariantType)
            {
                // Allow int==float numeric comparison (e.g. a JSON 1 vs a stored 1.0).
                if (IsNumeric(a.VariantType) && IsNumeric(b.VariantType))
                    return Math.Abs(a.AsDouble() - b.AsDouble()) < double.Epsilon;
                return false;
            }
            try
            {
                // Use Godot's Variant equality (Operator.Equal).
                var result = a.OperatorEqual(b);
                return result.AsBool();
            }
            catch
            {
                return false;
            }
        }

        static bool IsNumeric(Variant.Type t) => t == Variant.Type.Int || t == Variant.Type.Float;
    }
}
#endif
