#nullable enable

namespace GodotOpenMcp.Verify.Rules.MaterialsShaderHealth
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="MaterialsShaderHealthRule"/>. Codes are part of the
    /// stable API surface — MCP responses, the capability catalog, and the gate delta all match on the
    /// <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). Per-instance detail (which
    /// material, which shader path, which include) lives in <see cref="Core.VerifyIssue.Evidence"/>, not in
    /// the code.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>Materials</c> + <c>ShaderAnalysis</c> rules — but the fidelity is
    /// <c>adapt</c> for the missing-shader / builtin / duplicate / unused detection patterns and
    /// <c>greenfield</c> for the Godot-specific <c>.gdshader</c> include resolution (Unity's shader rules
    /// parse <c>Fallback</c> directives, variant explosion, and SRP/GPU-instancing signals — none of which
    /// have a Godot twin in v1; see <c>specs/execution/P14/P14.3.md</c>). Intentional deltas for v1:
    /// <list type="bullet">
    ///   <item><b>No SRP-batcher / GPU-instancing / render-queue signals.</b> Unity's renderer-specific
    ///     signals have no Godot analogue (Godot's rendering pipeline differs); per the spec they are
    ///     skipped.</item>
    ///   <item><b>No material-variant / variant-chain signals.</b> Godot has no material-variant system in
    ///     v1.</item>
    ///   <item><b><c>orphan_shader_include</c> is greenfield.</b> Godot <c>.gdshader</c> <c>#include</c>
    ///     resolution is Godot-specific (Unity has no twin).</item>
    ///   <item><b><c>missing_shader</c> maps to the future <c>reassign_missing_shader</c> fix.</c> The fix
    ///     itself lands in a later phase (P13.3 pattern); the code is reserved now with empty
    ///     <c>fixIds</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The codes share the <c>materials_</c> prefix (mirroring the <c>project_</c> prefix P14.1 set and the
    /// <c>scene_</c> prefix P14.2 set) so an agent recognizes the family. The spec's freeze table listed
    /// unprefixed signal names, but the implementation precedent across P14.1/P14.2 is a family prefix. The
    /// <c>materials_shader_health</c> rule surfaces via <c>scan_paths</c> / <c>scan_all</c> /
    /// <c>validate_edit</c> / the gate delta — no new MCP tool (see <c>specs/execution/P14/README.md</c>
    /// "No new tools").
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A <c>ShaderMaterial</c> whose <c>shader = ExtResource("id")</c> resolves to nothing — the id is
        /// dangling (never declared), or the declared <c>[ext_resource]</c> path does not exist on disk AND
        /// its <c>uid://</c> is not registered. The shader failed to load, so the material falls back to the
        /// pink error shader (a silent render break). Adapted from Unity's <c>missing_shader</c>. Severity
        /// is <c>Error</c>: it is a render-integrity break, not cruft. The material path, the shader id,
        /// and the unresolved target are carried in evidence.
        ///
        /// <para>
        /// <b>Overlap note:</b> the same broken <c>[ext_resource]</c> may also be flagged by the
        /// <c>broken_references</c> rule as <c>broken_scene_reference</c>. The two rules surface it under
        /// different framings (generic broken ref vs. materials-specific shader slot) with different
        /// future fixes; the gate delta keys them distinctly, so both may appear on the same asset.
        /// </para>
        /// </summary>
        public const string MissingShader = "materials_missing_shader";

        /// <summary>
        /// A <c>StandardMaterial3D</c> / <c>ORMMaterial3D</c> whose serialized body carries no property
        /// overrides — a freshly-created, never-configured material using only builtin defaults (apart from
        /// optional <c>resource_name</c> identity). Informational cruft. Adapted from Unity's
        /// <c>builtin_shader</c>. Severity is <c>Warning</c>. The material path and the resource type are
        /// carried in evidence.
        /// </summary>
        public const string BuiltinShaderOnly = "materials_builtin_shader_only";

        /// <summary>
        /// A <c>.gdshader</c> whose <c>#include "..."</c> directive resolves to no file on disk. Godot 4.x
        /// resolves <c>res://</c> includes and includes relative to the shader file (or the global
        /// shader-include search path, which offline cannot be queried); an include that does not resolve
        /// fails to compile. Greenfield for Godot (Unity has no twin). Severity is <c>Warning</c>: offline
        /// resolution may false-positive on a relative include that lives only in the global include search
        /// path. The shader path, the include target, and the resolution basis are carried in evidence.
        /// </summary>
        public const string OrphanShaderInclude = "materials_orphan_shader_include";

        /// <summary>
        /// Two <c>.tres</c> materials whose normalized property sets are identical (same type, same resolved
        /// shader, same property values with file-local <c>ExtResource</c> ids normalized to their
        /// <c>res://</c> targets). Cruft — one is redundant. Adapted from Unity's
        /// <c>duplicate_material</c>. Severity is <c>Warning</c>. Scan-only (Full mode): the check needs
        /// the full material set as context. The material path, the duplicate count, and the sibling paths
        /// are carried in evidence.
        /// </summary>
        public const string DuplicateMaterial = "materials_duplicate_material";

        /// <summary>
        /// A <c>.tres</c> material not referenced by any scene/resource/script in the scanned subtree (no
        /// other file's text mentions its <c>res://</c> path or <c>uid://</c>). An orphaned asset. Adapted
        /// from Unity's <c>unused_material</c> (which uses renderer references; Godot's analogue reuses the
        /// P13.1 reverse-edge text scan). Severity is <c>Warning</c>. Scan-only (Full mode): the reverse-edge
        /// scan is expensive (reads every reference-bearing file). The material path and (when known) its
        /// uid are carried in evidence.
        /// </summary>
        public const string UnusedMaterial = "materials_unused_material";
    }
}
