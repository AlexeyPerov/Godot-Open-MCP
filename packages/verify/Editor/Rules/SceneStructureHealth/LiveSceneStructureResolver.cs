#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace GodotOpenMcp.Verify.Rules.SceneStructureHealth
{
    /// <summary>
    /// Production <see cref="ISceneStructureResolver"/>: lists immediate children of a <c>res://</c>
    /// directory via a one-level <c>DirAccess</c> read, and checks file existence via
    /// <c>FileAccess.FileExists</c>. Editor-only (<c>#if TOOLS</c>) because both APIs live in the engine;
    /// the parser + rule stay pure-managed and inject this through the rule's constructor in
    /// <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// Identical in structure and policy to <c>ProjectHealth.LiveProjectHealthResolver</c> — the
    /// scene-structure rule needs the same one-level directory walk to reach every <c>.tscn</c> in a
    /// scoped subtree, with the same internal-skip set (<c>.godot</c>, VCS, <c>node_modules</c>) and the
    /// same deterministic dirs-first-then-files ordering. See that class for the full rationale; only the
    /// entry type (<see cref="SceneFolderEntry"/>) differs.
    /// </para>
    /// </summary>
    public sealed class LiveSceneStructureResolver : ISceneStructureResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveSceneStructureResolver Instance = new();

        /// <summary>
        /// Internal-skip set: directories the listing NEVER surfaces. Mirrors
        /// <c>LiveProjectHealthResolver.InternalSkipDirs</c> and <c>project-index.ts</c>'s
        /// <c>INTERNAL_SKIP_DIRS</c> exactly so the live rule and the offline reader agree on what counts
        /// as user project state.
        /// </summary>
        private static readonly HashSet<string> InternalSkipDirs = new(StringComparer.Ordinal)
        {
            ".godot",
            ".git",
            ".hg",
            ".svn",
            "node_modules",
        };

        private LiveSceneStructureResolver() { }

        public bool FileExists(string? resPath)
        {
            if (string.IsNullOrWhiteSpace(resPath)) return false;
            if (!resPath!.StartsWith("res://", StringComparison.Ordinal)) return false;
            return FileAccess.FileExists(resPath);
        }

        public IReadOnlyList<SceneFolderEntry> ListDirectory(string? resDir)
        {
            // Same normalization as LiveProjectHealthResolver: strip the scheme, trim slashes, re-add one
            // trailing slash. Avoids the "res://" → "res:/" trap.
            var raw = string.IsNullOrWhiteSpace(resDir) ? "res://" : resDir!;
            if (!raw.StartsWith("res://", StringComparison.Ordinal)) return Array.Empty<SceneFolderEntry>();
            var relative = raw.Substring("res://".Length).Trim('/');
            var root = relative.Length == 0 ? "res://" : "res://" + relative + "/";

            using var dir = DirAccess.Open(root);
            if (dir == null) return Array.Empty<SceneFolderEntry>();

            var dirs = new List<SceneFolderEntry>();
            var files = new List<SceneFolderEntry>();
            dir.ListDirBegin();
            string? name;
            while ((name = dir.GetNext()) != null && name.Length > 0)
            {
                if (name == "." || name == "..") continue;
                if (InternalSkipDirs.Contains(name)) continue;

                if (dir.CurrentIsDir())
                    dirs.Add(new SceneFolderEntry(root + name + "/", name, isDirectory: true));
                else
                    files.Add(new SceneFolderEntry(root + name, name, isDirectory: false));
            }
            dir.ListDirEnd();

            // Directories first, then files, each group sorted by name (ordinal) for deterministic
            // issue emission.
            dirs.Sort(CompareByName);
            files.Sort(CompareByName);

            var combined = new List<SceneFolderEntry>(dirs.Count + files.Count);
            combined.AddRange(dirs);
            combined.AddRange(files);
            return combined;
        }

        private static int CompareByName(SceneFolderEntry a, SceneFolderEntry b)
            => string.CompareOrdinal(a.Name, b.Name);
    }
}
#endif
