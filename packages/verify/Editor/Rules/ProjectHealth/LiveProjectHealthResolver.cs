#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace GodotOpenMcp.Verify.Rules.ProjectHealth
{
    /// <summary>
    /// Production <see cref="IProjectHealthResolver"/>: lists immediate children of a <c>res://</c>
    /// directory via a one-level <c>DirAccess</c> read, and checks file existence via
    /// <c>FileAccess.FileExists</c>. Editor-only (<c>#if TOOLS</c>) because both APIs live in the
    /// engine; the parser + rule stay pure-managed and inject this through the rule's constructor in
    /// <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// <b>One-level listing, not a recursive walk.</b> The rule recurses itself (it tracks depth and
    /// de-dupes overlapping scoped roots), so the resolver returns only the immediate children of the
    /// requested directory — the same contract the live <c>filesystem_list</c> tool and
    /// <c>project-index.ts</c>'s <c>listProjectDirectoryOffline</c> expose. This keeps the resolver
    /// stateless and the recursion policy in the rule (where the depth threshold lives).
    /// </para>
    ///
    /// <para>
    /// <b>Internal-skip set.</b> The walk never surfaces <c>.godot</c> (the engine's import cache),
    /// <c>.git</c>/<c>.hg</c>/<c>.svn</c> (VCS internals), or <c>node_modules</c> (a JS toolchain a Godot
    /// project may sit beside) — the same set <c>project-index.ts</c>'s <c>INTERNAL_SKIP_DIRS</c> uses,
    /// so a scan does not flag engine/import/VCS internals as user project cruft. Hidden dotfile
    /// directories (other than the named internals) ARE surfaced, because a user-named hidden folder
    /// (e.g. <c>.archive</c>) is legitimate project state this rule should be able to flag.
    /// </para>
    ///
    /// <para>
    /// <b>Deterministic ordering.</b> Directory entries are returned first (each with a trailing
    /// <c>/</c>), then file entries, each group sorted by name via ordinal compare — the same ordering
    /// <c>filesystem_list</c> and <c>project-index.ts</c> produce. Pinning to ordinal (not
    /// <c>StringComparer.CurrentCulture</c>) makes the output deterministic across locales so two scans
    /// of the same tree emit issues in the same order (gate-delta stability).
    /// </para>
    /// </summary>
    public sealed class LiveProjectHealthResolver : IProjectHealthResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveProjectHealthResolver Instance = new();

        private static readonly string[] Empty = Array.Empty<string>();

        /// <summary>
        /// Internal-skip set: directories the listing NEVER surfaces, regardless of scope. Mirrors
        /// <c>project-index.ts</c>'s <c>INTERNAL_SKIP_DIRS</c> exactly so the live rule and the offline
        /// reader agree on what counts as user project state.
        /// </summary>
        private static readonly HashSet<string> InternalSkipDirs = new(StringComparer.Ordinal)
        {
            ".godot",
            ".git",
            ".hg",
            ".svn",
            "node_modules",
        };

        private LiveProjectHealthResolver() { }

        public bool FileExists(string? resPath)
        {
            if (string.IsNullOrWhiteSpace(resPath)) return false;
            if (!resPath!.StartsWith("res://", StringComparison.Ordinal)) return false;
            return FileAccess.FileExists(resPath);
        }

        public IReadOnlyList<ProjectFolderEntry> ListDirectory(string? resDir)
        {
            // Normalize the res:// directory the same way LiveImportHealthResolver does: strip the
            // scheme, trim slashes, re-add exactly one trailing slash. A naive TrimEnd('/') on the
            // project root "res://" yields "res:" and re-appending "/" gives "res:/" — which then fails
            // the StartsWith("res://") guard and returned an empty list. "res://" is the documented
            // whole-project scope value.
            var raw = string.IsNullOrWhiteSpace(resDir) ? "res://" : resDir!;
            if (!raw.StartsWith("res://", StringComparison.Ordinal)) return Array.Empty<ProjectFolderEntry>();
            var relative = raw.Substring("res://".Length).Trim('/');
            var root = relative.Length == 0 ? "res://" : "res://" + relative + "/";

            using var dir = DirAccess.Open(root);
            // A null dir (bad path, permissions, or a file-shaped path passed as a directory) yields an
            // empty list — the rule treats an empty listing as "no children", not an error.
            if (dir == null) return Array.Empty<ProjectFolderEntry>();

            var dirs = new List<ProjectFolderEntry>();
            var files = new List<ProjectFolderEntry>();
            dir.ListDirBegin();
            string? name;
            while ((name = dir.GetNext()) != null && name.Length > 0)
            {
                // Skip the navigation entries Godot yields. Current="." and parent=".." are always present.
                if (name == "." || name == "..") continue;
                // Internal-skip ALWAYS wins — never surface .godot/, VCS internals, or node_modules.
                if (InternalSkipDirs.Contains(name)) continue;

                if (dir.CurrentIsDir())
                {
                    dirs.Add(new ProjectFolderEntry(root + name + "/", name, isDirectory: true));
                }
                else
                {
                    files.Add(new ProjectFolderEntry(root + name, name, isDirectory: false));
                }
            }
            dir.ListDirEnd();

            // Directories first, then files, each group sorted by name (ordinal). The rule's
            // empty/uid-only/large/deep checks and the asset checks all key off this ordering for
            // deterministic issue emission.
            dirs.Sort(CompareByName);
            files.Sort(CompareByName);

            var combined = new List<ProjectFolderEntry>(dirs.Count + files.Count);
            combined.AddRange(dirs);
            combined.AddRange(files);
            return combined;
        }

        private static int CompareByName(ProjectFolderEntry a, ProjectFolderEntry b)
            => string.CompareOrdinal(a.Name, b.Name);
    }
}
#endif
