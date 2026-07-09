#nullable enable
using System;
using System.Collections.Generic;

namespace GodotOpenMcp.Verify.Rules.ImportHealth
{
    /// <summary>
    /// A parsed <c>.import</c> sidecar declaration. Godot writes a sidecar next to every imported
    /// source asset (e.g. <c>res://Sprites/Player.png</c> → <c>res://Sprites/Player.png.import</c>)
    /// as an INI-style file with a <c>[remap]</c> header. The fields a verify rule needs:
    /// <list type="bullet">
    ///   <item><see cref="Source"/> — the <c>source=</c> value, the <c>res://</c> path to the source
    ///     asset this sidecar describes. An orphan sidecar is one whose source no longer exists.</item>
    ///   <item><see cref="Uid"/> — the <c>uid=</c> value (Godot 4.4+ writes it into the sidecar as
    ///     <c>uid://...</c>). A duplicate-uid finding needs this.</item>
    ///   <item><see cref="Path"/> — the <c>path=</c> value, the engine-internal cache path (e.g.
    ///     <c>res://.godot/imported/...png-...png</c>). Present so an agent can locate what Godot
    ///     cached, but not used to decide orphan-ness — the source is the source of truth.</item>
    ///   <item><see cref="Importer"/> — the <c>importer=</c> value (e.g. <c>texture</c>,
    ///     <c>gdshader</c>). Carried in evidence so a human/agent can tell what kind of import the
    ///     sidecar governed.</item>
    /// </list>
    /// Any of <see cref="Source"/>/<see cref="Path"/>/<see cref="Uid"/>/<see cref="Importer"/> may be
    /// null/empty — older Godot versions or hand-edited sidecars omit fields. The rule treats missing
    /// fields defensively rather than flagging structural sidecar malformations (a separate concern).
    /// </summary>
    internal sealed class ImportFileDecl
    {
        /// <summary>The <c>res://</c> path of the sidecar file itself (e.g. <c>res://Sprites/Player.png.import</c>).</summary>
        public string SidecarPath { get; }

        /// <summary>
        /// The <c>source=</c> value — the source asset this sidecar describes (e.g.
        /// <c>res://Sprites/Player.png</c>). An orphan sidecar is one whose source is gone. Null/empty
        /// when the sidecar omits <c>source=</c> (a malformed sidecar; not this rule's domain).
        /// </summary>
        public string? Source { get; }

        /// <summary>
        /// The <c>path=</c> value — the engine-internal cache path Godot imports INTO. Null/empty when
        /// absent. Not used to decide orphan-ness; carried in evidence only.
        /// </summary>
        public string? Path { get; }

        /// <summary>
        /// The <c>uid=</c> value as written in the sidecar (<c>uid://...</c>). Null/empty when Godot
        /// did not assign a uid to this resource (older versions, or types that do not get uids). A
        /// duplicate-uid finding compares this value across the scanned sidecars.
        /// </summary>
        public string? Uid { get; }

        /// <summary>The <c>importer=</c> value (e.g. <c>texture</c>). Null/empty when absent.</summary>
        public string? Importer { get; }

        public ImportFileDecl(string sidecarPath, string? source, string? path, string? uid, string? importer)
        {
            SidecarPath = sidecarPath;
            Source = source;
            Path = path;
            Uid = uid;
            Importer = importer;
        }
    }

    /// <summary>
    /// Line-oriented parser for Godot <c>.import</c> sidecar files. Pure-managed, no Godot API surface
    /// — the same binary-less-test discipline as <see cref="BrokenReferences.SceneRefParser"/>. Greenfield
    /// for Godot: Unity has no <c>.import</c>-sidecar concept (Unity's import metadata lives in the
    /// <c>.meta</c> + the <c>Library/</c> cache); Godot's sidecar is its own INI-shaped artifact.
    ///
    /// <para>
    /// Godot writes the sidecar as an INI file with a single <c>[remap]</c> section:
    ///
    /// <code>
    /// [remap]
    /// importer="texture"
    /// type="CompressedTexture2D"
    /// uid="uid://abcdefghij"
    /// path="res://.godot/imported/abcd.png-abc.png"
    /// source="res://Sprites/Player.png"
    /// </code>
    ///
    /// The key/value order is not guaranteed across versions and some keys are absent for some importer
    /// types (e.g. an <c>image</c> importer writes no <c>uid=</c> in 4.3). This parser tolerates any
    /// order and any subset of keys, and it only reads keys inside the <c>[remap]</c> section (other
    /// sections like <c>[deps]</c> are not relevant to orphan/uid health).
    /// </para>
    ///
    /// <para>
    /// <b>Robustness contract (<c>IVerifyRule</c>):</b> the parser never throws on ordinary malformed
    /// input. A sidecar without a <c>[remap]</c> section, an unbalanced quote, or a truncated line
    /// yields whatever it can — never an exception — so a malformed sidecar never drops its file's
    /// issues from a scoped gate check.
    /// </para>
    /// </summary>
    internal static class ImportFileParser
    {
        /// <summary>
        /// Parse the sidecar text into a declaration. Never throws. Null/empty input yields a declaration
        /// with null fields (the rule then treats the sidecar as structurally malformed and contributes no
        /// orphan/uid issues for it — a separate concern).
        /// </summary>
        public static ImportFileDecl Parse(string sidecarPath, string? text)
        {
            string? source = null;
            string? path = null;
            string? uid = null;
            string? importer = null;

            if (string.IsNullOrEmpty(text)) return new ImportFileDecl(sidecarPath, null, null, null, null);

            var lines = text!.Split('\n');
            // Track whether we are inside the [remap] section. Godot writes other sections ([deps],
            // [params]) after [remap]; we only want the remap header's keys. A new bracketed header
            // closes the current section.
            var inRemap = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                // Bracketed section header. Godot INI files use [section].
                if (line[0] == '[')
                {
                    inRemap = line.StartsWith("[remap]", StringComparison.Ordinal);
                    continue;
                }

                if (!inRemap) continue;

                // key="value" inside [remap]. Parse the key (up to '='), then the double-quoted value.
                var eq = line.IndexOf('=');
                if (eq <= 0) continue; // a line with no '=' is not a key/value pair; skip silently

                var key = line.Substring(0, eq).Trim();
                var valueRaw = line.Substring(eq + 1).Trim();
                var value = Unquote(valueRaw);

                switch (key)
                {
                    case "source":
                        source = value;
                        break;
                    case "path":
                        path = value;
                        break;
                    case "uid":
                        uid = value;
                        break;
                    case "importer":
                        importer = value;
                        break;
                }
            }

            return new ImportFileDecl(sidecarPath, source, path, uid, importer);
        }

        /// <summary>
        /// Strip a leading and trailing double-quote if present. Godot always double-quotes sidecar
        /// values; a value that is not quoted is returned as-is (defensive — a hand-edited sidecar may
        /// omit quotes). Returns null when the unquoted value is empty so the caller can treat it as
        /// "field absent".
        /// </summary>
        private static string? Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            return value.Length == 0 ? null : value;
        }
    }
}
