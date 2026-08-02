#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace GodotOpenMcp.Verify.Rules.AnimationAnalysis
{
    /// <summary>
    /// Production <see cref="IAnimationAnalysisResolver"/>: lists immediate children of a <c>res://</c>
    /// directory via a one-level <c>DirAccess</c> read, and resolves <c>res://</c> paths / <c>uid://</c>
    /// tokens via <c>FileAccess.FileExists</c> / <c>ResourceUID.Has</c>. Editor-only (<c>#if TOOLS</c>)
    /// because every API it touches lives in the engine; the parser + rule stay pure-managed and inject
    /// this through the rule's constructor in <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// <b>Directory walk + internal skip set:</b> identical in structure and policy to
    /// <c>ScriptAudit.LiveScriptAuditResolver</c> /
    /// <c>MaterialsShaderHealth.LiveMaterialsShaderResolver</c> /
    /// <c>SceneStructureHealth.LiveSceneStructureResolver</c> /
    /// <c>ProjectHealth.LiveProjectHealthResolver</c> — same one-level <c>DirAccess</c> read, same
    /// dirs-first-then-files ordering, same (<c>.godot</c>, VCS, <c>node_modules</c>) skip set. See those
    /// classes for the rationale. Path/uid resolution mirrors <c>MaterialsShaderHealth</c> /
    /// <c>BrokenReferences</c> (Godot prefers uid; either path-or-uid resolving is authoritative).
    /// </para>
    /// </summary>
    public sealed class LiveAnimationAnalysisResolver : IAnimationAnalysisResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveAnimationAnalysisResolver Instance = new();

        /// <summary>
        /// Internal-skip set: directories the listing NEVER descends into. Mirrors the sibling live
        /// resolvers and <c>project-index.ts</c>'s <c>INTERNAL_SKIP_DIRS</c>.
        /// </summary>
        private static readonly HashSet<string> InternalSkipDirs = new(StringComparer.Ordinal)
        {
            ".godot",
            ".git",
            ".hg",
            ".svn",
            "node_modules",
        };

        private LiveAnimationAnalysisResolver() { }

        public IReadOnlyList<AnimationFolderEntry> ListDirectory(string? resDir)
        {
            // Same normalization as the sibling live resolvers: strip the scheme, trim slashes, re-add one
            // trailing slash. Avoids the "res://" → "res:/" trap.
            var raw = string.IsNullOrWhiteSpace(resDir) ? "res://" : resDir!;
            if (!raw.StartsWith("res://", StringComparison.Ordinal)) return Array.Empty<AnimationFolderEntry>();
            var relative = raw.Substring("res://".Length).Trim('/');
            var root = relative.Length == 0 ? "res://" : "res://" + relative + "/";

            using var dir = DirAccess.Open(root);
            if (dir == null) return Array.Empty<AnimationFolderEntry>();

            var dirs = new List<AnimationFolderEntry>();
            var files = new List<AnimationFolderEntry>();
            dir.ListDirBegin();
            string? name;
            while ((name = dir.GetNext()) != null && name.Length > 0)
            {
                if (name == "." || name == "..") continue;
                if (InternalSkipDirs.Contains(name)) continue;

                if (dir.CurrentIsDir())
                    dirs.Add(new AnimationFolderEntry(root + name + "/", name, isDirectory: true));
                else
                    files.Add(new AnimationFolderEntry(root + name, name, isDirectory: false));
            }
            dir.ListDirEnd();

            // Directories first, then files, each group sorted by name (ordinal) for deterministic issue
            // emission.
            dirs.Sort(CompareByName);
            files.Sort(CompareByName);

            var combined = new List<AnimationFolderEntry>(dirs.Count + files.Count);
            combined.AddRange(dirs);
            combined.AddRange(files);
            return combined;
        }

        public bool PathExists(string? resPath)
        {
            if (string.IsNullOrEmpty(resPath)) return false;
            if (!resPath!.StartsWith("res://", StringComparison.Ordinal)) return false;
            // FileAccess.FileExists resolves res:// against the project root — mirrors the sibling resolvers.
            return FileAccess.FileExists(resPath);
        }

        public bool UidExists(string? uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            // Keep the `uid://` scheme — see LiveImportHealthResolver.UidExists: text_to_id rejects a token
            // without it and returns InvalidId. Mirrors the sibling live resolvers' defensive wrap so a
            // malformed uid never crashes a scan.
            var token = uid!.Trim();
            if (!token.StartsWith("uid://", StringComparison.Ordinal)) return false;

            long id;
            try
            {
                id = ResourceUid.TextToId(token);
            }
            catch
            {
                return false;
            }
            if (id == ResourceUid.InvalidId) return false;
            return ResourceUid.Singleton.HasId(id);
        }

        private static int CompareByName(AnimationFolderEntry a, AnimationFolderEntry b)
            => string.CompareOrdinal(a.Name, b.Name);
    }
}
#endif
