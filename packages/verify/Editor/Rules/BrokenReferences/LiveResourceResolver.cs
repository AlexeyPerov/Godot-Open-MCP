#if TOOLS
#nullable enable
using Godot;

namespace GodotOpenMcp.Verify.Rules.BrokenReferences
{
    /// <summary>
    /// Production <see cref="IResourceResolver"/>: delegates to Godot's <c>ResourceLoader.Exists</c>
    /// (path resolution) and <c>ResourceUid.Singleton.HasId</c> (uid table lookup). Editor-only
    /// (<c>#if TOOLS</c>) because both APIs live in the engine; the scanner stays pure-managed and
    /// injects this through <see cref="BrokenReferencesRule"/>'s constructor in
    /// <see cref="Core.VerifyRunner.RegisterDefaults"/>.
    ///
    /// <para>
    /// <b>Why <c>ResourceLoader.Exists</c> and not <c>DirAccess.FileExists</c>:</b> a path may point at a
    /// <c>.tscn</c> that is itself fine, or at a <c>.gd</c> script — <c>ResourceLoader.Exists</c> honors
    /// the same importer/cache the editor uses, so it matches what the gate actually sees. It also covers
    /// the rare case of a <c>.remap</c> indirection in exported builds, though the verify gate only ever
    /// runs in-editor.
    /// </para>
    ///
    /// <para>
    /// <b>Uid resolution:</b> Godot's managed binding (<see cref="ResourceUid"/>) exposes uids as
    /// <c>Int64</c> internal ids, not the <c>uid://&lt;base32&gt;</c> text form written into
    /// <c>.tscn</c>. The conversion is <see cref="ResourceUid.TextToId"/>; existence is checked with
    /// <see cref="ResourceUidInstance.HasId"/> on the singleton. A deregistered uid (the target was
    /// deleted) returns <c>false</c> — exactly the broken-ref signal. <c>TextToId</c> is wrapped
    /// defensively because malformed text throws; the resolver treats an unparseable uid as missing
    /// rather than letting it crash the scan.
    /// </para>
    /// </summary>
    public sealed class LiveResourceResolver : IResourceResolver
    {
        /// <summary>Singleton — the resolver is stateless, so one instance serves every scan.</summary>
        public static readonly LiveResourceResolver Instance = new();

        private LiveResourceResolver() { }

        public bool PathExists(string? resPath)
        {
            if (string.IsNullOrWhiteSpace(resPath)) return false;
            // ResourceLoader.Exists handles res:// paths only; any other scheme is not a loadable
            // resource reference from the scanner's point of view.
            if (!resPath!.StartsWith("res://")) return false;
            return ResourceLoader.Exists(resPath);
        }

        public bool UidExists(string? uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return false;

            // Pass the token through WITH its `uid://` scheme. Godot's ResourceUID.text_to_id starts by
            // rejecting anything that does not begin with "uid://" and returns INVALID_ID (-1) — stripping
            // the prefix first made this method return false for every uid in the project. That silently
            // disabled the false-positive guard the broken-reference and missing-script rules depend on:
            // a reference whose `path=` is stale but whose `uid=` is still live (the normal state after
            // Godot relocates an asset) was reported as a hard Error, failing the gate under Enforce.
            var token = uid!.Trim();
            if (!token.StartsWith("uid://", System.StringComparison.Ordinal)) return false;

            // ResourceUid works in the Int64 id space. text_to_id does not throw — it returns InvalidId
            // for a malformed token — but the try/catch is retained as a cheap guard against a future
            // binding change, and a corrupt uid in a .tscn surfaces as a broken-ref issue, not a crash.
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

            // HasId returns false for a deregistered uid without throwing (unlike GetIdPath).
            return ResourceUid.Singleton.HasId(id);
        }
    }
}
#endif
