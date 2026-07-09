#if TOOLS
#nullable enable
using System.Collections.Generic;
using Godot;

namespace GodotOpenMcp.Verify.Rules.ImportHealth
{
    /// <summary>
    /// Production <see cref="IImportHealthResolver"/>: delegates to Godot's <c>FileAccess.FileExists</c>
    /// (source-asset existence), <c>ResourceUid.Singleton.HasId</c> (uid registration), and a recursive
    /// <c>DirAccess</c> walk (sidecar enumeration). Editor-only (<c>#if TOOLS</c>) because all three APIs
    /// live in the engine; the parser + rule stay pure-managed and inject this through the rule's
    /// constructor in <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// <b>Why <c>FileAccess.FileExists</c> and not <c>ResourceLoader.Exists</c> for the orphan check:</b>
    /// the orphan signal is "the source asset file is gone from disk", not "the engine can load it". A
    /// <c>.import</c> sidecar's <c>source=</c> points at a real file on disk (a <c>.png</c>, a
    /// <c>.gdshader</c>); if that file is gone, the sidecar is orphan regardless of whether Godot still
    /// has a cached import in <c>.godot/imported/</c>. <c>FileAccess.FileExists</c> answers exactly that.
    /// (<c>ResourceLoader.Exists</c> would return true for a cached import even after the source was
    /// deleted, masking the orphan.)
    /// </para>
    ///
    /// <para>
    /// <b>Uid resolution:</b> mirrors <see cref="BrokenReferences.LiveResourceResolver"/> — the
    /// <c>uid://</c> text is converted via <c>ResourceUid.TextToId</c> (wrapped defensively because
    /// malformed text throws) and existence is <c>ResourceUid.Singleton.HasId</c>. Kept even though the
    /// duplicate-uid check is primarily cross-sidecar, because an unregistered uid is a useful signal
    /// for a future rule and the resolver contract exposes it.
    /// </para>
    ///
    /// <para>
    /// <b>Sidecar enumeration:</b> a recursive <c>DirAccess</c> walk collecting every file ending in
    /// <c>.import</c>. The <c>.godot/</c> directory is skipped (Godot never writes user sidecars there;
    /// it holds the import cache, which is not part of this rule's domain). The walk is bounded by the
    /// project tree; a scoped scan passes the scoped root, not <c>res://</c>, so a checkpoint over a
    /// single scene does not enumerate the whole tree.
    /// </para>
    /// </summary>
    public sealed class LiveImportHealthResolver : IImportHealthResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveImportHealthResolver Instance = new();

        private LiveImportHealthResolver() { }

        public bool FileExists(string? resPath)
        {
            if (string.IsNullOrWhiteSpace(resPath)) return false;
            if (!resPath!.StartsWith("res://")) return false;
            return FileAccess.FileExists(resPath);
        }

        public bool UidExists(string? uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            var token = uid!;
            const string scheme = "uid://";
            if (token.StartsWith(scheme))
                token = token.Substring(scheme.Length);
            if (string.IsNullOrEmpty(token)) return false;

            long id;
            try
            {
                id = ResourceUid.TextToId(token);
            }
            catch
            {
                return false;
            }
            return ResourceUid.Singleton.HasId(id);
        }

        public IReadOnlyList<string> EnumerateImportSidecars(string? resRoot)
        {
            var root = string.IsNullOrWhiteSpace(resRoot) ? "res://" : resRoot!.TrimEnd('/') + "/";
            if (!root.StartsWith("res://")) return Empty;

            var results = new List<string>();
            // DirAccess.Open opens the directory; the recursive walk uses ListDirBegin/ListDirEnd per
            // nesting level. A null dir (bad path, permissions) yields an empty list — the rule then
            // finds no sidecars and contributes no orphan/uid issues for that subtree, rather than
            // crashing the scan.
            using var dir = DirAccess.Open(root);
            if (dir == null) return Empty;
            Walk(dir, root, results);
            return results;
        }

        private static readonly string[] Empty = System.Array.Empty<string>();

        /// <summary>
        /// Recursive directory walk. Collects <c>.import</c> files and recurses into sub-directories,
        /// skipping <c>.godot</c> (the engine's internal cache, not a user sidecar source).
        /// </summary>
        private static void Walk(DirAccess dir, string currentDirRes, List<string> results)
        {
            dir.ListDirBegin();
            string? name;
            while ((name = dir.GetNext()) != null && name.Length > 0)
            {
                // Skip the navigation entries Godot yields. Current="." and parent=".." are always
                // present; hidden files are not relevant here.
                if (name == "." || name == "..") continue;

                if (dir.CurrentIsDir())
                {
                    // Skip the engine cache and hidden directories.
                    if (name == ".godot" || name.StartsWith(".")) continue;
                    var subDirRes = currentDirRes + name + "/";
                    using var sub = DirAccess.Open(subDirRes);
                    if (sub != null) Walk(sub, subDirRes, results);
                    continue;
                }

                if (name.EndsWith(".import", System.StringComparison.OrdinalIgnoreCase))
                    results.Add(currentDirRes + name);
            }
            dir.ListDirEnd();
        }
    }
}
#endif
