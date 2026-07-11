#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Pure-string validation/normalization of <c>res://</c> file paths and <c>uid://</c> identifiers
    /// shared by the resource tool family (<c>resource_find</c> / <c>resource_get_data</c> in P4.1, and
    /// the P4.2/P4.3 mutators later). Lives outside the <c>#if TOOLS</c> guard — like
    /// <see cref="NodePathNormalizer"/> — so the scheme/extension/traversal checks, the part most prone
    /// to off-by-one bugs, are unit-testable in the plain xUnit host with no live Godot filesystem.
    ///
    /// <para>
    /// Godot addresses every project resource under the <c>res://</c> virtual root and assigns a stable
    /// <c>uid://</c> identifier to imported assets. The resource tools accept either form and resolve a
    /// <c>uid://</c> to its canonical <c>res://</c> path on the main thread (<c>ResourceUid</c>). These
    /// helpers only enforce the textual invariants — scheme prefix, supported extensions, and no parent
    /// traversal — without touching any Godot API.
    /// </para>
    ///
    /// <para>
    /// <b>Adapted from Godot-MCP's <c>ResPathNormalizer</c></b> (behavior reference): the directory
    /// trailing-slash convention and the parent-traversal rejection are lifted from there. The
    /// <c>res://</c> file-path + extension validation and the <c>uid://</c> shape checks are
    /// greenfield for this tool family.
    /// </para>
    /// </summary>
    internal static class ResourcePathNormalizer
    {
        /// <summary>The Godot resource scheme prefix.</summary>
        public const string ResScheme = "res://";

        /// <summary>The Godot UID scheme prefix.</summary>
        public const string UidScheme = "uid://";

        /// <summary>
        /// Supported on-disk resource extensions for direct lookup. Godot's text format is
        /// <c>.tres</c>; binary is <c>.res</c>. Other resource-bearing extensions (<c>.tscn</c>,
        /// <c>.scn</c>, imported textures) are out of P4.1 scope — the find/get-data contract is
        /// <c>.tres</c>/<c>.res</c> only.
        /// </summary>
        public static readonly string[] ResourceExtensions = { ".tres", ".res" };

        /// <summary>
        /// True when <paramref name="path"/> is a non-empty <c>res://</c> path.
        /// </summary>
        public static bool IsResPath(string? path)
            => !string.IsNullOrEmpty(path) && path!.StartsWith(ResScheme, StringComparison.Ordinal);

        /// <summary>
        /// True when <paramref name="value"/> is a non-empty <c>uid://</c> identifier.
        /// </summary>
        public static bool IsUid(string? value)
            => !string.IsNullOrEmpty(value) && value!.StartsWith(UidScheme, StringComparison.Ordinal);

        /// <summary>
        /// True when <paramref name="path"/> ends with a supported resource extension
        /// (<c>.tres</c> or <c>.res</c>, case-insensitive).
        /// </summary>
        public static bool HasResourceExtension(string path)
        {
            foreach (var ext in ResourceExtensions)
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Validate that <paramref name="path"/> is a <c>res://</c> file path, throwing
        /// <see cref="ArgumentException"/> (with <paramref name="paramName"/>) otherwise. Returns the
        /// trimmed path on success. Rejects the bare scheme (root is a directory, not a file),
        /// directory paths (trailing slash), parent-traversal segments (<c>..</c>), and unsupported
        /// extensions (only <c>.tres</c>/<c>.res</c> in P4.1). Does NOT touch the filesystem — existence
        /// is the handler's job via <c>ResourceLoader.Exists</c>.
        /// </summary>
        public static string RequireResFilePath(string? path, string paramName)
        {
            var p = (path ?? string.Empty).Trim();
            if (!IsResPath(p))
                throw new ArgumentException(
                    $"Path must be a '{ResScheme}' path; got '{path}'.", paramName);
            if (p == ResScheme)
                throw new ArgumentException(
                    $"Path must name a file under '{ResScheme}', not the bare project root '{ResScheme}'.", paramName);
            if (p.EndsWith("/", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Path must be a file path, not a directory; got '{path}'.", paramName);
            RejectParentTraversal(p, path, paramName);
            if (!HasResourceExtension(p))
                throw new ArgumentException(
                    $"Path must end with '.tres' or '.res'; got '{path}'.", paramName);
            return p;
        }

        /// <summary>
        /// Try to validate <paramref name="path"/> as a <c>res://</c> file path. Returns the trimmed
        /// path on success or an error message on failure (no exception). Mirrors the checks in
        /// <see cref="RequireResFilePath"/> for the handler's structured-error path.
        /// </summary>
        public static bool TryRequireResFilePath(string? path, out string normalized, out string? error)
        {
            try
            {
                normalized = RequireResFilePath(path, nameof(path));
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                normalized = string.Empty;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Validate that <paramref name="uid"/> is a <c>uid://</c> identifier, throwing
        /// <see cref="ArgumentException"/> otherwise. Returns the trimmed value on success. Does NOT
        /// verify the uid exists — that is the handler's job via <c>ResourceUid.HasId</c>.
        /// </summary>
        public static string RequireUid(string? uid, string paramName)
        {
            var u = (uid ?? string.Empty).Trim();
            if (!IsUid(u))
                throw new ArgumentException(
                    $"UID must be a '{UidScheme}' identifier; got '{uid}'.", paramName);
            return u;
        }

        /// <summary>
        /// Try to validate <paramref name="uid"/> as a <c>uid://</c> identifier. Returns the trimmed
        /// value on success or an error message on failure (no exception).
        /// </summary>
        public static bool TryRequireUid(string? uid, out string normalized, out string? error)
        {
            try
            {
                normalized = RequireUid(uid, nameof(uid));
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                normalized = string.Empty;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reject a <c>..</c> path segment so that <c>res://a/../b.tres</c> cannot bypass a
        /// string-equality guard. Bounded by Godot's <c>res://</c> sandbox so this is not a traversal
        /// vulnerability — it locks the path-equality guards. A bare <c>.</c> is harmless and allowed.
        /// </summary>
        static void RejectParentTraversal(string normalized, string? original, string paramName)
        {
            foreach (var segment in normalized.Split('/'))
            {
                if (segment == "..")
                    throw new ArgumentException(
                        $"Path must not contain a '..' parent-directory segment; got '{original}'.", paramName);
            }
        }
    }
}
