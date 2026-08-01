#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace GodotOpenMcp.Verify.Rules.MaterialsShaderHealth
{
    /// <summary>
    /// Production <see cref="IMaterialsShaderResolver"/>: lists immediate children of a <c>res://</c>
    /// directory via a one-level <c>DirAccess</c> read, resolves paths via <c>ResourceLoader.Exists</c>
    /// / uids via <c>ResourceUid</c>, and runs the P13.1-style reverse-edge text scan for
    /// <c>unused_material</c>. Editor-only (<c>#if TOOLS</c>) because every API it touches lives in the
    /// engine; the parser + rule stay pure-managed and inject this through the rule's constructor in
    /// <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// <b>Directory walk + internal skip set:</b> identical in structure and policy to
    /// <c>SceneStructureHealth.LiveSceneStructureResolver</c> / <c>ProjectHealth.LiveProjectHealthResolver</c>
    /// — same one-level <c>DirAccess</c> read, same dirs-first-then-files ordering, same
    /// (<c>.godot</c>, VCS, <c>node_modules</c>) skip set. See those classes for the rationale.
    /// </para>
    ///
    /// <para>
    /// <b>Reverse-edge scan (<see cref="IsReferenced"/>):</b> adapted (adapt fidelity) from the offline
    /// <c>findReferencesOffline</c> in <c>mcp-server/src/offline/references.ts</c> (P13.1). It walks every
    /// <c>.tscn</c>/<c>.tres</c> under <c>res://</c> (skipping internal dirs) and reports a reference when
    /// the file text mentions the target <c>res://</c> path or <c>uid://</c> token. Re-reads per call — no
    /// cache — mirroring the MCP-server offline-read no-cache philosophy. Expensive → the rule runs it only
    /// in Full mode. The self-reference (the material's own file) is excluded by path.
    /// </para>
    /// </summary>
    public sealed class LiveMaterialsShaderResolver : IMaterialsShaderResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveMaterialsShaderResolver Instance = new();

        /// <summary>
        /// Internal-skip set: directories the listing + reverse-edge scan NEVER descend into. Mirrors the
        /// sibling live resolvers and <c>project-index.ts</c>'s <c>INTERNAL_SKIP_DIRS</c>.
        /// </summary>
        private static readonly HashSet<string> InternalSkipDirs = new(StringComparer.Ordinal)
        {
            ".godot",
            ".git",
            ".hg",
            ".svn",
            "node_modules",
        };

        /// <summary>Extensions whose text the reverse-edge scan reads (.tscn/.tres carry [ext_resource]
        /// edges; .gd/.cs carry preload/load string literals). Mirrors references.ts's scan sets.</summary>
        private static readonly HashSet<string> ReferenceScanExtensions = new(StringComparer.Ordinal)
        {
            ".tscn", ".scn", ".tres", ".res", ".gd", ".cs",
        };

        private LiveMaterialsShaderResolver() { }

        public bool PathExists(string? resPath)
        {
            if (string.IsNullOrWhiteSpace(resPath)) return false;
            if (!resPath!.StartsWith("res://", StringComparison.Ordinal)) return false;
            // ResourceLoader.Exists honors the importer/cache the editor uses, matching what the gate sees.
            return ResourceLoader.Exists(resPath);
        }

        public bool UidExists(string? uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;
            var token = uid!.Trim();
            if (!token.StartsWith("uid://", StringComparison.Ordinal)) return false;
            long id;
            try { id = ResourceUid.TextToId(token); }
            catch { return false; }
            if (id == ResourceUid.InvalidId) return false;
            return ResourceUid.Singleton.HasId(id);
        }

        public IReadOnlyList<MaterialsFolderEntry> ListDirectory(string? resDir)
        {
            // Same normalization as the sibling live resolvers: strip the scheme, trim slashes, re-add one
            // trailing slash. Avoids the "res://" → "res:/" trap.
            var raw = string.IsNullOrWhiteSpace(resDir) ? "res://" : resDir!;
            if (!raw.StartsWith("res://", StringComparison.Ordinal)) return Array.Empty<MaterialsFolderEntry>();
            var relative = raw.Substring("res://".Length).Trim('/');
            var root = relative.Length == 0 ? "res://" : "res://" + relative + "/";

            using var dir = DirAccess.Open(root);
            if (dir == null) return Array.Empty<MaterialsFolderEntry>();

            var dirs = new List<MaterialsFolderEntry>();
            var files = new List<MaterialsFolderEntry>();
            dir.ListDirBegin();
            string? name;
            while ((name = dir.GetNext()) != null && name.Length > 0)
            {
                if (name == "." || name == "..") continue;
                if (InternalSkipDirs.Contains(name)) continue;

                if (dir.CurrentIsDir())
                    dirs.Add(new MaterialsFolderEntry(root + name + "/", name, isDirectory: true));
                else
                    files.Add(new MaterialsFolderEntry(root + name, name, isDirectory: false));
            }
            dir.ListDirEnd();

            // Directories first, then files, each group sorted by name (ordinal) for deterministic
            // issue emission.
            dirs.Sort(CompareByName);
            files.Sort(CompareByName);

            var combined = new List<MaterialsFolderEntry>(dirs.Count + files.Count);
            combined.AddRange(dirs);
            combined.AddRange(files);
            return combined;
        }

        public bool IsReferenced(string? resPathOrUid, string? ownerResPath)
        {
            if (string.IsNullOrWhiteSpace(resPathOrUid)) return false;
            var target = resPathOrUid!.Trim();
            var pathTarget = target.StartsWith("res://", StringComparison.Ordinal) ? target : null;
            var uidTarget = target.StartsWith("uid://", StringComparison.Ordinal) ? target : null;
            if (pathTarget == null && uidTarget == null) return false;

            // Walk res:// for reference-bearing files and probe each for the target token. A hit means some
            // other file references this material/shader. The owner's own file is excluded (a material's
            // own [gd_resource] is not a reference TO itself).
            return ScanReverseEdges("res://", pathTarget, uidTarget, ownerResPath);
        }

        // ---- Reverse-edge scan (adapted from references.ts findReferencesOffline) ----

        /// <summary>
        /// Recursively walk <paramref name="dirRes"/> for reference-bearing files and report whether any
        /// (other than <paramref name="ownerResPath"/>) contains the target path/uid token. Re-reads files
        /// per call — no cache. Never throws: an unreadable file is skipped.
        /// </summary>
        private bool ScanReverseEdges(string dirRes, string? pathTarget, string? uidTarget, string? ownerResPath)
        {
            IReadOnlyList<MaterialsFolderEntry> entries;
            try { entries = ListDirectory(dirRes); }
            catch { return false; }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (ScanReverseEdges(entry.ResPath, pathTarget, uidTarget, ownerResPath)) return true;
                    continue;
                }

                // Only read text files that can carry references.
                var ext = GetExtension(entry.ResPath);
                if (!ReferenceScanExtensions.Contains(ext)) continue;

                // Exclude the owner's own file.
                if (ownerResPath != null && entry.ResPath == ownerResPath) continue;

                string text;
                try { text = FileAccess.GetFileAsString(entry.ResPath); }
                catch { continue; }
                if (string.IsNullOrEmpty(text)) continue;

                if (pathTarget != null && text.Contains(pathTarget, StringComparison.Ordinal)) return true;
                if (uidTarget != null && text.Contains(uidTarget, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string GetExtension(string resPath)
        {
            var slash = resPath.LastIndexOf('/');
            var leaf = slash >= 0 ? resPath.Substring(slash + 1) : resPath;
            var dot = leaf.LastIndexOf('.');
            return dot < 0 ? "" : leaf.Substring(dot);
        }

        private static int CompareByName(MaterialsFolderEntry a, MaterialsFolderEntry b)
            => string.CompareOrdinal(a.Name, b.Name);
    }
}
#endif
