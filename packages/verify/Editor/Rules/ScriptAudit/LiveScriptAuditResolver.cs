#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace GodotOpenMcp.Verify.Rules.ScriptAudit
{
    /// <summary>
    /// Production <see cref="IScriptAuditResolver"/>: lists immediate children of a <c>res://</c> directory
    /// via a one-level <c>DirAccess</c> read. Editor-only (<c>#if TOOLS</c>) because every API it touches
    /// lives in the engine; the parser + rule stay pure-managed and inject this through the rule's
    /// constructor in <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// <b>Directory walk + internal skip set:</b> identical in structure and policy to
    /// <c>MaterialsShaderHealth.LiveMaterialsShaderResolver</c> /
    /// <c>SceneStructureHealth.LiveSceneStructureResolver</c> /
    /// <c>ProjectHealth.LiveProjectHealthResolver</c> — same one-level <c>DirAccess</c> read, same
    /// dirs-first-then-files ordering, same (<c>.godot</c>, VCS, <c>node_modules</c>) skip set. See those
    /// classes for the rationale. The rule reads script/scene file text through its injected file reader
    /// (<c>File.ReadAllText</c> in production), so this resolver exposes only the directory walk.
    /// </para>
    /// </summary>
    public sealed class LiveScriptAuditResolver : IScriptAuditResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveScriptAuditResolver Instance = new();

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

        private LiveScriptAuditResolver() { }

        public IReadOnlyList<ScriptFolderEntry> ListDirectory(string? resDir)
        {
            // Same normalization as the sibling live resolvers: strip the scheme, trim slashes, re-add one
            // trailing slash. Avoids the "res://" → "res:/" trap.
            var raw = string.IsNullOrWhiteSpace(resDir) ? "res://" : resDir!;
            if (!raw.StartsWith("res://", StringComparison.Ordinal)) return Array.Empty<ScriptFolderEntry>();
            var relative = raw.Substring("res://".Length).Trim('/');
            var root = relative.Length == 0 ? "res://" : "res://" + relative + "/";

            using var dir = DirAccess.Open(root);
            if (dir == null) return Array.Empty<ScriptFolderEntry>();

            var dirs = new List<ScriptFolderEntry>();
            var files = new List<ScriptFolderEntry>();
            dir.ListDirBegin();
            string? name;
            while ((name = dir.GetNext()) != null && name.Length > 0)
            {
                if (name == "." || name == "..") continue;
                if (InternalSkipDirs.Contains(name)) continue;

                if (dir.CurrentIsDir())
                    dirs.Add(new ScriptFolderEntry(root + name + "/", name, isDirectory: true));
                else
                    files.Add(new ScriptFolderEntry(root + name, name, isDirectory: false));
            }
            dir.ListDirEnd();

            // Directories first, then files, each group sorted by name (ordinal) for deterministic issue
            // emission.
            dirs.Sort(CompareByName);
            files.Sort(CompareByName);

            var combined = new List<ScriptFolderEntry>(dirs.Count + files.Count);
            combined.AddRange(dirs);
            combined.AddRange(files);
            return combined;
        }

        private static int CompareByName(ScriptFolderEntry a, ScriptFolderEntry b)
            => string.CompareOrdinal(a.Name, b.Name);
    }
}
#endif
