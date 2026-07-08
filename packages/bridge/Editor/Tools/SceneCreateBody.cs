#nullable enable
using System;

namespace GodotOpenMcp.Bridge.Editor
{
    /// <summary>
    /// Parsed request body for <c>godot_open_mcp_scene_create</c> (P2.7). Extracts the scalar fields
    /// the create handler needs straight off the raw JSON body using the same hand-rolled
    /// <c>IndexOf</c>-substring style as <see cref="NodeCreateBody"/> and <see cref="SceneOpenBody"/> —
    /// the bridge deliberately carries no typed JSON DOM dependency on the hot path
    /// (<c>packages/bridge/AGENTS.md</c> §Transport).
    ///
    /// <para>
    /// Pure-managed (no Godot API surface, no <c>#if TOOLS</c>), so the parsing logic is unit-testable
    /// in the binary-less xUnit host. The editor-only <see cref="SceneTools"/> handler constructs this
    /// from the raw body string and then branches on the parsed fields.
    /// </para>
    /// </summary>
    internal sealed class SceneCreateBody
    {
        /// <summary>
        /// <c>res://</c> path for the new scene file (e.g. <c>res://levels/level_2.tscn</c>). Required
        /// by the handler — an empty/missing path fails with <c>missing_parameter</c>. Must start with
        /// <c>res://</c> and end with <c>.tscn</c> / <c>.scn</c>.
        /// </summary>
        internal string? Path { get; private set; }

        /// <summary>
        /// Godot class name for the scene's root Node (e.g. <c>Node</c>, <c>Node2D</c>,
        /// <c>Node3D</c>). When null/empty the handler falls back to <see cref="DefaultRootType"/>.
        /// Defaults to <c>Node2D</c> per the P2.7 plan (matches the Unity scene-create default which
        /// picks a common game root; Godot's own <c>Tool_Scene.Create</c> defaults to plain
        /// <c>Node</c>, but a game root is more useful out of the box).
        /// </summary>
        internal string? RootType { get; private set; }

        /// <summary>Default <see cref="RootType"/> when the caller omits it. Per the P2.7 plan.</summary>
        internal const string DefaultRootType = "Node2D";

        /// <summary>
        /// Optional name for the root Node. When null/empty the handler derives a default from the
        /// <c>res://</c> path's filename stem (e.g. <c>level_2.tscn</c> → <c>Level2</c>) — matching the
        /// Godot editor's own new-scene naming convention.
        /// </summary>
        internal string? RootName { get; private set; }

        /// <summary>
        /// When true, overwrite a scene file that already exists at <see cref="Path"/>. Default false:
        /// the handler refuses with <c>path_exists</c> so an agent does not clobber an existing scene
        /// accidentally. Opt-in via the schema.
        /// </summary>
        internal bool Overwrite { get; private set; } = false;

        /// <summary>
        /// When true, open the newly-created scene as the active/edited scene. Default true: the typical
        /// create-then-edit workflow expects the new scene to be active immediately. An agent setting up
        /// a batch of scene files can pass <c>open: false</c> to create without switching the edited
        /// scene.
        /// </summary>
        internal bool Open { get; private set; } = true;

        /// <summary>The effective root class to instantiate. Falls back to <see cref="DefaultRootType"/>
        /// when the caller omitted <see cref="RootType"/>.</summary>
        internal string EffectiveRootType =>
            string.IsNullOrEmpty(RootType) ? DefaultRootType : RootType!;

        /// <summary>
        /// Parse <paramref name="body"/> into a <see cref="SceneCreateBody"/>. Never throws — a missing
        /// or malformed field falls back to its default. Empty/null body returns an all-default instance
        /// (null path, root type Node2D) — the handler then fails with <c>missing_parameter</c>.
        /// </summary>
        internal static SceneCreateBody Parse(string? body)
        {
            var parsed = new SceneCreateBody();
            if (string.IsNullOrEmpty(body)) return parsed;

            parsed.Path = ExtractNullableStringValue(body, "path");
            parsed.RootType = ExtractNullableStringValue(body, "root_type");
            parsed.RootName = ExtractNullableStringValue(body, "root_name");
            parsed.Overwrite = ExtractBoolValue(body, "overwrite", defaultValue: false);
            parsed.Open = ExtractBoolValue(body, "open", defaultValue: true);
            return parsed;
        }

        SceneCreateBody() { }

        // --- hand-rolled JSON scalar extraction ----------------------------------------
        //
        // Mirrors NodeCreateBody.Extract* / SceneOpenBody.Extract*: locate `"key"`, walk past the colon,
        // read the scalar. Strings are unwrapped from their quotes and unescaped for the small set of
        // JSON string escapes (\" \\ \/ \n \r \t \uXXXX); a field that is present-but-null is treated
        // as absent.

        static string? ExtractNullableStringValue(string body, string key)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return null;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;
            // null literal → absent.
            if (start + 4 <= body.Length && body.Substring(start, 4) == "null") return null;
            if (body[start] == '"')
                return SliceQuotedString(body, start);
            return null;
        }

        static bool ExtractBoolValue(string body, string key, bool defaultValue)
        {
            var quotedKey = "\"" + key + "\"";
            var idx = body.IndexOf(quotedKey, StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            var colonIdx = body.IndexOf(':', idx + quotedKey.Length);
            if (colonIdx < 0) return defaultValue;
            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return defaultValue;
            if (start + 4 <= body.Length && body.Substring(start, 4) == "true") return true;
            if (start + 5 <= body.Length && body.Substring(start, 5) == "false") return false;
            return defaultValue;
        }

        static string SliceQuotedString(string body, int start)
        {
            // start points at the opening quote.
            var sb = new System.Text.StringBuilder(body.Length - start);
            int i = start + 1;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var next = body[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i += 2; continue;
                        case '\\': sb.Append('\\'); i += 2; continue;
                        case '/': sb.Append('/'); i += 2; continue;
                        case 'n': sb.Append('\n'); i += 2; continue;
                        case 'r': sb.Append('\r'); i += 2; continue;
                        case 't': sb.Append('\t'); i += 2; continue;
                        case 'b': sb.Append('\b'); i += 2; continue;
                        case 'f': sb.Append('\f'); i += 2; continue;
                        case 'u' when i + 5 < body.Length:
                            if (int.TryParse(body.Substring(i + 2, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var code))
                                sb.Append((char)code);
                            i += 6;
                            continue;
                        default:
                            sb.Append(next); i += 2; continue;
                    }
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
