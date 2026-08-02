#nullable enable

namespace GodotOpenMcp.Verify.Rules.ScriptAudit
{
    /// <summary>
    /// Stable issue-code constants emitted by <see cref="ScriptAuditRule"/>. Codes are part of the stable
    /// API surface — MCP responses, the capability catalog, and the gate delta all match on the
    /// <c>ruleId|issueCode</c> tuple (<c>packages/verify/AGENTS.md</c>). Per-instance detail (which script,
    /// which class name, which scene) lives in <see cref="Core.VerifyIssue.Evidence"/>, not in the code.
    ///
    /// <para>
    /// Adapted from Unity Open MCP's <c>MissingReferences</c> rule's script-class subset — but the fidelity
    /// is <c>adapt</c> for the class-name-vs-file mismatch detection (Unity keys its check off the
    /// MonoBehaviour's <c>m_Script</c> GUID resolved to a C# type via reflection) and <c>greenfield</c> for
    /// the Godot-specific <c>class_name</c> GDScript parsing and <c>.cs</c> file-name class resolution
    /// (Unity has no GDScript twin and resolves C# types through the compiled assembly). Intentional deltas
    /// for v1 (see <c>specs/execution/P14/P14.4.md</c>):
    /// <list type="bullet">
    ///   <item><b>No <c>missing_method</c> / <c>type_mismatch</c> / <c>duplicate_component</c>.</b> Unity's
    ///     component-model signals have no Godot analogue (Godot has no Unity component model); per the spec
    ///     they are skipped.</item>
    ///   <item><b>C# class resolution is heuristic.</b> Without compiling, the <c>.cs</c> class name is
    ///     inferred from the <c>public [partial] class X</c> declaration, falling back to the file-name stem
    ///     (Godot convention: <c>Player.cs</c> → <c>Player</c>). Uncertain resolutions are Warning, not
    ///     Error.</item>
    ///   <item><b>All three codes are Warning.</b> These are advisory: a script without <c>class_name</c> is
    ///     valid Godot; a mismatch may be intentional; a duplicate <c>class_name</c> is a latent load break
    ///     but not a guaranteed one.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The codes share the <c>script_</c> prefix (mirroring the <c>project_</c> prefix P14.1 set, the
    /// <c>scene_</c> prefix P14.2 set, and the <c>materials_</c> prefix P14.3 set) so an agent recognizes
    /// the family. The <c>script_audit</c> rule surfaces via <c>scan_paths</c> / <c>scan_all</c> /
    /// <c>validate_edit</c> / the gate delta — no new MCP tool (see <c>specs/execution/P14/README.md</c>
    /// "No new tools").
    /// </para>
    /// </summary>
    internal static class IssueCodes
    {
        /// <summary>
        /// A <c>.tscn</c>/<c>.tres</c> header records <c>script_class="X"</c> but the resolved script file
        /// declares a different class — the <c>.gd</c>'s <c>class_name</c> or the <c>.cs</c>'s
        /// <c>public [partial] class</c> (with a file-name-stem fallback). The Godot analog of Unity's
        /// MonoBehaviour whose <c>m_Script</c> GUID resolves to a type whose name differs from the recorded
        /// class. The node silently loses the intended typed behavior. Severity is <c>Warning</c>: offline
        /// C# resolution is heuristic (file-name) without compile, so a false positive is possible; and a
        /// mismatch may be intentional (a refactor in progress). The asset path, the recorded class, the
        /// resolved class, and the script path are carried in evidence.
        ///
        /// <para>
        /// <b>Relationship to <c>missing_scripts</c> (P3.3):</b> <c>missing_scripts</c> fires when the
        /// script's <c>[ext_resource]</c> does not resolve (file gone). This code fires when the file
        /// <i>exists</i> but its class name no longer matches what the scene recorded — a distinct failure
        /// mode the file-resolution rule cannot see.
        /// </para>
        /// </summary>
        public const string ClassMismatch = "script_class_mismatch";

        /// <summary>
        /// A <c>.gd</c> script attached to a node/resource (via <c>script = ExtResource("id")</c>) declares
        /// no <c>class_name</c>. A class_name-less script is valid Godot but cannot be registered as a
        /// global type, cannot be referenced via <c>preload</c> by name, and is invisible to the
        /// "attach script" type picker — limiting editor ergonomics. Informational cruft, not an integrity
        /// break. Greenfield for Godot (Unity has no GDScript twin; C# scripts are always named types).
        /// Severity is <c>Warning</c>. Emitted once per attached script (de-duplicated by script path across
        /// the scan, so a script attached in many scenes surfaces once on its own <c>.gd</c> path). The
        /// script path is carried in evidence.
        ///
        /// <para>
        /// <b>"Attached" qualifier:</b> only scripts actually referenced through <c>script = ExtResource</c>
        /// in a scanned scene/resource are flagged. A class_name-less <c>.gd</c> that nothing attaches is
        /// not flagged (it may be a base class, a library, or an entrypoint), bounding noise to scripts in
        /// use.
        /// </para>
        /// </summary>
        public const string MissingClassName = "script_missing_class_name";

        /// <summary>
        /// Two <c>.gd</c> files declare the same <c>class_name X</c>. Godot refuses to load one of them
        /// (the duplicate registration is a load-time error) — a latent break. Adapted from Unity's
        /// duplicate-script-class signal (Unity reports it as a compile error; Godot surfaces it at load).
        /// Greenfield for the GDScript parsing. Severity is <c>Warning</c>: a duplicate may be intentional
        /// during a rename/move in progress, and which file Godot picks is deterministic but
        /// version-dependent. Scan-only (<c>Validate</c>/<c>Full</c> mode): the check needs the full
        /// <c>.gd</c> set as context. C# class collisions are <b>not</b> flagged here — they need the
        /// compiled assembly to resolve reliably, which is out of scope for an offline text rule. The
        /// class name, the duplicate count, and the sibling paths are carried in evidence.
        /// </summary>
        public const string CyclicClassName = "script_cyclic_class_name";
    }

    /// <summary>
    /// Values placed in <c>Evidence["kind"]</c> to distinguish script-audit failure modes. Kept internal
    /// because the stable surface is the issue codes in <see cref="IssueCodes"/>. Mirrors the
    /// <c>EvidenceKinds</c> pattern the sibling P14 rules use.
    /// </summary>
    internal static class EvidenceKinds
    {
        public const string ClassMismatch = "class_mismatch";
        public const string MissingClassName = "missing_class_name";
        public const string CyclicClassName = "cyclic_class_name";
    }
}
