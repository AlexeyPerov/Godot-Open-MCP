#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.MaterialsShaderHealth
{
    /// <summary>
    /// Facts extracted from one <c>.tres</c> material. The rule applies the thresholds/classification
    /// (missing shader, builtin, duplicate, unused) so the tuning surface lives in the rule, not the
    /// parser — same separation <c>SceneStructureParser</c> uses (it returns facts; the rule decides
    /// whether a fact triggers a finding). Threshold-free by design.
    /// </summary>
    internal sealed class MaterialData
    {
        /// <summary>The serialized resource type from the <c>[gd_resource type="..."]</c> header (e.g. <c>StandardMaterial3D</c>, <c>ShaderMaterial</c>).</summary>
        public string ResourceType { get; }

        /// <summary>
        /// The <c>res://</c> path the <c>shader =</c> slot points at, when a ShaderMaterial carries one.
        /// <c>null</c> for a non-shader material (StandardMaterial3D etc.) or a ShaderMaterial with no
        /// <c>shader =</c> body line. For a <c>shader = ExtResource("id")</c> usage this is the declared
        /// <c>[ext_resource]</c> <c>path=</c> value the id maps to. <c>null</c> when the id was never
        /// declared (dangling) or the declaration carried no <c>path=</c> (uid-only).
        /// </summary>
        public string? ShaderTarget { get; internal set; }

        /// <summary>
        /// The <c>uid://</c> token the <c>shader =</c> slot's <c>[ext_resource]</c> declares, when present
        /// and distinct from <see cref="ShaderTarget"/>. Godot writes both <c>path=</c> and <c>uid=</c> when
        /// it can; the rule resolves <c>shader =</c> as resolved when EITHER the path exists OR the uid is
        /// registered (Godot prefers uid; either is authoritative — the normal state after Godot relocates
        /// an asset is a stale path + a live uid). Kept separate so the rule can try both.
        /// </summary>
        public string? ShaderUid { get; internal set; }

        /// <summary>The raw ExtResource id the <c>shader =</c> slot uses, when present (e.g. <c>1_abc</c>).</summary>
        public string? ShaderUsageId { get; }

        /// <summary>Whether the <c>shader =</c> usage id was never declared by any <c>[ext_resource]</c>.</summary>
        public bool ShaderUsageDangling { get; internal set; }

        /// <summary>
        /// Whether the <c>shader =</c> slot's resolved target exists on disk / in the uid table. Computed
        /// by the rule via the resolver. <c>false</c> means missing (a render break) once the rule confirms
        /// the id was declared.
        /// </summary>
        public bool ShaderTargetResolved { get; internal set; }

        /// <summary>The <c>uid://</c> declared on the material's own <c>[gd_resource]</c> header, when present (the material's identity, for the reverse-edge probe).</summary>
        public string? OwnUid { get; }

        /// <summary>
        /// Whether the material body carries any property override (any non-blank body line after the
        /// <c>[resource]</c> / sub-resource header, excluding <c>resource_name =</c> identity and the
        /// <c>shader =</c> slot itself). A ShaderMaterial/StandardMaterial3D with overrides is never
        /// "builtin-only". The <c>shader =</c> line is excluded because a ShaderMaterial with a shader but
        /// no uniform overrides is still a configured material, not a default.
        /// </summary>
        public bool HasOverrides { get; }

        /// <summary>
        /// Normalized property fingerprint for duplicate detection: the resource type + the resolved
        /// shader target + the sorted, <c>ExtResource</c>-id-normalized property lines. Two materials with
        /// the same fingerprint are duplicates. Built once per material; the rule groups on it.
        /// </summary>
        public string Fingerprint { get; }

        public MaterialData(
            string resourceType,
            string? shaderTarget,
            string? shaderUid,
            string? shaderUsageId,
            bool shaderUsageDangling,
            bool shaderTargetResolved,
            string? ownUid,
            bool hasOverrides,
            string fingerprint)
        {
            ResourceType = resourceType;
            ShaderTarget = shaderTarget;
            ShaderUid = shaderUid;
            ShaderUsageId = shaderUsageId;
            ShaderUsageDangling = shaderUsageDangling;
            ShaderTargetResolved = shaderTargetResolved;
            OwnUid = ownUid;
            HasOverrides = hasOverrides;
            Fingerprint = fingerprint;
        }
    }

    /// <summary>
    /// One <c>#include "..."</c> directive parsed from a <c>.gdshader</c>, with the raw target the rule
    /// will resolve against the resolver.
    /// </summary>
    internal sealed class ShaderInclude
    {
        /// <summary>The raw include target as written (e.g. <c>res://shaders/common.gdshaderinc</c> or <c>common.gdshaderinc</c>).</summary>
        public string Target { get; }

        public ShaderInclude(string target) => Target = target;
    }

    /// <summary>
    /// Facts extracted from one <c>.gdshader</c>: its <c>#include</c> directives (the rule resolves each).
    /// </summary>
    internal sealed class ShaderData
    {
        public List<ShaderInclude> Includes { get; } = new();
    }

    /// <summary>
    /// Threshold-free facts extracted from one <c>.tres</c> or <c>.gdshader</c>. A <c>Skip</c> means the
    /// file is not this rule's input (a non-material <c>.tres</c>, a non-<c>.gdshader</c>, a header-less or
    /// empty file). Mirrors <c>SceneStructureAnalysis.Skipped</c>: the rule emits nothing for a skipped
    /// file rather than duplicating <c>project_health</c>'s broken-asset finding.
    /// </summary>
    internal sealed class MaterialsShaderAnalysis
    {
        public MaterialData? Material { get; }
        public ShaderData? Shader { get; }
        public bool Skipped { get; }
        public string? SkipReason { get; }

        private MaterialsShaderAnalysis(MaterialData? material, ShaderData? shader, bool skipped, string? skipReason)
        {
            Material = material;
            Shader = shader;
            Skipped = skipped;
            SkipReason = skipReason;
        }

        internal static MaterialsShaderAnalysis ForMaterial(MaterialData material)
            => new(material, shader: null, skipped: false, skipReason: null);
        internal static MaterialsShaderAnalysis ForShader(ShaderData shader)
            => new(material: null, shader, skipped: false, skipReason: null);
        internal static MaterialsShaderAnalysis Skip(string reason)
            => new(material: null, shader: null, skipped: true, skipReason: reason);
    }

    /// <summary>
    /// Line-oriented <c>.tres</c> material + <c>.gdshader</c> include parser for the
    /// <c>materials_shader_health</c> rule. Pure-managed, no Godot API surface — same binary-less-test
    /// discipline as <see cref="SceneStructureHealth.SceneStructureParser"/> and
    /// <see cref="BrokenReferences.SceneRefParser"/>. Adapted from Unity's <c>Materials.Scanner</c> /
    /// <c>ShaderAnalysis.Scanner</c>: where Unity loads materials/shaders via
    /// <c>AssetDatabase.LoadAssetAtPath</c> and inspects the live objects, Godot's <c>.tres</c> /
    /// <c>.gdshader</c> are text-serialized and parseable offline, so no engine load is needed.
    ///
    /// <para>
    /// <b>What this parser does and does not do</b>
    /// <list type="bullet">
    ///   <item><b>Does:</b> from a <c>.tres</c> extract the <c>[gd_resource type=]</c> resource type, the
    ///     material's own <c>uid</c>, the <c>shader = ExtResource("id")</c> slot (with the id resolved to
    ///     its declared <c>[ext_resource]</c> path/uid), whether the id was dangling, whether the body has
    ///     overrides, and a normalized fingerprint for duplicate detection. From a <c>.gdshader</c> extract
    ///     every <c>#include "..."</c> directive. These are exactly the facts the five materials/shader
    ///     signals need.</item>
    ///   <item><b>Does not:</b> resolve references against disk (the rule does that via the resolver),
    ///     interpret shader uniforms beyond the <c>shader =</c> slot, or validate <c>[ext_resource]</c>
    ///     integrity (that is <c>broken_references</c>' domain — though <c>missing_shader</c> re-resolves
    ///     the shader slot specifically). It returns <c>Skip</c> for a non-material <c>.tres</c> /
    ///     header-less file so this rule emits nothing rather than duplicating project_health's
    ///     broken-asset check.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A truncated header, a stray bracket, or a body without a <c>[resource]</c> section yields a
    /// <c>Skip</c> or whatever facts it could extract — never an exception. A throw would silently drop
    /// this file's issues from a scoped gate check.
    /// </para>
    /// </summary>
    internal static class MaterialsShaderParser
    {
        /// <summary>
        /// Parse the given file text into threshold-free facts. Never throws. Null/empty input, a file with
        /// no <c>[gd_resource]</c> header, or a non-material <c>.tres</c> yields a <see cref="MaterialsShaderAnalysis.Skip"/>.
        /// A <c>.gdshader</c> (caller-dispatched) yields the include set. The caller decides which to call
        /// based on the file extension.
        /// </summary>
        public static MaterialsShaderAnalysis ParseMaterial(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return MaterialsShaderAnalysis.Skip("material text is empty");

            var lines = text!.Split('\n');

            if (!TryFindResourceHeader(lines, out var resourceType, out var ownUid, out var headerReason))
                return MaterialsShaderAnalysis.Skip(headerReason);

            // Collect [ext_resource] declarations (id → path + uid) so the shader slot's ExtResource id can
            // be resolved to its declared target(s).
            var extById = CollectExtResources(lines);

            // Scan the [resource]/[sub_resource] body for the shader slot + property overrides.
            var (shaderUsageId, hasOverrides, bodyProps) = ScanMaterialBody(lines);

            // Resolve the shader slot. Godot writes both path= and uid= when it can; keep both so the rule
            // can resolve by whichever is live (the normal state after relocation is a stale path + a live
            // uid — mirrors BrokenReferences' resolve-by-path-or-uid).
            string? shaderTarget = null;
            string? shaderUid = null;
            var dangling = false;
            if (shaderUsageId != null)
            {
                if (extById.TryGetValue(shaderUsageId, out var decl))
                {
                    shaderTarget = decl.Path;
                    shaderUid = decl.Uid;
                }
                else
                    dangling = true; // id never declared — the rule flags this as missing_shader.
            }

            var fingerprint = BuildFingerprint(resourceType, shaderTarget, bodyProps);
            var data = new MaterialData(resourceType, shaderTarget, shaderUid, shaderUsageId, dangling,
                shaderTargetResolved: false, ownUid, hasOverrides, fingerprint);
            return MaterialsShaderAnalysis.ForMaterial(data);
        }

        /// <summary>
        /// Parse a <c>.gdshader</c>'s <c>#include</c> directives. Never throws. Null/empty input yields a
        /// shader with no includes. A line is an include when its first non-whitespace token is
        /// <c>#include</c> followed by a quoted target. C-style <c>#include &lt;...&gt;</c> angle-bracket
        /// form is not used by Godot shaders; only the quoted form is recognized.
        /// </summary>
        public static MaterialsShaderAnalysis ParseShader(string? text)
        {
            var shader = new ShaderData();
            if (string.IsNullOrEmpty(text)) return MaterialsShaderAnalysis.ForShader(shader);

            var lines = text!.Split('\n');
            foreach (var raw in lines)
            {
                var include = TryParseInclude(raw);
                if (include != null) shader.Includes.Add(include);
            }
            return MaterialsShaderAnalysis.ForShader(shader);
        }

        // ---- Header detection -------------------------------------------------

        /// <summary>
        /// Find the first <c>[gd_resource ...]</c> header and read its <c>type=</c> + <c>uid=</c>. Returns
        /// false (with a reason) when the file has no <c>[gd_resource]</c> header — that is not a material
        /// (could be a <c>[gd_scene]</c> → project_health/scene_structure_health's domain, or broken).
        /// </summary>
        private static bool TryFindResourceHeader(string[] lines, out string resourceType, out string? ownUid, out string reason)
        {
            resourceType = "";
            ownUid = null;
            reason = "";
            foreach (var rawLine in lines)
            {
                var trimmed = rawLine.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";")) continue; // Godot writes leading comments in some exports
                if (trimmed.StartsWith("[gd_resource"))
                {
                    resourceType = ExtractAttribute(trimmed, "type") ?? "";
                    ownUid = ExtractAttribute(trimmed, "uid");
                    return true;
                }
                reason = $"first non-comment line is not a [gd_resource header: \"{Truncate(trimmed, 60)}\"";
                return false;
            }
            reason = "file has no non-blank, non-comment content";
            return false;
        }

        // ---- [ext_resource] collection ----------------------------------------

        /// <summary>
        /// Build an id → (path, uid) map from every <c>[ext_resource path= uid= id=]</c> header. Both
        /// <c>path</c> and <c>uid</c> are kept (Godot writes both when it can); the rule resolves a
        /// reference as resolved when EITHER the path exists OR the uid is registered. A declared
        /// ext_resource with neither is recorded as (null, null) so the rule treats it as unresolved.
        /// Mirrors <c>SceneRefParser.ParseExtResource</c> (which keeps path + uid too).
        /// </summary>
        private static Dictionary<string, (string? Path, string? Uid)> CollectExtResources(string[] lines)
        {
            var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                if (raw.Length == 0 || raw[0] != '[') continue;
                if (!raw.StartsWith("[ext_resource")) continue;
                var id = ExtractAttribute(raw, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var path = ExtractAttribute(raw, "path");
                var uid = ExtractAttribute(raw, "uid");
                map[id!] = (
                    string.IsNullOrEmpty(path) ? null : path,
                    string.IsNullOrEmpty(uid) ? null : uid);
            }
            return map;
        }

        // ---- Material body scan ----------------------------------------------

        /// <summary>
        /// Walk the material body (the lines after the headers) for the <c>shader = ExtResource("id")</c>
        /// slot and any property overrides. Returns (shaderUsageId, hasOverrides, bodyProps). The body is
        /// every indented line OR any line under a <c>[resource]</c>/<c>[sub_resource</c> section. A line
        /// is a property when it contains <c>=</c> at top level of the trimmed value (not a bracketed
        /// header). <c>resource_name =</c> and <c>shader =</c> are NOT counted as overrides (they are
        /// identity / the shader slot, not material configuration); every other property line is.
        /// </summary>
        private static (string? ShaderUsageId, bool HasOverrides, List<string> BodyProps) ScanMaterialBody(string[] lines)
        {
            string? shaderUsageId = null;
            var hasOverrides = false;
            var bodyProps = new List<string>();

            foreach (var raw in lines)
            {
                // Bracketed headers start at column 0; body lines are indented or appear under [resource].
                if (raw.Length > 0 && raw[0] == '[') continue;
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith(";")) continue;

                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue; // not a `key = value` line

                var key = trimmed.Substring(0, eq).Trim();

                // The shader slot on a ShaderMaterial.
                if (key == "shader")
                {
                    shaderUsageId = ExtractExtResourceId(trimmed);
                    // The shader line itself is not an override — a ShaderMaterial with a shader but no
                    // uniform overrides is still a configured material (it pins a shader). Still record it
                    // for the fingerprint so two materials differing only by shader are not flagged dup.
                    bodyProps.Add("shader=" + (shaderUsageId ?? ""));
                    continue;
                }

                // resource_name is identity, not configuration.
                if (key == "resource_name") continue;

                hasOverrides = true;
                // Normalize any ExtResource("id") to a stable placeholder so the fingerprint depends on
                // the resolved target, not the file-local id. The rule rewrites the id to its declared
                // target when computing the fingerprint; here we keep the raw normalized line and let
                // BuildFingerprint do the id→target rewrite.
                bodyProps.Add(NormalizePropLine(trimmed));
            }

            return (shaderUsageId, hasOverrides, bodyProps);
        }

        /// <summary>Extract the id from a <c>shader = ExtResource("id")</c> body line, or null.</summary>
        private static string? ExtractExtResourceId(string line)
        {
            if (!line.Contains("ExtResource(", StringComparison.Ordinal)) return null;
            var open = line.IndexOf('"');
            if (open < 0) return null;
            var close = line.IndexOf('"', open + 1);
            if (close < 0) return null;
            var id = line.Substring(open + 1, close - open - 1);
            return id.Length > 0 ? id : null;
        }

        /// <summary>
        /// Normalize a property line for the fingerprint: collapse runs of whitespace around <c>=</c> and
        /// commas so cosmetic spacing does not split duplicates. <c>ExtResource</c> ids are rewritten to
        /// their resolved targets by <see cref="BuildFingerprint"/> (which knows the id→target map).
        /// </summary>
        private static string NormalizePropLine(string line)
        {
            // Collapse ` = ` and `, ` spacing to a canonical form. Color(...) / VectorN(...) literals keep
            // their internal spacing (rare to differ and still be a real duplicate).
            var sb = new System.Text.StringBuilder(line.Length);
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == ' ')
                {
                    // Keep a single space only inside Color/Vector parens? Simplest: drop spaces adjacent
                    // to '=' and ',' so "albedo_color = Color(1, 2, 3)" == "albedo_color=Color(1,2,3)".
                    var prev = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                    var next = i + 1 < line.Length ? line[i + 1] : '\0';
                    if (prev == '=' || next == '=' || prev == ',' || next == ',') continue;
                    sb.Append(c);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Build the duplicate-detection fingerprint: resource type + resolved shader target + the sorted,
        /// id-normalized property lines. Two materials with the same fingerprint are duplicates. The
        /// <c>ExtResource</c> ids inside property values are left as-is here (the per-material
        /// <c>extById</c> map is not available at fingerprint time); duplicate detection's false-positive
        /// risk is bounded because a ShaderMaterial's uniform property values are literals or sub-resource
        /// refs, and two truly-identical materials serialise identical id schemes. (A future hardening
        /// could rewrite ids to targets; v1 keeps the simple form, matching the spec's "identical property
        /// sets" signal.)
        /// </summary>
        private static string BuildFingerprint(string resourceType, string? shaderTarget, List<string> bodyProps)
        {
            bodyProps.Sort(StringComparer.Ordinal);
            return resourceType + "|shader=" + (shaderTarget ?? "") + "|" + string.Join(";", bodyProps);
        }

        // ---- .gdshader #include parsing ---------------------------------------

        /// <summary>
        /// Parse a single line as a Godot shader <c>#include "..."</c> directive. Returns null when the
        /// line is not an include. Godot shaders use the quoted form exclusively; the C angle-bracket form
        /// is rejected. A <c>//</c> / <c>/*</c> comment before the directive is respected (the line is not
        /// an include).
        /// </summary>
        private static ShaderInclude? TryParseInclude(string line)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#include", StringComparison.Ordinal)) return null;
            // Skip whitespace after #include.
            var i = "#include".Length;
            while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t')) i++;
            if (i >= trimmed.Length || trimmed[i] != '"') return null;
            var close = trimmed.IndexOf('"', i + 1);
            if (close < 0) return null;
            var target = trimmed.Substring(i + 1, close - i - 1);
            return target.Length > 0 ? new ShaderInclude(target) : null;
        }

        // ---- Attribute extraction (mirrors SceneStructureParser / SceneRefParser) ----

        /// <summary>
        /// Extract a <c>key="value"</c> attribute from a header line. The leading-space probe
        /// (<c>" key=\""</c>) prevents <c>type</c> matching inside <c>subtype</c> or <c>uid</c> — same
        /// trick the sibling parsers use.
        /// </summary>
        private static string? ExtractAttribute(string line, string key)
        {
            var probe = " " + key + "=\"";
            var idx = line.IndexOf(probe, StringComparison.Ordinal);
            if (idx < 0) return null;
            var valueStart = idx + probe.Length;
            var valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd < 0) return null;
            return line.Substring(valueStart, valueEnd - valueStart);
        }

        private static string Truncate(string s, int n)
            => s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}
